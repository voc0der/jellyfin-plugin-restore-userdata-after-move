using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.UserDataRestore.Configuration;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.Core.Reporting;
using Jellyfin.Plugin.UserDataRestore.Jellyfin;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserDataRestore.ScheduledTasks;

/// <summary>
/// Finds user data Jellyfin stranded when a media path changed, and puts it back.
/// </summary>
/// <remarks>
/// <para>One task, not two. An earlier design split this into an analysis that
/// wrote a plan and an apply that consumed it, with a whole-plan reconciliation
/// pass between them to catch drift in the gap. There is no gap here: the
/// analysis is computed and acted on in the same pass, so every candidate it
/// calls ready is ready as of moments ago. A plan from yesterday is not a
/// contract to honour today — by then the library has drifted again — so the
/// plan this writes is a record of what happened, not an input to anything.</para>
/// <para>Safe to run on a schedule, which is the point: identification lags
/// stranding. Jellyfin reattaches user data across a move by provider id, but
/// only if the new item is already identified when the old one is removed, and
/// it gets exactly one attempt. A repeating run gets one every time, and picks
/// the item up as soon as its provider ids arrive. Nothing here can restore the
/// same snapshot twice. Once a target holds the recovered state the pair
/// classifies <c>already_applied</c>; if somebody edits it afterwards it
/// classifies <c>current_state_conflict</c>. Either way it is never written
/// again, so a scheduled run cannot undo a user marking something
/// unwatched.</para>
/// <para>The stranded rows are never modified or deleted. They are the only
/// remaining copy of this state, and leaving them intact is what makes a failed
/// run recoverable.</para>
/// </remarks>
public class RestoreUserDataTask : IScheduledTask
{
    // Plans are small, and five is enough history to compare a bad run against
    // the last good one. Nobody opening a plugin page has an opinion about it.
    private const int PlansKept = 5;

