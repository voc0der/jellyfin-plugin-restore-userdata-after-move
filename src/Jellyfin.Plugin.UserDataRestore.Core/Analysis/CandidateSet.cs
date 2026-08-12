using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// The state of an analysis between DESIGN §7.4 and §7.5 — everything classified
/// that can be classified without asking the database about current target state.
/// </summary>
public sealed class CandidateSet
{
    internal CandidateSet(
        IReadOnlyList<RowDraft> rowDrafts,
        IReadOnlyList<CandidateRecord> resolvedCandidates,
        IReadOnlyList<GroupDraft> pendingCandidates,
        AnalysisDiagnostics diagnostics)
    {
        RowDrafts = rowDrafts;
        ResolvedCandidates = resolvedCandidates;
        PendingCandidates = pendingCandidates;
        Diagnostics = diagnostics;
        PairsToInspect = [.. pendingCandidates
            .Select(g => (UserId: g.UserId, ItemId: g.Target.ItemId))
            .OrderBy(p => p.UserId)
            .ThenBy(p => p.ItemId)];
    }

    /// <summary>
    /// Gets the <c>(user, item)</c> pairs whose current rows must be read before
    /// the analysis can finish.
    /// </summary>
    public IReadOnlyList<(Guid UserId, Guid ItemId)> PairsToInspect { get; }

    /// <summary>Gets the run-level observations gathered so far.</summary>
    public AnalysisDiagnostics Diagnostics { get; }

    internal IReadOnlyList<RowDraft> RowDrafts { get; }

    internal IReadOnlyList<CandidateRecord> ResolvedCandidates { get; }

    internal IReadOnlyList<GroupDraft> PendingCandidates { get; }
}

/// <summary>
/// A detached row being classified. Mutable while the pipeline runs; sealed into
/// an immutable <see cref="SourceRowRecord"/> at the end.
/// </summary>
internal sealed class RowDraft(DetachedUserDataRow row)
{
    private ReasonCode? _reason;

    public DetachedUserDataRow Row { get; } = row;

    public string? Violation { get; set; }

    public Guid? TargetItemId { get; set; }

    public KeyEvidence Evidence { get; set; }

    public string? EvidenceProvider { get; set; }

    public bool SeriesGuidEpisodeDerived { get; set; }

    public IReadOnlyList<MatchedItemRef> Matches { get; set; } = [];

    public void Resolve(ReasonCode reason) => _reason = reason;

    public SourceRowRecord ToRecord() => new()
    {
        Row = Row,

        // Every row is resolved either directly or through its group. A row that
        // reaches here unresolved is a pipeline bug, and failing loudly beats
        // reporting a wrong category in an artifact an operator will act on.
        Reason = _reason ?? throw new InvalidOperationException(
            $"Detached row (user {Row.UserId:N}, key '{Row.CustomDataKey}') was never classified."),
        Violation = Violation,
        TargetItemId = TargetItemId,
        Evidence = Evidence,
        EvidenceProvider = EvidenceProvider,
        SeriesGuidEpisodeDerived = SeriesGuidEpisodeDerived,
        Matches = Matches,
    };
}

/// <summary>
/// A <c>(user, target)</c> group being collapsed.
/// </summary>
internal sealed class GroupDraft(Guid userId, CurrentItemSnapshot target)
{
    public Guid UserId { get; } = userId;

    public CurrentItemSnapshot Target { get; } = target;

    public List<RowDraft> Rows { get; } = [];

    public List<ContributingKey> Keys { get; } = [];

    public RecoveryState? State { get; set; }

    public string EvidenceRule { get; set; } = IdentityEvidenceRule.NoneRule;
}
