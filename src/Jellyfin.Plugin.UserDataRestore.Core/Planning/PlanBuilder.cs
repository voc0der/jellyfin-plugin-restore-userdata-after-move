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

    /// <summary>
    /// Gets the table fingerprint taken after the writes, or null if it could not
    /// be taken.
    /// </summary>
    public required UserDataFingerprint? FingerprintAfter { get; init; }

    /// <summary>
    /// Gets what became of each planned write, in the order the analysis planned
    /// them.
    /// </summary>
    /// <remarks>
    /// Supplied by the host rather than derived from the analysis, because it is
    /// the one thing in the plan the analysis cannot know. It must cover every
    /// planned write — a run that stopped early reports the rest as
    /// <see cref="WriteOutcome.NotAttempted"/> rather than omitting them.
    /// </remarks>
    public required IReadOnlyList<WriteResult> WriteResults { get; init; }
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

        // The plan says what this run did, so a result list that does not line up
        // with the writes it claims to describe is a bug worth failing on rather
        // than an artifact worth publishing.
        if (context.WriteResults.Count != result.Writes.Count)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The run reported {context.WriteResults.Count} outcomes for {result.Writes.Count} planned writes."),
                nameof(context));
        }

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

            // Recorded in the artifact so a plan written by an analysis-only build
            // can never be mistaken for one this build produced.
            ApplySupported = true,
            Summary = BuildSummary(result, context.WriteResults),
            TableChange = new PlanTableChange
            {
                RowCountBefore = context.FingerprintBefore.RowCount,
                RowCountAfter = context.FingerprintAfter?.RowCount,
                DigestBefore = context.FingerprintBefore.Digest,
                DigestAfter = context.FingerprintAfter?.Digest,
                Unchanged = context.FingerprintAfter is { } after ? context.FingerprintBefore == after : null,
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
            Writes = [.. context.WriteResults.Select(ToPlanWrite)],
        };

        return PlanCanonicalizer.Seal(plan);
    }

    private static PlanSummary BuildSummary(AnalysisResult result, IReadOnlyList<WriteResult> outcomes)
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

        // Every reason, including the zeroes: "no items were dropped for a missing
        // file" is itself the answer to the question this block exists to raise.
        foreach (var exclusion in ItemExclusions.All)
        {
            diagnostics["items." + ItemExclusions.ToWire(exclusion)] =
                result.Diagnostics.ExclusionCounts.GetValueOrDefault(exclusion);
        }

        return new PlanSummary
        {
            RowCounts = ToWireCounts(result.RowCounts),
            CandidateCounts = ToWireCounts(result.CandidateCounts),
            WriteCount = result.Writes.Count,
            WriteOutcomeCounts = WriteOutcomes.All.ToDictionary(
                WriteOutcomes.ToWire,
                outcome => outcomes.Count(entry => entry.Outcome == outcome),
                StringComparer.Ordinal),
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

    private static PlanWrite ToPlanWrite(WriteResult result) => new()
    {
        UserId = Format(result.Write.UserId),
        ItemId = Format(result.Write.ItemId),
        State = ToPlanState(result.Write.State),
        EvidenceRule = result.Write.EvidenceRule,
        SourceFingerprints = [.. result.Write.SourceFingerprints.Order(StringComparer.Ordinal)],
        SourceKeys = ToSet(result.Write.SourceKeys),
        Outcome = WriteOutcomes.ToWire(result.Outcome),
        OutcomeDetail = result.Detail,
    };

    private static PlanState ToPlanState(RecoveryState state)
    {
        var finite = state.Rating is { } rating && double.IsFinite(rating);

        return new PlanState
        {
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = Format(state.LastPlayedDate),

            // JSON has no number for NaN or an infinity, and the serializer throws
            // rather than inventing one. Such a rating is always rejected as
            // invalid_source_state and never restored, but the row carrying it is
            // still reported — and this document is written after the run's writes
            // have landed, so letting it throw here would trade a row nobody acted
            // on for the audit record of every restore that did happen. The value
            // survives as text; the numeric field stays a number or nothing.
            Rating = finite ? state.Rating : null,
            RatingLiteral = finite || state.Rating is null
                ? null
                : state.Rating.Value.ToString("R", CultureInfo.InvariantCulture),
        };
    }

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