    // Ledgers are smaller still - a line per write - and are the thing you go
    // looking for when a plan is missing, so more of them are kept (DESIGN §8).
    private const int LedgersKept = 20;

    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationHost _applicationHost;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<RestoreUserDataTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestoreUserDataTask"/> class.
    /// </summary>
    /// <param name="dbFactory">The host's database context factory.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The only supported write path.</param>
    /// <param name="applicationHost">The application host.</param>
    /// <param name="taskManager">The task manager, for detecting a running library scan.</param>
    /// <param name="logger">The logger.</param>
    public RestoreUserDataTask(
        IDbContextFactory<JellyfinDbContext> dbFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IApplicationHost applicationHost,
        ITaskManager taskManager,
        ILogger<RestoreUserDataTask> logger)
    {
        _dbFactory = dbFactory;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _applicationHost = applicationHost;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Restore user data after move";

    /// <inheritdoc />
    public string Key => "UserDataRestore";

    /// <inheritdoc />
    public string Description =>
        "Finds played state, resume positions, favourites and ratings that Jellyfin stranded when a media path changed, and restores them onto the item that holds that media now.";

    /// <inheritdoc />
    public string Category => "Restore User Data After Move";

    /// <inheritdoc />
    /// <remarks>
    /// None. This task is only useful after the thing that moves your media has
    /// finished and the library has been rescanned, and Jellyfin cannot express
    /// "run after that" — it has no task chaining, only wall-clock and interval
    /// triggers. A default schedule would therefore be a guess at someone else's
    /// maintenance window, and a wrong guess runs mid-move, where every stranded
    /// row honestly reports as unmatchable.
    ///
    /// So it ships inert. Add a trigger in Dashboard -> Scheduled Tasks timed to
    /// land after your own pipeline, or press Run now. Repeat runs are no-ops by
    /// construction, so an over-frequent trigger costs nothing but log lines.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        ServerVersionGate.EnsureSupported(_applicationHost.ApplicationVersion);

        // Mid-scan is the worst possible moment to look. Jellyfin removes the
        // vacated items and creates their replacements in separate passes, so a
        // library caught between the two reports stranded rows as unmatchable and
        // offers targets that are about to be replaced. Waiting costs a night at
        // most; this task runs again tomorrow.
        if (LibraryScanIsRunning())
        {
            _logger.LogInformation(
                "A library scan is running, so the library is mid-change and nothing it reports can be trusted. "
                + "Skipping this run; the next scheduled one will pick it up.");
            progress.Report(100);
            return;
        }

        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("The plugin instance is not available.");
        var configuration = plugin.Configuration;
        ClearLegacyScopeOverrides(plugin, configuration);

        var scope = new LibraryScope(_libraryManager).Resolve(configuration.EligibleLibraryIds);
        var options = BuildOptions(configuration, scope);

        if (!options.IsScopeConfigured)
        {
            throw new InvalidOperationException(
                scope.LibraryIds.Count == 0
                    ? "No movie or TV libraries were found on this server, so there is nothing this plugin can recover into."
                    : "The selected libraries have no configured folders, so no recovery target can be identified.");
        }

        _logger.LogInformation(
            "Scope: {LibraryCount} {Source} libraries, {PrefixCount} folders ({Prefixes}).",
            scope.LibraryIds.Count,
            scope.Defaulted ? "auto-detected" : "selected",
            options.FinalPathPrefixes.Count,
            string.Join(", ", options.FinalPathPrefixes));

        var reader = new UserDataReader(_dbFactory);
        await reader.EnsureModelCompatibleAsync(cancellationToken).ConfigureAwait(false);

        var fingerprintBefore = await reader.FingerprintAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(5);

        var result = await AnalyzeAsync(options, reader, cancellationToken).ConfigureAwait(false);
        progress.Report(50);

        var outcomes = await ApplyAsync(plugin, result.Writes, options, reader, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(95);

        // Everything from here describes writes that have already landed, so none
        // of it takes the run's token. A cancellation arriving mid-run has done
        // its job by stopping the writes; letting it also stop the record of them
        // would leave user data changed with nothing saying what changed it, which
        // is the one outcome this whole ordering exists to prevent. The
        // cancellation is rethrown below, once the artifact is on disk.
        var fingerprintAfter = await TryFingerprintAsync(reader, CancellationToken.None).ConfigureAwait(false);

        // Ordered so the counts reach the log either way. The plan is written after
        // the writes, because it records what they did, which means a plan that
        // cannot be written would otherwise take the only account of them with it.
        var planFailure = TryWritePlan(plugin, result, options, fingerprintBefore, fingerprintAfter, outcomes);
        Report(result, outcomes, configuration.VerboseLogging);

        progress.Report(100);

        var uncertain = CountOf(outcomes, WriteOutcome.Uncertain);
        var failed = CountOf(outcomes, WriteOutcome.Failed);

        if (uncertain + failed > 0)
        {
            var stoppedBy = uncertain > 0
                ? "the save was entered and its result is unknown"
                : "it threw before writing anything";

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This run stopped at a restore that did not complete: {stoppedBy}. {CountOf(outcomes, WriteOutcome.Restored)} of {result.Writes.Count} restores had completed and {CountOf(outcomes, WriteOutcome.NotAttempted)} were not attempted. The stranded rows are untouched; the next run plans them again."),
                planFailure);
        }

        // Last, so the plan above exists first. Jellyfin reports this as a
        // cancelled task rather than a failed one, which is what it was.
        if (ApplySequence.WasCancelled(outcomes))
        {
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"Cancelled after {CountOf(outcomes, WriteOutcome.Restored)} of {result.Writes.Count} restores. The plan for this run records every one of them; the rest were not attempted and the next run plans them again.");

            throw planFailure is null
                ? new OperationCanceledException(message, cancellationToken)
                : new OperationCanceledException(message, planFailure, cancellationToken);
        }

