using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Verification;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>Facts about the run that the analyzer itself does not know.</summary>
public sealed record PlanContext
{
    /// <summary>Gets the plugin version.</summary>
    public required string PluginVersion { get; init; }

    /// <summary>Gets the Jellyfin version this build was compiled against.</summary>
    public required string TargetJellyfinVersion { get; init; }

    /// <summary>Gets the exact Jellyfin NuGet package this build compiled against.</summary>
    public required string JellyfinPackageVersion { get; init; }

    /// <summary>Gets the manifest ABI this build declares.</summary>
    public required string TargetAbi { get; init; }

    /// <summary>Gets the running server's identity.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the running server's exact version.</summary>
    public required string ServerVersion { get; init; }

    /// <summary>Gets the plan creation time.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Gets the configured scope.</summary>
    public required AnalysisOptions Options { get; init; }

    /// <summary>Gets the table fingerprint taken before analysis.</summary>
    public required UserDataFingerprint FingerprintBefore { get; init; }

    /// <summary>Gets the table fingerprint taken after analysis.</summary>
    public required UserDataFingerprint FingerprintAfter { get; init; }
}

/// <summary>
/// Turns an <see cref="AnalysisResult"/> into the sealed plan artifact.
/// </summary>
public static class PlanBuilder
{
    /// <summary>
    /// Builds and seals a plan.
    /// </summary>
    /// <param name="result">The completed analysis.</param>
    /// <param name="context">Run facts from the host.</param>
    /// <returns>A plan with its canonical ID attached.</returns>
    public static PlanDocument Build(AnalysisResult result, PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var plan = new PlanDocument
        {
            PluginVersion = context.PluginVersion,
            TargetJellyfinVersion = context.TargetJellyfinVersion,
            BuiltAgainstJellyfinPackage = context.JellyfinPackageVersion,
            TargetAbi = context.TargetAbi,
            ServerId = context.ServerId,
            ServerVersion = context.ServerVersion,
            CreatedUtc = context.CreatedUtc,
            ConfiguredLibraryIds = ToSet(context.Options.EligibleLibraryIds.Select(Format)),
            FinalPathPrefixes = ToSet(context.Options.FinalPathPrefixes),

            // Milestone 1 ships no apply task at all. The flag is in the artifact
            // so a plan can never be mistaken for one a future build armed.
            ApplySupported = false,
            Summary = BuildSummary(result),
            ReadOnlyProof = new PlanReadOnlyProof
            {
                RowCountBefore = context.FingerprintBefore.RowCount,
                RowCountAfter = context.FingerprintAfter.RowCount,
                DigestBefore = context.FingerprintBefore.Digest,
                DigestAfter = context.FingerprintAfter.Digest,
                Unchanged = context.FingerprintBefore == context.FingerprintAfter,
            },
            // Array order and length are both part of the plan ID (see
            // PlanCanonicalizer), so every array in the document leaves here in a
            // defined, total order — nothing is left in whatever order a database,
            // a dictionary, or the host happened to produce — and the ones that
            // mean a set go through ToSet. Source rows are ordered here; the rest
            // are ordered by the analyzer or in the mappers below.
            SourceRows = [.. result.SourceRows
                .Select(ToPlanRow)
                .OrderBy(row => row.UserId, StringComparer.Ordinal)
                .ThenBy(row => row.CustomDataKey, StringComparer.Ordinal)
                .ThenBy(row => row.Fingerprint, StringComparer.Ordinal)],
            Candidates = [.. result.Candidates.Select(ToPlanCandidate)],
            Writes = [.. result.Writes.Select(ToPlanWrite)],
        };

        return PlanCanonicalizer.Seal(plan);
    }

    private static PlanSummary BuildSummary(AnalysisResult result)
    {
        var diagnostics = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["detachedRowsInspected"] = result.Diagnostics.DetachedRowsInspected,
            ["currentItemsInspected"] = result.Diagnostics.CurrentItemsInspected,
            ["eligibleTargets"] = result.Diagnostics.EligibleTargetCount,
            ["eligibleTargetsWithProviderKeys"] = result.Diagnostics.EligibleTargetsWithProviderKeys,
            ["distinctCurrentKeys"] = result.Diagnostics.DistinctCurrentKeyCount,
            ["knownUsers"] = result.Diagnostics.KnownUserCount,
            ["seriesGuidEpisodeDerivedRows"] = result.Diagnostics.SeriesGuidEpisodeDerivedRows,
            ["candidatesBlockedOnlyBySeriesGuidEvidence"] = result.Diagnostics.CandidatesBlockedOnlyBySeriesGuidEvidence,
        };

        foreach (var (evidence, count) in result.Diagnostics.UniqueMatchEvidenceCounts)
        {
            diagnostics["uniqueMatchEvidence." + KeyEvidenceNames.ToWire(evidence)] = count;
        }

