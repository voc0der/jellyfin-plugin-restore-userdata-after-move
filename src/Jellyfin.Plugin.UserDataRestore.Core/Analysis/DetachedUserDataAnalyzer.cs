using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Everything the analyzer needs, with no Jellyfin types in sight.
/// </summary>
public sealed record AnalysisInput
{
    /// <summary>Gets the sentinel-owned rows (DESIGN §7.1).</summary>
    public required IReadOnlyList<DetachedUserDataRow> DetachedRows { get; init; }

    /// <summary>Gets every current movie and episode on the server (DESIGN §7.2).</summary>
    public required IReadOnlyList<CurrentItemSnapshot> CurrentItems { get; init; }

    /// <summary>Gets the IDs of surviving Jellyfin users.</summary>
    public required IReadOnlySet<Guid> KnownUserIds { get; init; }

    /// <summary>Gets the configured scope.</summary>
    public required AnalysisOptions Options { get; init; }
}

/// <summary>
/// The analysis pipeline of DESIGN §7.
/// </summary>
/// <remarks>
/// <para>Split in two because step §7.5 needs a database round trip that this
/// library must not perform: <see cref="BuildCandidates"/> produces the
/// <c>(user, item)</c> pairs worth inspecting, the caller queries current rows for
/// exactly those pairs, and <see cref="Complete"/> finishes the classification.
/// Everything in between is pure.</para>
/// <para>Precedence between reason codes is fixed and matters, because the
/// go/no-go review reads these counts. A group whose recoverable state is entirely
/// default is reported as <c>source_has_no_effect</c> even when its identity
/// evidence is also insufficient: it would produce no write either way, and
/// counting it as an evidence failure would overstate how much the evidence rule
/// costs.</para>
/// </remarks>
public static class DetachedUserDataAnalyzer
{
    /// <summary>
    /// Runs everything up to the point where current target state is needed.
    /// </summary>
    /// <param name="input">The analysis input.</param>
    /// <returns>Partially classified rows plus the pairs still to inspect.</returns>
    public static CandidateSet BuildCandidates(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var index = CurrentKeyIndex.Build(input.CurrentItems, input.Options);
        var drafts = new List<RowDraft>(input.DetachedRows.Count);
        var groups = new Dictionary<(Guid UserId, Guid ItemId), GroupDraft>();
        var evidenceCounts = new Dictionary<KeyEvidence, int>();
        var seriesGuidDerivedRows = 0;

        foreach (var row in input.DetachedRows)
        {
            var draft = new RowDraft(row);
            drafts.Add(draft);

            if (!SourceStateValidator.TryValidate(row, input.Options, out var violation))
            {
                draft.Resolve(ReasonCode.InvalidSourceState);
                draft.Violation = violation;
                continue;
            }

            if (!input.KnownUserIds.Contains(row.UserId))
            {
                draft.Resolve(ReasonCode.UnknownUser);
                continue;
            }

            var lookup = index.Lookup(row.CustomDataKey);
            draft.Matches = lookup.Matches;

            switch (lookup.Kind)
            {
                case KeyMatchKind.NoMatch:
                    draft.Resolve(ReasonCode.NoCurrentKeyMatch);
                    continue;

                case KeyMatchKind.Ambiguous:
                    draft.Resolve(ReasonCode.AmbiguousCurrentKey);
                    continue;

                case KeyMatchKind.UniqueExcluded:
                    draft.Resolve(ItemEligibility.ToReasonCode(lookup.Matches[0].Exclusion));
                    continue;
            }

            var target = lookup.Target!;
            var evidence = KeyEvidenceAnnotator.Annotate(target, row.CustomDataKey!);

            draft.TargetItemId = target.ItemId;
            draft.Evidence = evidence.Evidence;
            draft.EvidenceProvider = evidence.ProviderName;
            draft.SeriesGuidEpisodeDerived = evidence.SeriesGuidDerived;

            evidenceCounts[evidence.Evidence] = evidenceCounts.GetValueOrDefault(evidence.Evidence) + 1;
            if (evidence.SeriesGuidDerived)
            {
                seriesGuidDerivedRows++;
            }

            var key = (row.UserId, target.ItemId);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new GroupDraft(row.UserId, target);
                groups[key] = group;
            }

            group.Rows.Add(draft);
            group.Keys.Add(new ContributingKey(row, evidence.Evidence, evidence.ProviderName, evidence.SeriesGuidDerived));
        }

        var resolved = new List<CandidateRecord>();
        var pending = new List<GroupDraft>();
        var blockedOnlyBySeriesGuid = 0;

        foreach (var group in groups.Values)
        {
            // §7.4: rows in the same group are redundant only when every
            // recoverable field is identical. No winner is chosen from retention
            // date, highest play count, or furthest position — those policies
            // silently combine different moments in time.
            var states = group.Keys.Select(k => k.Row.State).ToArray();
            if (states.Distinct(RecoveryStateComparer.Exact).Count() > 1)
            {
                resolved.Add(Resolve(group, ReasonCode.InconsistentSourceState, null, IdentityEvidenceRule.NoneRule));
                continue;
            }

            var state = states[0];
            if (state.IsDefault)
            {
                resolved.Add(Resolve(group, ReasonCode.SourceHasNoEffect, state, IdentityEvidenceRule.NoneRule));
                continue;
            }

            var verdict = IdentityEvidenceRule.Evaluate(group.Keys);
            if (!verdict.IsSufficient)
            {
                if (group.Keys.Any(k => k.SeriesGuidDerived))
                {
                    blockedOnlyBySeriesGuid++;
                }

                resolved.Add(Resolve(group, ReasonCode.InsufficientIdentityEvidence, state, verdict.Rule));
                continue;
            }

            group.State = state;
            group.EvidenceRule = verdict.Rule;
            pending.Add(group);
        }

