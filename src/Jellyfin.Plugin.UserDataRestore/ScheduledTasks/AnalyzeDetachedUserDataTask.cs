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
/// Analyze detached user data (DESIGN §7, PLAN §2).
/// </summary>
/// <remarks>
/// <para>Read-only, and demonstrably so: the task fingerprints the entire
/// <c>UserData</c> table before and after the run and records both in the plan.
/// Nothing in this file writes.</para>
/// <para>It produces the plan the apply task consumes. The apply task re-runs
/// this same analysis before it writes anything, so a stale plan cannot be acted
/// on — which is what makes it safe for this half to have no guard at all.</para>
/// </remarks>
public class AnalyzeDetachedUserDataTask : IScheduledTask
{
    // Housekeeping, not a preference. Plans are small, five is plenty of history
    // to compare against, and nobody opening a plugin page has an opinion about it.
    private const int PlansKept = 5;

    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<AnalyzeDetachedUserDataTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzeDetachedUserDataTask"/> class.
    /// </summary>
    /// <param name="dbFactory">The host's database context factory.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="applicationHost">The application host, for server identity and version.</param>
    /// <param name="logger">The logger.</param>
    public AnalyzeDetachedUserDataTask(
        IDbContextFactory<JellyfinDbContext> dbFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IApplicationHost applicationHost,
        ILogger<AnalyzeDetachedUserDataTask> logger)
    {
        _dbFactory = dbFactory;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Analyze detached user data";

    /// <inheritdoc />
    public string Key => "UserDataRestoreAnalyze";

    /// <inheritdoc />
    public string Description =>
        "Finds user data left behind when media paths changed and reports which current item each stranded snapshot belongs to. Read-only: this task cannot change any user data.";

    /// <inheritdoc />
    public string Category => "Restore User Data After Move";

    /// <inheritdoc />
    /// <remarks>
    /// No triggers. An administrator can still add one, and that is harmless here:
    /// this task only reads and writes a plan file. The apply half is a separate
    /// task precisely so that scheduling this one cannot schedule that one.
    /// </remarks>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        ServerVersionGate.EnsureSupported(_applicationHost.ApplicationVersion);

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
        var collector = new LibraryItemCollector(_libraryManager);

        _logger.LogInformation(
            "Analyzing detached user data. Plugin {PluginVersion}, built against Jellyfin package {PackageVersion} for server {TargetVersion}, running on {ServerVersion}.",
            BuildInfo.PluginVersion,
            BuildInfo.JellyfinPackageVersion,
            BuildInfo.JellyfinRuntimeVersion,
            _applicationHost.ApplicationVersion);

        // The version check above compares major.minor.build only, because
        // Jellyfin's assemblies carry no prerelease marker to compare. This checks
        // the thing the version was standing in for.
        await reader.EnsureModelCompatibleAsync(cancellationToken).ConfigureAwait(false);

        // Taken before anything else so the proof covers every query this task
        // makes, including the ones that follow.
        var fingerprintBefore = await reader.FingerprintAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(5);

        var detachedRows = await reader.ReadDetachedAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(15);

        var currentItems = collector.Collect(options.EligibleLibraryIds, cancellationToken);
        progress.Report(50);

        var knownUserIds = _userManager.GetUsersIds().ToHashSet();
        progress.Report(55);

        var candidates = DetachedUserDataAnalyzer.BuildCandidates(new AnalysisInput
        {
            DetachedRows = detachedRows,
            CurrentItems = currentItems,
            KnownUserIds = knownUserIds,
            Options = options,
        });
        progress.Report(70);

        var currentRows = await reader
            .ReadCurrentAsync(candidates.PairsToInspect, cancellationToken)
            .ConfigureAwait(false);
        progress.Report(80);

        var result = DetachedUserDataAnalyzer.Complete(candidates, currentRows);
        progress.Report(85);

        var fingerprintAfter = await reader.FingerprintAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(90);

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
            FingerprintBefore = fingerprintBefore,
            FingerprintAfter = fingerprintAfter,
        });

        var store = new PlanStore(plugin.PlanDirectory);
        var planPath = store.Write(plan);
        store.PruneToLatest(PlansKept);
        progress.Report(95);

        ReportReadOnlyProof(fingerprintBefore, fingerprintAfter);
        Report(result, plan, planPath, configuration.VerboseLogging);

        progress.Report(100);
    }

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

    private void ReportReadOnlyProof(
        Core.Verification.UserDataFingerprint before,
        Core.Verification.UserDataFingerprint after)
    {
        if (before == after)
        {
            _logger.LogInformation(
                "UserData is unchanged: {RowCount} rows, digest {Digest}.",
                after.RowCount,
                after.Digest);
            return;
        }

        // This task issues no writes at all, so a difference means something else
        // touched the table while it ran — playback, a client, another task. Say
        // that plainly rather than implying the analysis did it.
        _logger.LogWarning(
            "UserData changed while analysis ran ({BeforeRows} rows/{BeforeDigest} then {AfterRows} rows/{AfterDigest}). "
            + "This task performs no writes, so another writer was active — playback, a client, or another scheduled task. "
            + "Re-run during a quiet window for a clean read-only proof.",
            before.RowCount,
            before.Digest,
            after.RowCount,
            after.Digest);
    }

    private void Report(AnalysisResult result, PlanDocument plan, string planPath, bool verbose)
    {
        _logger.LogInformation("{Summary}", AnalysisSummary.Render(result, plan.PlanId, planPath));

        foreach (var code in ReasonCodes.All)
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

        var blocked = result.Diagnostics.CandidatesBlockedOnlyBySeriesGuidEvidence;
        if (blocked > 0)
        {
            _logger.LogInformation(
                "{Count} candidates carried a current-series-GUID episode key and nothing else the v1 evidence rule accepts. "
                + "Recorded for review; not recoverable by this design.",
                blocked);
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

        foreach (var row in result.SourceRows)
        {
            _logger.LogDebug(
                "Row user {UserId} key '{Key}': {Reason}{Detail}.",
                row.Row.UserId,
                row.Row.CustomDataKey,
                ReasonCodes.ToWire(row.Reason),
                row.Violation is null
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $" ({row.Violation})"));
        }
    }
}