        return new PlanSummary
        {
            RowCounts = ToWireCounts(result.RowCounts),
            CandidateCounts = ToWireCounts(result.CandidateCounts),
            WriteCount = result.Writes.Count,
            Diagnostics = diagnostics,
        };
    }

    private static Dictionary<string, int> ToWireCounts(IReadOnlyDictionary<ReasonCode, int> counts) =>
        counts.ToDictionary(entry => ReasonCodes.ToWire(entry.Key), entry => entry.Value, StringComparer.Ordinal);

    private static PlanSourceRow ToPlanRow(SourceRowRecord record) => new()
    {
        UserId = Format(record.Row.UserId),
        CustomDataKey = record.Row.CustomDataKey,
        RetentionDate = Format(record.Row.RetentionDate),
        State = ToPlanState(record.Row.State),
        ReportOnly = new PlanReportOnlyFields
        {
            Likes = record.Row.Likes,
            AudioStreamIndex = record.Row.AudioStreamIndex,
            SubtitleStreamIndex = record.Row.SubtitleStreamIndex,
        },
        Fingerprint = record.Row.Fingerprint,
        Reason = ReasonCodes.ToWire(record.Reason),
        Violation = record.Violation,
        TargetItemId = record.TargetItemId is { } id ? Format(id) : null,
        Evidence = KeyEvidenceNames.ToWire(record.Evidence),
        EvidenceProvider = record.EvidenceProvider,
        SeriesGuidEpisodeDerived = record.SeriesGuidEpisodeDerived,
        Matches = [.. record.Matches
            .Select(match => new PlanMatch
            {
                ItemId = Format(match.ItemId),
                Kind = match.Kind.ToString(),
                Exclusion = match.Exclusion.ToString(),
                Name = match.Name,
                Path = match.Path,
            })
            .OrderBy(match => match.ItemId, StringComparer.Ordinal)],
    };

    private static PlanCandidate ToPlanCandidate(CandidateRecord record) => new()
    {
        UserId = Format(record.UserId),
        TargetItemId = Format(record.Target.ItemId),
        TargetKind = record.Target.Kind.ToString(),
        TargetName = record.Target.Name,
        TargetPath = record.Target.Path,
        TargetLibraryIds = ToSet(record.Target.LibraryIds.Select(Format)),

        // A set, not the list GetUserDataKeys() handed back. What the plan asserts
        // is which keys the target answers to; the order and multiplicity the host
        // happens to produce them in are the host's business, and CurrentKeyIndex
        // already reads a repeated key as one match.
        TargetKeys = ToSet(record.Target.UserDataKeys),
        ContributingKeys = [.. record.ContributingKeys
            .Select(key => new PlanContributingKey
            {
                Key = key.Key,
                Evidence = KeyEvidenceNames.ToWire(key.Evidence),
                Provider = key.ProviderName,
                Fingerprint = key.Row.Fingerprint,
            })
            .OrderBy(key => key.Key, StringComparer.Ordinal)],
        RecoveredState = record.RecoveredState is null ? null : ToPlanState(record.RecoveredState),
        EvidenceRule = record.EvidenceRule,
        CurrentRowCount = record.CurrentRowCount,
        CurrentState = record.CurrentState is null ? null : ToPlanState(record.CurrentState),
        Reason = ReasonCodes.ToWire(record.Reason),
    };

    private static PlanWrite ToPlanWrite(PlannedWrite write) => new()
    {
        UserId = Format(write.UserId),
        ItemId = Format(write.ItemId),
        State = ToPlanState(write.State),
        EvidenceRule = write.EvidenceRule,
        SourceFingerprints = [.. write.SourceFingerprints.Order(StringComparer.Ordinal)],
    };

    private static PlanState ToPlanState(RecoveryState state) => new()
    {
        Played = state.Played,
        PlayCount = state.PlayCount,
        PlaybackPositionTicks = state.PlaybackPositionTicks,
        IsFavorite = state.IsFavorite,
        LastPlayedDate = Format(state.LastPlayedDate),
        Rating = state.Rating,
    };

    // The plan's set-valued fields: key sets, library memberships, configured
    // prefixes. Deduplicated as well as ordered, because array *length* is part of
    // the plan ID too. Without this a target that reports the same key twice — which
    // CurrentKeyIndex deliberately counts once — would hash differently from the
    // same target reporting it once, as would a configuration listing one path
    // prefix twice, and two plans meaning exactly the same thing would carry
    // different IDs.
    //
    // Deliberately not applied to sourceRows, candidates, or writes. Those are
    // ledgers, not sets: two entries that happen to render alike are still two
    // rows, and §8 requires writes to stay the exact ordered list they are.
    private static IReadOnlyList<string> ToSet(IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string Format(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string? Format(DateTime? value) =>
        value is null ? null : DateTimeNormalization.ToUtc(value.Value).ToString("O", CultureInfo.InvariantCulture);
}
