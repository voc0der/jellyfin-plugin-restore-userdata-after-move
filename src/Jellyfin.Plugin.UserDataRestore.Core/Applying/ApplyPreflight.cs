using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;

namespace Jellyfin.Plugin.UserDataRestore.Core.Applying;

/// <summary>What preflight decided about one planned write.</summary>
public enum WriteDisposition
{
    /// <summary>Preconditions still hold; write it.</summary>
    Write,

    /// <summary>The target already holds this state. Nothing to do, not a failure.</summary>
    AlreadyApplied,

    /// <summary>A precondition changed. The whole run aborts.</summary>
    Blocked,
}

/// <summary>One planned write, reconciled against the world as it is now.</summary>
/// <param name="UserId">The user.</param>
/// <param name="ItemId">The target item.</param>
/// <param name="State">The state to restore.</param>
/// <param name="Disposition">What preflight decided.</param>
/// <param name="Reason">Why, when blocked.</param>
public readonly record struct ReconciledWrite(
    Guid UserId,
    Guid ItemId,
    RecoveryState State,
    WriteDisposition Disposition,
    string Reason);

/// <summary>The outcome of the whole preflight pass.</summary>
/// <param name="Writes">Every planned write, in plan order.</param>
/// <param name="Blockers">Every reason the run must not proceed.</param>
public sealed record PreflightResult(IReadOnlyList<ReconciledWrite> Writes, IReadOnlyList<string> Blockers)
{
    /// <summary>Gets a value indicating whether the apply may proceed.</summary>
    public bool MayProceed => Blockers.Count == 0;

    /// <summary>Gets the writes that will actually be issued.</summary>
    public IEnumerable<ReconciledWrite> Pending =>
        Writes.Where(write => write.Disposition == WriteDisposition.Write);
}

/// <summary>
/// The whole-plan validation pass of DESIGN §9.1.
/// </summary>
/// <remarks>
/// <para>Preflight re-runs the analysis and reconciles the result against the
/// plan, rather than re-checking a list of conditions by hand. Anything that
/// would change a classification — a vanished item, a key that became ambiguous,
/// state appearing on a target, a source row edited underneath the plan — changes
/// the fresh classification too, and is caught by comparison.</para>
/// <para>It is all-or-nothing by design. A stale candidate must not be discovered
/// after three hundred writes have already landed.</para>
/// </remarks>
public static class ApplyPreflight
{
    /// <summary>
    /// Reconciles a plan against a freshly computed analysis.
    /// </summary>
    /// <param name="plan">The plan being applied.</param>
    /// <param name="fresh">An analysis run moments ago, on the same options.</param>
    /// <returns>The preflight outcome.</returns>
    public static PreflightResult Reconcile(PlanDocument plan, AnalysisResult fresh)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fresh);

        var blockers = new List<string>();
        var writes = new List<ReconciledWrite>();

        if (!PlanCanonicalizer.VerifyPlanId(plan))
        {
            blockers.Add("The plan file does not match its own ID: it has been edited since it was written.");
        }

        var freshByPair = fresh.Candidates.ToDictionary(
            candidate => (candidate.UserId, candidate.Target.ItemId),
            candidate => candidate);

        var freshRowsByFingerprint = fresh.SourceRows
            .Select(row => row.Row.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var planned in plan.Writes)
        {
            if (!Guid.TryParse(planned.UserId, out var userId) || !Guid.TryParse(planned.ItemId, out var itemId))
            {
                blockers.Add("A planned write does not carry parseable identifiers.");
                continue;
            }

            var label = string.Create(CultureInfo.InvariantCulture, $"user {planned.UserId} item {planned.ItemId}");

            // Every source row must still be byte-identical to what was planned.
            // A row whose state changed underneath the plan is a row somebody
            // played since, and its snapshot is no longer what was reviewed.
            var missing = planned.SourceFingerprints
                .Where(fingerprint => !freshRowsByFingerprint.Contains(fingerprint))
                .ToArray();

            if (missing.Length > 0)
            {
                blockers.Add($"The stranded rows behind {label} have changed since the plan was written.");
                writes.Add(new ReconciledWrite(userId, itemId, ToState(planned.State), WriteDisposition.Blocked, "source changed"));
                continue;
            }

            if (!freshByPair.TryGetValue((userId, itemId), out var candidate))
            {
                blockers.Add($"{label} no longer resolves to a recovery candidate.");
                writes.Add(new ReconciledWrite(userId, itemId, ToState(planned.State), WriteDisposition.Blocked, "candidate gone"));
                continue;
            }

            var plannedState = ToState(planned.State);

            if (candidate.RecoveredState is null || !RecoveryStateComparer.Exact.Equals(candidate.RecoveredState, plannedState))
            {
                blockers.Add($"The state to restore for {label} is no longer what the plan recorded.");
                writes.Add(new ReconciledWrite(userId, itemId, plannedState, WriteDisposition.Blocked, "state changed"));
                continue;
            }

            switch (candidate.Reason)
            {
                case ReasonCode.Ready:
                    writes.Add(new ReconciledWrite(userId, itemId, plannedState, WriteDisposition.Write, string.Empty));
                    break;

                // Someone applied it, or Jellyfin reattached it, between planning
                // and now. That is the intended end state, so it is a no-op rather
                // than a reason to stop.
                case ReasonCode.AlreadyApplied:
                    writes.Add(new ReconciledWrite(userId, itemId, plannedState, WriteDisposition.AlreadyApplied, "already applied"));
                    break;

                default:
                    blockers.Add(
                        $"{label} is now classified {ReasonCodes.ToWire(candidate.Reason)} rather than ready.");
                    writes.Add(new ReconciledWrite(
                        userId,
                        itemId,
                        plannedState,
                        WriteDisposition.Blocked,
                        ReasonCodes.ToWire(candidate.Reason)));
                    break;
            }
        }

        if (writes.Count != plan.Writes.Count)
        {
            blockers.Add("The plan's write list could not be read in full.");
        }

        return new PreflightResult(writes, blockers);
    }

    private static RecoveryState ToState(PlanState? state) => state is null
        ? RecoveryState.Default
        : new RecoveryState
        {
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = state.LastPlayedDate is null
                ? null
                : DateTime.Parse(state.LastPlayedDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Rating = state.Rating,
        };
}
