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

        var applied = Apply(result.Writes, progress, cancellationToken);
        progress.Report(95);

        var fingerprintAfter = await reader.FingerprintAsync(cancellationToken).ConfigureAwait(false);

        // Ordered so the counts reach the log either way. The plan is written after
        // the writes, because it records what they did, which means a plan that
        // cannot be written would otherwise take the only account of them with it.
        var planFailure = TryWritePlan(plugin, result, options, fingerprintBefore, fingerprintAfter);
        Report(result, applied, configuration.VerboseLogging);

        progress.Report(100);

        if (applied.Failed > 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{applied.Failed} of {result.Writes.Count} restores did not complete: they threw, or the state did not read back. The stranded rows are untouched; the next run will retry them."),
                planFailure);
        }

        if (planFailure is not null)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The restores completed ({applied.Restored} restored, {applied.Skipped} skipped) but the plan for this run could not be written. What happened is in the log above; the artifact is missing."),
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

    private static AnalysisOptions BuildOptions(PluginConfiguration configuration, ResolvedLibraryScope scope) => new()
    {
        EligibleLibraryIds = scope.LibraryIds,

        // Typed prefixes win; otherwise the libraries' own locations, which the
        // server already knows and a human cannot mistype.
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

    private AppliedCounts Apply(
        IReadOnlyList<PlannedWrite> writes,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
        {
            return default;
        }

        var writer = new UserDataWriter(_userDataManager);
        var counts = default(AppliedCounts);

        for (var index = 0; index < writes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counts = Apply(writer, writes[index], counts);
            progress.Report(50 + (45.0 * (index + 1) / writes.Count));
        }

        return counts;
    }

    /// <remarks>
    /// Nothing in here is allowed to escape except cancellation. A write that
    /// throws must not take the rest of the batch with it, and — more importantly
    /// — must not skip the plan: the plan is written after this pass, so an
    /// exception escaping here would leave user state changed with no record of
    /// what changed it. A failure is counted, logged, and the run carries on.
    /// </remarks>
    private AppliedCounts Apply(UserDataWriter writer, PlannedWrite write, AppliedCounts counts)
    {
        try
        {
            return ApplyCore(writer, write, counts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Restoring user {UserId} item {ItemId} threw. The stranded row is untouched, so the next run retries it.",
                write.UserId,
                write.ItemId);
            return counts with { Failed = counts.Failed + 1 };
        }
    }

    private AppliedCounts ApplyCore(UserDataWriter writer, PlannedWrite write, AppliedCounts counts)
    {
        var user = _userManager.GetUserById(write.UserId);
        var item = _libraryManager.GetItemById(write.ItemId);

        if (user is null || item is null)
        {
            _logger.LogWarning(
                "User {UserId} or item {ItemId} disappeared between analysis and write; skipping.",
                write.UserId,
                write.ItemId);
            return counts with { Skipped = counts.Skipped + 1 };
        }

        // The analysis established this target had no state, but that was a few
        // seconds ago and somebody may have started watching in between. This is
        // the last moment it can be checked, and it is cheap, so check it.
        //
        // Narrower than the analysis on purpose, because it has to be: the manager
        // reports state, not row existence, so it cannot tell "no row" from "a row
        // holding defaults". The analysis can, and refuses to plan a write when any
        // row exists — which is what makes a clear survive a later run. The gap is
        // a clear performed between that analysis and this write, inside one run,
        // which this check reads as untouched and overwrites. Seconds wide, and the
        // next run classifies it current_state_conflict and leaves it alone.
        // Closing it fully means a per-write database round trip on the apply path;
        // not worth it for a window this size.
        if (!writer.Read(user, item).IsDefault)
        {
            _logger.LogInformation(
                "Item {ItemId} gained user state for {UserId} since the analysis; leaving it alone.",
                write.ItemId,
                write.UserId);
            return counts with { Skipped = counts.Skipped + 1 };
        }

        writer.Save(user, item, write.State);

        // Read back through the manager rather than trusting the call: the point
        // of the exercise is that the state is there afterwards.
        if (!RecoveryStateComparer.Semantic.Equals(writer.Read(user, item), write.State))
        {
            _logger.LogError(
                "Wrote user {UserId} item {ItemId} but read back different state. The stranded row is untouched.",
                write.UserId,
                write.ItemId);
            return counts with { Failed = counts.Failed + 1 };
        }

        return counts with { Restored = counts.Restored + 1 };
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
        Core.Verification.UserDataFingerprint after)
    {
        try
        {
            WritePlan(plugin, result, options, before, after);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "The plan for this run could not be written. The restores below already happened; this is the record of them that is missing.");
            return ex;
        }
    }

    private void WritePlan(
        Plugin plugin,
        AnalysisResult result,
        AnalysisOptions options,
        Core.Verification.UserDataFingerprint before,
        Core.Verification.UserDataFingerprint after)
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
        });

        var store = new PlanStore(plugin.PlanDirectory);
        var path = store.Write(plan);
        store.PruneToLatest(PlansKept);

        _logger.LogInformation("{Summary}", AnalysisSummary.Render(result, plan.PlanId, path));
    }

    private void Report(AnalysisResult result, AppliedCounts applied, bool verbose)
    {
        _logger.LogInformation(
            "Restored {Restored} snapshots, skipped {Skipped}, failed {Failed}.",
            applied.Restored,
            applied.Skipped,
            applied.Failed);

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

    /// <summary>What one run actually did.</summary>
    private readonly record struct AppliedCounts(int Restored, int Skipped, int Failed);
}
