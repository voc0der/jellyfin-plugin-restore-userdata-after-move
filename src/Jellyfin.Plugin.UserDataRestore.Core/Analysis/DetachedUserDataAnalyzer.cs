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

        RecordSentinelState(pending, input.DetachedRows);

        foreach (var contradicted in FindDivergentEpisodeAttribution(pending))
        {
            pending.Remove(contradicted);
            resolved.Add(Resolve(
                contradicted,
                ReasonCode.AmbiguousSourceAttribution,
                contradicted.State,
                contradicted.EvidenceRule));
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
                    SourceKeys = [.. group.Keys
                        .Select(k => k.Row.CustomDataKey!)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)],
                    TargetKeys = [.. group.Target.UserDataKeys
                        .Where(key => !string.IsNullOrEmpty(key))
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)],
                    SentinelFingerprints = group.SentinelFingerprints,
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

    /// <summary>
    /// Records, for each remaining candidate, the whole of the sentinel that
    /// concerns it — every detached row this user holds under any key the target
    /// reports (DESIGN §9.1).
    /// </summary>
    /// <remarks>
    /// <para>Not just the rows the candidate was built from. Between the analysis
    /// and the write, another deletion can strand a <i>newer</i> snapshot under a
    /// key this target also answers to but that contributed nothing here — and a
    /// re-read of only the contributing keys never asks about it. The old state
    /// would then be written, Jellyfin would fan it out across every key the
    /// target reports including that one, and the newer snapshot would read as
    /// <c>current_state_conflict</c> against it on that run and every run after.
    /// The stale answer becomes permanent.</para>
    /// <para>Recording the rows the analysis <i>declined</i> matters as much as
    /// recording the ones it used. A row with an impossible rating, or one whose
    /// key a second item also answers to, sits under a target key and was
    /// deliberately left out of the candidate. If the baseline held only the
    /// contributing rows, that row would look like something that had just
    /// arrived, and every write with one nearby would be refused forever.</para>
    /// </remarks>
    private static void RecordSentinelState(
        IReadOnlyList<GroupDraft> pending,
        IReadOnlyList<DetachedUserDataRow> detachedRows)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var byUserAndKey = new Dictionary<(Guid UserId, string Key), List<DetachedUserDataRow>>();
        foreach (var row in detachedRows)
        {
            if (string.IsNullOrEmpty(row.CustomDataKey))
            {
                continue;
            }

            var pair = (row.UserId, row.CustomDataKey);
            if (!byUserAndKey.TryGetValue(pair, out var bucket))
            {
                bucket = [];
                byUserAndKey[pair] = bucket;
            }

            bucket.Add(row);
        }

        foreach (var group in pending)
        {
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in group.Target.UserDataKeys)
            {
                if (string.IsNullOrEmpty(key) || !byUserAndKey.TryGetValue((group.UserId, key), out var rows))
                {
                    continue;
                }

                foreach (var row in rows)
                {
                    fingerprints.Add(row.Fingerprint);
                }
            }

            group.SentinelFingerprints = [.. fingerprints.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Finds candidates whose evidence contradicts another candidate's, across the
    /// grouping boundary (DESIGN §7.4).
    /// </summary>
    /// <remarks>
    /// <para>Jellyfin writes one save under every key the item reported, so a
    /// single episode's stranded state arrives as several rows differing only in
    /// their key: the episode's own IMDb ID, the series' IMDb ID with
    /// <c>SSSEEE</c> appended, the item GUID. They are meant to converge on one
    /// current item.</para>
    /// <para>When they do not — the episode-level key resolving uniquely to
    /// episode A while the series-derived one resolves uniquely to episode B —
    /// nothing downstream notices. Each group passes the IMDb rule on its own
    /// merits, and the duplicate-key guard cannot fire because A and B share no
    /// raw key. Grouping by <c>(user, target)</c> is what hides it: the
    /// contradiction lives between two groups rather than inside either, and the
    /// result is one old snapshot copied onto two current episodes.</para>
    /// <para>Deliberately narrow. A batch deletion stamps a whole library's rows
    /// with one retention date, so identical payloads are not on their own
    /// evidence that two rows described one item — clustering on that would refuse
    /// entire recoveries. What distinguishes this shape is that each side is
    /// <i>missing</i> the key the other has. An episode whose own IMDb key and
    /// series-derived key both point at it produces one complete group, not two
    /// half ones; two episodes deleted in the same sweep each keep both halves,
    /// however alike their state.</para>
    /// <para>A row stored under the current item's own GUID settles where its
    /// snapshot belongs — that key <i>is</i> item identity — so a side carrying
    /// one is kept and only the side with nothing to stand on is refused. Both
    /// sides carrying one is not a contradiction at all: each item accounts for
    /// its own snapshot, and the matching payloads are a batch deletion.</para>
    /// </remarks>
    private static IReadOnlyList<GroupDraft> FindDivergentEpisodeAttribution(IReadOnlyList<GroupDraft> pending)
    {
        var contradicted = new List<GroupDraft>();

        foreach (var byUser in pending.GroupBy(group => group.UserId))
        {
            var ownKeyOnly = byUser.Where(group => Carries(group, KeyEvidence.Imdb, KeyEvidence.SeriesImdbEpisode)).ToArray();
            var seriesKeyOnly = byUser.Where(group => Carries(group, KeyEvidence.SeriesImdbEpisode, KeyEvidence.Imdb)).ToArray();

            foreach (var own in ownKeyOnly)
            {
                foreach (var series in seriesKeyOnly)
                {
                    if (own.Target.ItemId.Equals(series.Target.ItemId) || !SharesAPayload(own, series))
                    {
                        continue;
                    }

                    var ownProven = ProvenByItemGuid(own);
                    var seriesProven = ProvenByItemGuid(series);

                    // Both sides hold a row stored under the current item's own
                    // GUID, so each snapshot is accounted for by the item it
                    // names. Identical payloads are then what a batch deletion
                    // looks like, not a contradiction.
                    if (ownProven && seriesProven)
                    {
                        continue;
                    }

                    // Otherwise the proven side stands and only the side with
                    // nothing to stand on is refused. A GUID key is item identity
                    // itself: the row was written for exactly that item, which
                    // settles where that snapshot belongs and leaves the *other*
                    // claim on it the one in doubt. Excluding a GUID-bearing
                    // candidate from this check entirely — as the first version of
                    // it did — read that strength backwards, and let the
                    // unsupported write through beside the proven one.
                    if (!ownProven)
                    {
                        contradicted.Add(own);
                    }

                    if (!seriesProven)
                    {
                        contradicted.Add(series);
                    }
                }
            }
        }

        return [.. contradicted.Distinct()];
    }

    private static bool ProvenByItemGuid(GroupDraft group) =>
        group.Keys.Any(key => key.Evidence == KeyEvidence.CurrentItemGuid);

    private static bool Carries(GroupDraft group, KeyEvidence required, KeyEvidence disqualifying) =>
        group.Target.Kind == ItemKind.Episode
        && group.Keys.Any(key => key.Evidence == required)
        && !group.Keys.Any(key => key.Evidence == disqualifying);

    private static bool SharesAPayload(GroupDraft left, GroupDraft right)
    {
        var payloads = left.Keys.Select(key => key.Row.PayloadFingerprint).ToHashSet(StringComparer.Ordinal);
        return right.Keys.Any(key => payloads.Contains(key.Row.PayloadFingerprint));
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