        if (planFailure is not null)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The restores completed ({CountOf(outcomes, WriteOutcome.Restored)} restored, {CountOf(outcomes, WriteOutcome.Skipped)} skipped) but the plan for this run could not be written. The run ledger in the plans directory still records every write and its outcome; what is missing is the classification detail around them."),
                planFailure);
        }
    }

    /// <summary>
    /// Whether Jellyfin is part-way through rebuilding the library.
    /// </summary>
    /// <remarks>
    /// Matched on the task key rather than its display name, which is localized.
    /// If a future server renames the key this returns false and the run proceeds
    /// — the same behaviour as every server that has no scan running, and the
    /// classification rules still refuse to guess.
    /// </remarks>
    private bool LibraryScanIsRunning() =>
        _taskManager.ScheduledTasks.Any(worker =>
            string.Equals(worker.ScheduledTask.Key, "RefreshLibrary", StringComparison.Ordinal)
            && worker.State == TaskState.Running);

    /// <summary>
    /// Clears path settings an upgraded install may still carry from a version
    /// that had controls for them.
    /// </summary>
    /// <remarks>
    /// This runs before the scope is resolved, so a legacy value cannot reach a
    /// single write. Both settings decide which items a run may write to, and
    /// neither has been editable since 1.0.0.8 — a prefix list narrower than the
    /// library's own locations silently excludes valid targets, and a cleared
    /// path-existence flag admits items whose file is gone, which re-strands the
    /// data onto an item the next scan removes. The operator is told what was
    /// found rather than only that something was reset: it is the setting they
    /// once chose, and the reason their results are about to change.
    /// </remarks>
    private void ClearLegacyScopeOverrides(Plugin plugin, PluginConfiguration configuration)
    {
        if (!configuration.HasLegacyScopeOverrides)
        {
            return;
        }

        if (configuration.FinalPathPrefixes.Length > 0)
        {
            _logger.LogWarning(
                "Clearing {Count} final path prefix(es) saved by an older version ({Prefixes}). "
                + "The plugin has had no control for them since 1.0.0.8, and scope now comes from the selected libraries' own locations.",
                configuration.FinalPathPrefixes.Length,
                string.Join(", ", configuration.FinalPathPrefixes));
        }

        if (!configuration.RequirePathExists)
        {
            _logger.LogWarning(
                "Re-enabling the media-file check, which an older version had saved as off. "
                + "The plugin has had no control for it since 1.0.0.8, and recovering onto an item whose file is missing re-strands the data.");
        }

        configuration.FinalPathPrefixes = [];
        configuration.RequirePathExists = true;
        plugin.UpdateConfiguration(configuration);
    }

    private static AnalysisOptions BuildOptions(PluginConfiguration configuration, ResolvedLibraryScope scope) => new()
    {
        EligibleLibraryIds = scope.LibraryIds,

        // The libraries' own locations, which the server already knows and a human
        // cannot mistype. The configured list is read rather than assumed empty,
        // but ClearLegacyScopeOverrides has already guaranteed that it is: nothing
        // has offered a control for it since 1.0.0.8.
        FinalPathPrefixes = ScopeDefaults.ResolvePrefixes(configuration.FinalPathPrefixes, scope.Locations),

        // Jellyfin runs on the host's own filesystem semantics; comparing paths
        // case-sensitively on Windows would reject valid targets.
        PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
        RequirePathExists = configuration.RequirePathExists,
        NowUtc = DateTime.UtcNow,
    };

    private async Task<AnalysisResult> AnalyzeAsync(
        AnalysisOptions options,
        UserDataReader reader,
        CancellationToken cancellationToken)
    {
        var detachedRows = await reader.ReadDetachedAsync(cancellationToken).ConfigureAwait(false);
        var currentItems = new LibraryItemCollector(_libraryManager)
            .Collect(options.EligibleLibraryIds, options.RequirePathExists, cancellationToken);

        var candidates = DetachedUserDataAnalyzer.BuildCandidates(new AnalysisInput
        {
            DetachedRows = detachedRows,
            CurrentItems = currentItems,
            KnownUserIds = _userManager.GetUsersIds().ToHashSet(),
            Options = options,
        });

        var currentRows = await reader.ReadCurrentAsync(candidates.PairsToInspect, cancellationToken).ConfigureAwait(false);
        return DetachedUserDataAnalyzer.Complete(candidates, currentRows);
    }

    /// <summary>
    /// Performs the writes, returning what became of every one of them.
    /// </summary>
    /// <remarks>
    /// The returned list always has one entry per planned write, in the planned
    /// order, whether or not the run reached it. That is what makes it usable as
    /// the plan's record: an artifact that simply omitted the writes a run
    /// abandoned would read as though they had never been planned.
    /// </remarks>
    private async Task<IReadOnlyList<WriteResult>> ApplyAsync(
        Plugin plugin,
        IReadOnlyList<PlannedWrite> writes,
        AnalysisOptions options,
        UserDataReader reader,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
        {
            return [];
        }

        var writer = new UserDataWriter(_userDataManager);
        var collector = new LibraryItemCollector(_libraryManager);

        // Built now rather than reused from the analysis pass, and this is the one
        // piece of revalidation that cannot be per-write: whether a key still
        // belongs to one item is a fact about the whole catalogue, and answering it
        // costs a pass over every movie and episode on the server. Once per run is
        // affordable; once per write is not. Everything else each target is checked
        // against is read from the live item inside the loop.
        var ownership = collector.BuildKeyOwnership(cancellationToken);

        // Opened before the first write and flushed after each one, so the record
        // of a run exists while the run is still capable of failing. The plan is
        // the better artifact and this is the one that survives the plan not
        // being written at all.
        using var ledger = OpenLedger(plugin);
        var completed = 0;

        // Safety invariant 8 lives in ApplySequence, where it can be tested by
        // injecting a failure rather than by arranging for a real server to
        // produce one. All this supplies is the write itself.
        var results = await ApplySequence.RunAsync(
            writes,
            (write, token) => ApplyAsync(writer, reader, collector, write, options, ownership, token),
            LibraryScanIsRunning,
            result =>
            {
                Record(ledger, result);
                progress.Report(50 + (45.0 * ++completed / writes.Count));
            },
            cancellationToken).ConfigureAwait(false);

        ReportAbandoned(results);
        return results;
    }

    /// <summary>
    /// Opens this run's ledger, or returns null if it cannot be opened.
    /// </summary>
    /// <remarks>
    /// A ledger that will not open is a reason to say so, not a reason to refuse
    /// to restore anything: the plan is still coming, and the run is no worse off
    /// than every version before this one.
    /// </remarks>
    private RunLedger? OpenLedger(Plugin plugin)
    {
        try
        {
            var ledger = RunLedger.Open(plugin.PlanDirectory, DateTimeOffset.UtcNow);
            RunLedger.PruneToLatest(plugin.PlanDirectory, LedgersKept);
            return ledger;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Could not open a run ledger, so this run's only record will be the plan written at the end. "
                + "If that fails too, the log below is all there is.");
            return null;
        }
    }

    /// <summary>
    /// Appends one outcome to the ledger, never letting it disturb the run.
    /// </summary>
    /// <remarks>
    /// Logged once per failure rather than swallowed, but it cannot throw: this
    /// runs between two writes to user data, and an exception here would abandon
    /// the batch over a bookkeeping problem.
    /// </remarks>
    private void Record(RunLedger? ledger, WriteResult result)
    {
        if (ledger is null)
        {
            return;
        }

        try
        {
            ledger.Append(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "Could not record user {UserId} item {ItemId} ({Outcome}) in this run's ledger.",
                result.Write.UserId,
                result.Write.ItemId,
                WriteOutcomes.ToWire(result.Outcome));
        }
    }

    /// <summary>
    /// Says why a run stopped short, once, rather than per abandoned write.
    /// </summary>
    private void ReportAbandoned(IReadOnlyList<WriteResult> results)
    {
        var abandoned = results.Where(result => result.Outcome == WriteOutcome.NotAttempted).ToArray();
        if (abandoned.Length == 0)
        {
            return;
        }

        if (abandoned[0].Detail == ApplySequence.LibraryScanStarted)
        {
            _logger.LogWarning(
                "A library scan started part-way through this run. Abandoning the remaining {Count} restores; "
                + "the stranded rows are untouched, so the next run picks them up.",
                abandoned.Length);
            return;
        }

        _logger.LogError(
            "Stopping after the restore above rather than continuing into {Count} more ({Reason}). "
            + "The stranded rows are untouched, so the next run plans them again.",
            abandoned.Length,
            abandoned[0].Detail);
    }

    /// <remarks>
    /// <para>Nothing in here is allowed to escape except cancellation. A write
    /// that throws does stop the batch, but it must stop it by returning — the
    /// plan is written after this pass, so an exception escaping here would leave
    /// user state changed with no record of what changed it.</para>
    /// <para>Which side of the save it threw on is the whole of the difference
    /// between <see cref="WriteOutcome.Failed"/> and
    /// <see cref="WriteOutcome.Uncertain"/>. Everything before the save leaves the
    /// target provably untouched. The save itself does not: it can throw after the
    /// database has already committed, so once it has been entered the honest
    /// answer about that item is that nobody knows.</para>
    /// </remarks>
    private async Task<WriteResult> ApplyAsync(
        UserDataWriter writer,
        UserDataReader reader,
        LibraryItemCollector collector,
        PlannedWrite write,
        AnalysisOptions options,
        KeyOwnership ownership,
        CancellationToken cancellationToken)
    {
        var saveEntered = false;

        try
        {
            return await ApplyCoreAsync(
                writer, reader, collector, write, options, ownership, () => saveEntered = true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (saveEntered)
            {
                // Cancellation included. Once the save has been entered the run
                // owes an answer about this item, and "the operator pressed stop"
                // is not one - the write may already have committed.
                _logger.LogError(
                    ex,
                    "Restoring user {UserId} item {ItemId} threw during the save. Whether it landed is unknown - the save can throw "
                    + "after the database has committed. The stranded row is untouched, so the state is recoverable either way.",
                    write.UserId,
                    write.ItemId);
                return new WriteResult(write, WriteOutcome.Uncertain, "save_threw");
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogError(
                ex,
                "Restoring user {UserId} item {ItemId} threw before the save was attempted, so the item is untouched. The next run retries it.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Failed, "threw_before_save");
        }
    }

    private async Task<WriteResult> ApplyCoreAsync(
        UserDataWriter writer,
        UserDataReader reader,
        LibraryItemCollector collector,
        PlannedWrite write,
        AnalysisOptions options,
        KeyOwnership ownership,
        Action enteringSave,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(write.UserId);
        var item = _libraryManager.GetItemById(write.ItemId);

        if (user is null || item is null)
        {
            _logger.LogWarning(
                "User {UserId} or item {ItemId} disappeared between analysis and write; skipping.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Skipped, user is null ? "user_gone" : "item_gone");
        }

        // Everything that admitted this target, asked again of the item as it is
        // now. The analysis ran seconds ago, but a metadata refresh can rewrite an
        // item's provider IDs in that time — and the keys those IDs produce are the
        // entire identity argument for writing here. An item that has stopped
        // answering to them is not the item the evidence was about. Membership and
        // path come off the live item too, not out of a map built before the loop.
        var snapshot = collector.Snapshot(item, options.EligibleLibraryIds, options.RequirePathExists);
        if (TargetRevalidation.Evaluate(snapshot, options, write.SourceKeys, ownership) is { } disqualification)
        {
            _logger.LogWarning(
                "Item {ItemId} no longer qualifies as the target for user {UserId} ({Reason}); skipping. "
                + "The stranded row is untouched, so the next run reconsiders it.",
                write.ItemId,
                write.UserId,
                disqualification);
            return new WriteResult(write, WriteOutcome.Skipped, disqualification);
        }

        // Cheap second opinion through the manager, which sees state the database
        // read below cannot contradict but may have cached differently.
        if (!writer.Read(user, item).IsDefault)
        {
            _logger.LogInformation(
                "Item {ItemId} gained user state for {UserId} since the analysis; leaving it alone.",
                write.ItemId,
                write.UserId);
            return new WriteResult(write, WriteOutcome.Skipped, "target_gained_state");
        }

        // Row existence, straight from the database, because the manager cannot
        // answer it: it reports a pair with no row and a pair whose row holds
        // nothing but defaults identically. An unwatch or an unfavourite performed
        // since the analysis writes exactly such a row, and reading it as
        // "untouched" is how a scheduled run would undo a deliberate act.
        //
        // Last of the three checks, immediately before the save, and in that order
        // on purpose: it is the authoritative one, so nothing else belongs between
        // it and the write it guards. What remains is a genuine race — the manager
        // offers no conditional save, so another thread clearing this pair in the
        // gap would still be overwritten — but the gap is one round trip, and
        // reaching it at all requires the clear to land on a pair that has no row,
        // which from the user's side is clearing something already clear.
        if (await reader.RowExistsAsync(write.UserId, write.ItemId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "User {UserId} gained a user-data row for item {ItemId} since the analysis; leaving it alone. "
                + "A row holding default values is an explicit clear, not an absence.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Skipped, "row_exists");
        }

        // Past this line nothing about the target is provable any more, which is
        // why it is the last one: everything above declines cleanly, and the item
        // is untouched whatever happens.
        enteringSave();
        writer.Save(user, item, write.State);

        // Read back through the manager rather than trusting the call: the point
        // of the exercise is that the state is there afterwards.
        if (!RecoveryStateComparer.Semantic.Equals(writer.Read(user, item), write.State))
        {
            _logger.LogError(
                "Wrote user {UserId} item {ItemId} but read back different state. What the item holds now is not what was asked for "
                + "and not necessarily what it held before. The stranded row is untouched.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Uncertain, "verification_mismatch");
        }

        return new WriteResult(write, WriteOutcome.Restored, null);
    }

    /// <summary>
    /// Takes the post-run fingerprint, returning null rather than throwing.
    /// </summary>
    /// <remarks>
    /// This is the proof that the run changed only what it says it changed, and
    /// it is taken after the writes have already landed. A server going away
    /// underneath it — a shutdown cancelling the run, a database that will not
    /// open — must not cost the record of the restores the proof was about, so a
    /// failure here is recorded as a missing fingerprint and the plan is written
    /// without it.
    /// </remarks>
    private async Task<Core.Verification.UserDataFingerprint?> TryFingerprintAsync(
        UserDataReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.FingerprintAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "The post-run fingerprint of the UserData table could not be taken, so this run's plan cannot prove what it left behind. "
                + "What it did is still recorded below.");
            return null;
        }
    }

    /// <summary>
    /// Writes the plan, returning what stopped it rather than throwing.
    /// </summary>
    /// <remarks>
    /// By the time this runs the writes have already happened, so an exception
    /// escaping would trade the account of them for whatever went wrong building
    /// the artifact. The failure is not swallowed — the caller rethrows it once the
    /// counts are in the log, where an operator can still see what this run did.
    /// </remarks>
    private Exception? TryWritePlan(
        Plugin plugin,
        AnalysisResult result,
        AnalysisOptions options,
        Core.Verification.UserDataFingerprint before,
        Core.Verification.UserDataFingerprint? after,
        IReadOnlyList<WriteResult> outcomes)
    {
        try
        {
            WritePlan(plugin, result, options, before, after, outcomes);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "The plan for this run could not be written. The restores below already happened; the run ledger beside it records which pairs were touched, "
                + "and what is missing is the classification detail around them.");
            return ex;
        }
    }

    private void WritePlan(
        Plugin plugin,
        AnalysisResult result,
        AnalysisOptions options,
        Core.Verification.UserDataFingerprint before,
        Core.Verification.UserDataFingerprint? after,
        IReadOnlyList<WriteResult> outcomes)
    {
        var plan = PlanBuilder.Build(result, new PlanContext
        {
            PluginVersion = BuildInfo.PluginVersion,
            TargetJellyfinVersion = BuildInfo.JellyfinRuntimeVersion,
            JellyfinPackageVersion = BuildInfo.JellyfinPackageVersion,
            TargetAbi = BuildInfo.JellyfinTargetAbi,
            ServerId = _applicationHost.SystemId,
            ServerVersion = _applicationHost.ApplicationVersionString,
            CreatedUtc = DateTimeOffset.UtcNow,
            Options = options,
            FingerprintBefore = before,
            FingerprintAfter = after,
            WriteResults = outcomes,
        });

        var store = new PlanStore(plugin.PlanDirectory);
        var path = store.Write(plan);
        store.PruneToLatest(PlansKept);

        _logger.LogInformation("{Summary}", AnalysisSummary.Render(result, outcomes, plan.PlanId, path));
    }

    private void Report(AnalysisResult result, IReadOnlyList<WriteResult> outcomes, bool verbose)
    {
        _logger.LogInformation(
            "Restored {Restored} snapshots, skipped {Skipped}, failed {Failed}, left {Uncertain} uncertain, did not attempt {NotAttempted}.",
            CountOf(outcomes, WriteOutcome.Restored),
            CountOf(outcomes, WriteOutcome.Skipped),
            CountOf(outcomes, WriteOutcome.Failed),
            CountOf(outcomes, WriteOutcome.Uncertain),
            CountOf(outcomes, WriteOutcome.NotAttempted));

        foreach (var code in ReasonCodes.All.Where(
            code => result.CandidateCounts[code] > 0 || result.RowCounts[code] > 0))
        {
            _logger.LogInformation(
                "Classification {Reason}: {Candidates} candidates, {Rows} rows.",
                ReasonCodes.ToWire(code),
                result.CandidateCounts[code],
                result.RowCounts[code]);
        }

        if (result.Diagnostics.EligibleTargetCount > 0 && result.Diagnostics.EligibleTargetsWithProviderKeys == 0)
        {
            _logger.LogWarning(
                "None of the {Count} eligible items reports a key other than its own GUID, so only an exact old-item GUID could ever match. "
                + "Either these libraries carry no provider IDs, or their metadata is not loaded. Check that the items show IMDb/TMDb IDs in the Jellyfin UI.",
                result.Diagnostics.EligibleTargetCount);
        }

        // A mount that is not there looks exactly like a library with nothing to
        // recover: every item fails the file check, the eligible count collapses,
        // and the run succeeds. Say which it was.
        var missingFiles = result.Diagnostics.ExclusionCounts.GetValueOrDefault(ItemExclusion.MissingPath);
        if (missingFiles > 0 && missingFiles >= result.Diagnostics.EligibleTargetCount)
        {
            _logger.LogWarning(
                "{Count} items were skipped because their media file was not found, which is more than the {Eligible} that qualified. "
                + "If those files should be there, a mount is probably missing — fix that before trusting this run.",
                missingFiles,
                result.Diagnostics.EligibleTargetCount);
        }

        if (!verbose)
        {
            return;
        }

        foreach (var candidate in result.Candidates)
        {
            _logger.LogDebug(
                "Candidate user {UserId} item {ItemId} ({Kind}): {Reason} via {EvidenceRule}, {KeyCount} contributing keys, {CurrentRows} current rows.",
                candidate.UserId,
                candidate.Target.ItemId,
                candidate.Target.Kind,
                ReasonCodes.ToWire(candidate.Reason),
                candidate.EvidenceRule,
                candidate.ContributingKeys.Count,
                candidate.CurrentRowCount);
        }
    }

    private static int CountOf(IReadOnlyList<WriteResult> outcomes, WriteOutcome outcome) =>
        outcomes.Count(result => result.Outcome == outcome);
}
