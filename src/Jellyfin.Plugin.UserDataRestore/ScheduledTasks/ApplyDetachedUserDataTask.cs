using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Applying;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.Jellyfin;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserDataRestore.ScheduledTasks;

/// <summary>
/// Apply detached user-data recovery (DESIGN §9).
/// </summary>
/// <remarks>
/// <para>Applies the newest plan the analysis wrote, after re-running that
/// analysis and confirming the world still matches it in every respect. Running
/// this task is the deliberate act; it has no triggers and nothing schedules
/// it.</para>
/// <para>Every write goes through <see cref="IUserDataManager"/> and is read back
/// and verified. The stranded rows are never touched: they are the only remaining
/// copy of this state, and leaving them intact is what makes a failed run
/// recoverable.</para>
/// </remarks>
public class ApplyDetachedUserDataTask : IScheduledTask
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<ApplyDetachedUserDataTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyDetachedUserDataTask"/> class.
    /// </summary>
    /// <param name="dbFactory">The host's database context factory.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The only supported write path.</param>
    /// <param name="applicationHost">The application host.</param>
    /// <param name="logger">The logger.</param>
    public ApplyDetachedUserDataTask(
        IDbContextFactory<JellyfinDbContext> dbFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IApplicationHost applicationHost,
        ILogger<ApplyDetachedUserDataTask> logger)
    {
        _dbFactory = dbFactory;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Apply detached user-data recovery";

    /// <inheritdoc />
    public string Key => "UserDataRestoreApply";

    /// <inheritdoc />
    public string Description =>
        "Restores the recoverable user data found by the analysis, through Jellyfin's own user-data manager. Re-checks everything the plan assumed before writing anything.";

    /// <inheritdoc />
    public string Category => "Restore User Data After Move";

    /// <inheritdoc />
    /// <remarks>No triggers. This task exists to be run deliberately, once.</remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        ServerVersionGate.EnsureSupported(_applicationHost.ApplicationVersion);

        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("The plugin instance is not available.");
        var configuration = plugin.Configuration;
        var stored = new PlanStore(plugin.PlanDirectory).List();

        if (stored.Count == 0)
        {
            throw new InvalidOperationException(
                "No plan has been written. Run the analysis first.");
        }

        var plan = PlanCanonicalizer.FromJson(File.ReadAllText(stored[0].Path));

        var reader = new UserDataReader(_dbFactory);
        await reader.EnsureModelCompatibleAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(5);

        // Preflight re-runs the analysis and reconciles it against the plan, so
        // anything that would change a classification blocks the run before a
        // single write lands.
        var fresh = await AnalyzeAsync(configuration, reader, cancellationToken).ConfigureAwait(false);
        progress.Report(30);

        var preflight = ApplyPreflight.Reconcile(plan, fresh);

        if (!preflight.MayProceed)
        {
            foreach (var blocker in preflight.Blockers)
            {
                _logger.LogError("Preflight blocked the apply: {Blocker}", blocker);
            }

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Preflight refused this run: {preflight.Blockers.Count} precondition(s) changed since the plan was written. Run the analysis again."));
        }

        progress.Report(35);

        var pending = preflight.Pending.ToArray();
        _logger.LogInformation(
            "Applying plan {PlanId}: {Pending} writes, {NoOp} already applied.",
            plan.PlanId,
            pending.Length,
            preflight.Writes.Count - pending.Length);

        var writer = new UserDataWriter(_userDataManager);
        var applied = 0;
        var failed = 0;

        for (var index = 0; index < pending.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = pending[index];

            if (Apply(writer, write))
            {
                applied++;
            }
            else
            {
                failed++;
            }

            progress.Report(35 + (60.0 * (index + 1) / Math.Max(1, pending.Length)));
        }

        var fingerprintAfter = await reader.FingerprintAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Applied {Applied} of {Total} writes, {Failed} failed. UserData now {Rows} rows, digest {Digest}.",
            applied,
            pending.Length,
            failed,
            fingerprintAfter.RowCount,
            fingerprintAfter.Digest);

        if (failed > 0)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"{failed} of {pending.Length} writes did not verify. The stranded rows are untouched; re-run the analysis to see what remains."));
        }

        progress.Report(100);
    }

    private async Task<AnalysisResult> AnalyzeAsync(
        Configuration.PluginConfiguration configuration,
        UserDataReader reader,
        CancellationToken cancellationToken)
    {
        var scope = new LibraryScope(_libraryManager).Resolve(configuration.EligibleLibraryIds);
        var options = new AnalysisOptions
        {
            EligibleLibraryIds = scope.LibraryIds,
            FinalPathPrefixes = ScopeDefaults.ResolvePrefixes(configuration.FinalPathPrefixes, scope.Locations),
            PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
            RequirePathExists = configuration.RequirePathExists,
            NowUtc = DateTime.UtcNow,
        };

        var detachedRows = await reader.ReadDetachedAsync(cancellationToken).ConfigureAwait(false);
        var currentItems = new LibraryItemCollector(_libraryManager).Collect(options.EligibleLibraryIds, cancellationToken);

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

    private bool Apply(UserDataWriter writer, ReconciledWrite write)
    {
        var user = _userManager.GetUserById(write.UserId);
        var item = _libraryManager.GetItemById(write.ItemId);

        if (user is null || item is null)
        {
            _logger.LogError("User {UserId} or item {ItemId} vanished between preflight and write.", write.UserId, write.ItemId);
            return false;
        }

        writer.Save(user, item, write.State);

        // Read back through the manager rather than trusting the call: the point
        // of the exercise is that the state is there afterwards.
        var observed = writer.Read(user, item);

        if (!RecoveryStateComparer.Semantic.Equals(observed, write.State))
        {
            _logger.LogError(
                "Wrote user {UserId} item {ItemId} but read back different state. The stranded row is untouched.",
                write.UserId,
                write.ItemId);
            return false;
        }

        return true;
    }
}