        var diagnostics = new AnalysisDiagnostics
        {
            DetachedRowsInspected = input.DetachedRows.Count,
            CurrentItemsInspected = input.CurrentItems.Count,
            EligibleTargetCount = index.EligibleItemCount,
            EligibleTargetsWithProviderKeys = index.EligibleItemsWithProviderKeyCount,
            ExclusionCounts = index.ExclusionCounts,
            DistinctCurrentKeyCount = index.DistinctKeyCount,
            KnownUserCount = input.KnownUserIds.Count,
            UniqueMatchEvidenceCounts = evidenceCounts,
            SeriesGuidEpisodeDerivedRows = seriesGuidDerivedRows,
            CandidatesBlockedOnlyBySeriesGuidEvidence = blockedOnlyBySeriesGuid,
        };

        return new CandidateSet(drafts, resolved, pending, diagnostics);
    }

    /// <summary>
    /// Classifies the remaining candidates against current target state (DESIGN §7.5).
    /// </summary>
    /// <param name="candidates">The output of <see cref="BuildCandidates"/>.</param>
    /// <param name="currentRows">
    /// Live rows for the inspected pairs. Absence of a row is meaningful: a row
    /// holding default-looking values may record an explicit unwatch or an
    /// unfavorite, which is not the same as never having been touched.
    /// </param>
    /// <returns>The completed analysis.</returns>
    public static AnalysisResult Complete(CandidateSet candidates, IEnumerable<CurrentUserDataRow> currentRows)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(currentRows);

        var byPair = new Dictionary<(Guid UserId, Guid ItemId), List<CurrentUserDataRow>>();
        foreach (var row in currentRows)
        {
            var key = (row.UserId, row.ItemId);
            if (!byPair.TryGetValue(key, out var bucket))
            {
                bucket = [];
                byPair[key] = bucket;
            }

            bucket.Add(row);
        }

        var allCandidates = new List<CandidateRecord>(candidates.ResolvedCandidates);
        var writes = new List<PlannedWrite>();

        foreach (var group in candidates.PendingCandidates)
        {
            var state = group.State!;
            byPair.TryGetValue((group.UserId, group.Target.ItemId), out var existing);

            ReasonCode reason;
            RecoveryState? currentState = null;

            if (existing is null || existing.Count == 0)
            {
                reason = ReasonCode.Ready;
            }
            else
            {
                currentState = existing[0].State;
                var allAgree = existing.All(r => RecoveryStateComparer.Semantic.Equals(r.State, state));
                reason = allAgree ? ReasonCode.AlreadyApplied : ReasonCode.CurrentStateConflict;
            }

            allCandidates.Add(Resolve(group, reason, state, group.EvidenceRule, existing?.Count ?? 0, currentState));

            if (reason == ReasonCode.Ready)
            {
                writes.Add(new PlannedWrite
                {
                    UserId = group.UserId,
                    ItemId = group.Target.ItemId,
                    State = state,
                    EvidenceRule = group.EvidenceRule,
                    SourceFingerprints = [.. group.Keys.Select(k => k.Row.Fingerprint).Order(StringComparer.Ordinal)],
                });
            }
        }

        var sourceRows = candidates.RowDrafts.Select(d => d.ToRecord()).ToArray();

        return new AnalysisResult
        {
            SourceRows = sourceRows,
            Candidates = [.. allCandidates.OrderBy(c => c.UserId).ThenBy(c => c.Target.ItemId)],
            Writes = [.. writes.OrderBy(w => w.UserId).ThenBy(w => w.ItemId)],
            RowCounts = Tally(sourceRows.Select(r => r.Reason)),
            CandidateCounts = Tally(allCandidates.Select(c => c.Reason)),
            Diagnostics = candidates.Diagnostics,
        };
    }

    private static CandidateRecord Resolve(
        GroupDraft group,
        ReasonCode reason,
        RecoveryState? state,
        string evidenceRule,
        int currentRowCount = 0,
        RecoveryState? currentState = null)
    {
        foreach (var row in group.Rows)
        {
            row.Resolve(reason);
        }

        return new CandidateRecord
        {
            UserId = group.UserId,
            Target = group.Target,
            ContributingKeys = group.Keys,
            RecoveredState = state,
            Reason = reason,
            EvidenceRule = evidenceRule,
            CurrentRowCount = currentRowCount,
            CurrentState = currentState,
        };
    }

    private static IReadOnlyDictionary<ReasonCode, int> Tally(IEnumerable<ReasonCode> reasons)
    {
        var counts = ReasonCodes.All.ToDictionary(code => code, _ => 0);
        foreach (var reason in reasons)
        {
            counts[reason]++;
        }

        return counts;
    }
}
