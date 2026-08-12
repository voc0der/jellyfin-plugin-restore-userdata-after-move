using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// What became of one detached row (DESIGN §7.6).
/// </summary>
public sealed record SourceRowRecord
{
    /// <summary>Gets the detached row.</summary>
    public required DetachedUserDataRow Row { get; init; }

    /// <summary>Gets the single category this row ended in.</summary>
    public required ReasonCode Reason { get; init; }

    /// <summary>Gets the specific problem, when the reason is <see cref="ReasonCode.InvalidSourceState"/>.</summary>
    public string? Violation { get; init; }

    /// <summary>Gets the item this row's key resolved to, when it resolved uniquely.</summary>
    public Guid? TargetItemId { get; init; }

    /// <summary>Gets the identity evidence this row's key carries for that target.</summary>
    public KeyEvidence Evidence { get; init; }

    /// <summary>Gets the provider that produced the key, when known.</summary>
    public string? EvidenceProvider { get; init; }

    /// <summary>Gets a value indicating whether the key is series-GUID derived (recorded, not admitted).</summary>
    public bool SeriesGuidEpisodeDerived { get; init; }

    /// <summary>
    /// Gets every current item that reported this key. Populated for ambiguous and
    /// excluded matches so an operator can see what competed.
    /// </summary>
    public IReadOnlyList<MatchedItemRef> Matches { get; init; } = [];
}

/// <summary>
/// One collapsed <c>(user, target)</c> candidate and its outcome.
/// </summary>
public sealed record CandidateRecord
{
    /// <summary>Gets the user.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the target item.</summary>
    public required CurrentItemSnapshot Target { get; init; }

    /// <summary>Gets the detached rows that collapsed onto this candidate.</summary>
    public required IReadOnlyList<ContributingKey> ContributingKeys { get; init; }

    /// <summary>Gets the state that would be restored, or <see langword="null"/> when the sources disagreed.</summary>
    public RecoveryState? RecoveredState { get; init; }

    /// <summary>Gets the single category this candidate ended in.</summary>
    public required ReasonCode Reason { get; init; }

    /// <summary>Gets which case of the identity-evidence rule was satisfied.</summary>
    public string EvidenceRule { get; init; } = IdentityEvidenceRule.NoneRule;

    /// <summary>Gets the number of live <c>UserData</c> rows the target already has for this user.</summary>
    public int CurrentRowCount { get; init; }

    /// <summary>Gets the target's existing state, when it has any.</summary>
    public RecoveryState? CurrentState { get; init; }
}

/// <summary>
/// One recovery write, as it would be performed by a later apply task.
/// </summary>
public sealed record PlannedWrite
{
    /// <summary>Gets the user to write for.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the item to write to.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the state to write.</summary>
    public required RecoveryState State { get; init; }

    /// <summary>Gets the evidence rule that admitted this write.</summary>
    public required string EvidenceRule { get; init; }

    /// <summary>Gets the fingerprints of the source rows this write came from.</summary>
    public required IReadOnlyList<string> SourceFingerprints { get; init; }
}

/// <summary>
/// Run-level observations that are not classifications.
/// </summary>
/// <remarks>
/// These exist for the go/no-go review in PLAN §2: the question is whether
/// uniquely matchable, state-bearing rows are common enough to justify building a
/// write path, and that is answered by counts, not by anecdotes.
/// </remarks>
public sealed record AnalysisDiagnostics
{
    /// <summary>Gets the number of detached rows inspected.</summary>
    public int DetachedRowsInspected { get; init; }

    /// <summary>Gets the number of current movies and episodes inspected.</summary>
    public int CurrentItemsInspected { get; init; }

    /// <summary>Gets the number of current items that are eligible recovery targets.</summary>
    public int EligibleTargetCount { get; init; }

    /// <summary>
    /// Gets the number of eligible targets reporting a key other than their own GUID.
    /// </summary>
    /// <remarks>
    /// Zero, with eligible targets present, means nothing provider-derived exists
    /// to match a stranded row against. That is either a real property of the
    /// library or a sign the host returned items without their metadata; both are
    /// worth surfacing rather than reporting as "nothing recoverable".
    /// </remarks>
    public int EligibleTargetsWithProviderKeys { get; init; }

    /// <summary>
    /// Gets how many current items each exclusion reason accounted for.
    /// </summary>
    /// <remarks>
    /// A run where most items were dropped for a missing media file is almost
    /// never a library with missing files; it is a mount that is not there. That
    /// is indistinguishable from "nothing to recover" unless the reason is
    /// reported, which is what this is for.
    /// </remarks>
    public IReadOnlyDictionary<ItemExclusion, int> ExclusionCounts { get; init; } =
        new Dictionary<ItemExclusion, int>();

    /// <summary>Gets the number of distinct keys in the reverse index.</summary>
    public int DistinctCurrentKeyCount { get; init; }

    /// <summary>Gets the number of surviving users.</summary>
    public int KnownUserCount { get; init; }

    /// <summary>Gets, per evidence kind, how many rows matched a unique eligible target with that evidence.</summary>
    public IReadOnlyDictionary<KeyEvidence, int> UniqueMatchEvidenceCounts { get; init; } =
        new Dictionary<KeyEvidence, int>();

    /// <summary>Gets the number of rows whose key was the current series' GUID plus padded episode numbers.</summary>
    public int SeriesGuidEpisodeDerivedRows { get; init; }

    /// <summary>
    /// Gets the number of candidates rejected for insufficient evidence that would
    /// have qualified had series-GUID-derived episode keys counted.
    /// </summary>
    /// <remarks>
    /// If this number is large, DESIGN §7.3 is leaving safe recovery on the table
    /// and deserves a fourth case. If it is zero, the question never has to be
    /// reopened.
    /// </remarks>
    public int CandidatesBlockedOnlyBySeriesGuidEvidence { get; init; }
}

/// <summary>
/// The complete outcome of one analysis run.
/// </summary>
public sealed record AnalysisResult
{
    /// <summary>Gets one record per detached row.</summary>
    public required IReadOnlyList<SourceRowRecord> SourceRows { get; init; }

    /// <summary>Gets one record per collapsed candidate.</summary>
    public required IReadOnlyList<CandidateRecord> Candidates { get; init; }

    /// <summary>Gets the ordered list of writes a later apply task would perform.</summary>
    public required IReadOnlyList<PlannedWrite> Writes { get; init; }

    /// <summary>Gets the count of detached rows per reason code.</summary>
    public required IReadOnlyDictionary<ReasonCode, int> RowCounts { get; init; }

    /// <summary>Gets the count of candidates per reason code.</summary>
    public required IReadOnlyDictionary<ReasonCode, int> CandidateCounts { get; init; }

    /// <summary>Gets the run-level observations.</summary>
    public required AnalysisDiagnostics Diagnostics { get; init; }
}
