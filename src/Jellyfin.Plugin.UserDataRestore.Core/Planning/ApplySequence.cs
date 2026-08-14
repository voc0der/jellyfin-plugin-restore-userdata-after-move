using Jellyfin.Plugin.UserDataRestore.Core.Analysis;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>
/// Walks the planned writes and decides when to stop walking them.
/// </summary>
/// <remarks>
/// <para>Separated from the write itself because the two answer different
/// questions. Performing a write needs a Jellyfin server; deciding whether the
/// run may continue after one needs only the outcome, and it is the decision
/// that matters — it is what bounds how much user data a run that has started
/// going wrong is allowed to touch. Keeping it here means the boundary can be
/// tested by injecting failures directly, with no server in sight, which is the
/// only way to prove the writes after a failure were never attempted.</para>
/// <para>Reasons this stops short are recorded on the untouched writes rather
/// than logged from in here, so the whole policy stays a pure function of the
/// outcomes it sees.</para>
/// </remarks>
public static class ApplySequence
{
    /// <summary>Detail recorded when a library scan began mid-run.</summary>
    public const string LibraryScanStarted = "library_scan_started";

    /// <summary>Detail prefix recorded when an earlier write ended badly.</summary>
    public const string StoppedAfter = "stopped_after_";

    /// <summary>Detail recorded when the run was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Whether a run ended because it was cancelled.
    /// </summary>
    /// <param name="results">The results of the run.</param>
    /// <returns><see langword="true"/> when cancellation is what stopped it.</returns>
    public static bool WasCancelled(IReadOnlyList<WriteResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results.Any(result =>
            result.Outcome == WriteOutcome.NotAttempted
            && string.Equals(result.Detail, Cancelled, StringComparison.Ordinal));
    }

    /// <summary>
    /// Attempts each write in order, stopping at the first that ends
    /// <see cref="WriteOutcome.Failed"/> or <see cref="WriteOutcome.Uncertain"/>.
    /// </summary>
    /// <param name="writes">The planned writes, in the order the analysis produced them.</param>
    /// <param name="attemptAsync">Performs one write and reports what became of it.</param>
    /// <param name="libraryScanIsRunning">Whether the library is mid-rebuild right now.</param>
    /// <param name="completed">Called with the number of writes disposed of so far.</param>
    /// <param name="cancellationToken">The run's cancellation token.</param>
    /// <returns>One result per planned write, in the planned order.</returns>
    /// <remarks>
    /// A <see cref="WriteOutcome.Skipped"/> write does not stop anything: a guard
    /// declining is the guard working. The other two do. Neither is about the item
    /// that went wrong — its stranded row survives, so retrying it tomorrow costs
    /// nothing — it is about the ones after it. Both say the process has stopped
    /// understanding the server it is writing to, and continuing to mutate user
    /// data past that point widens the damage on the strength of an assumption
    /// that has just been contradicted.
    /// </remarks>
    public static async Task<IReadOnlyList<WriteResult>> RunAsync(
        IReadOnlyList<PlannedWrite> writes,
        Func<PlannedWrite, CancellationToken, Task<WriteResult>> attemptAsync,
        Func<bool> libraryScanIsRunning,
        Action<int> completed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(attemptAsync);
        ArgumentNullException.ThrowIfNull(libraryScanIsRunning);
        ArgumentNullException.ThrowIfNull(completed);

        var results = new List<WriteResult>(writes.Count);

        for (var index = 0; index < writes.Count; index++)
        {
            // Recorded and returned, not thrown. Cancellation arriving after a
            // write has landed is the ordinary case — somebody presses stop in
            // Scheduled Tasks — and letting it escape from here would carry the
            // run past the plan and leave user data changed with no artifact
            // saying what changed it. The caller rethrows once that record exists.
            if (cancellationToken.IsCancellationRequested)
            {
                Abandon(results, writes, index, Cancelled);
                break;
            }

            // The run refused to start during a scan; one can still begin here. A
            // scan invalidates every remaining target at once — items are being
            // removed and recreated — so the rest of the batch is abandoned rather
            // than revalidated against a moving library.
            if (libraryScanIsRunning())
            {
                Abandon(results, writes, index, LibraryScanStarted);
                break;
            }

            WriteResult result;
            try
            {
                result = await attemptAsync(writes[index], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation reaching the attempt itself. Only the checks before
                // the save take the token, so this write is untouched too; an
                // attempt that had reached the save reports Uncertain rather than
                // throwing.
                Abandon(results, writes, index, Cancelled);
                break;
            }

            results.Add(result);
            completed(index + 1);

            if (result.Outcome is WriteOutcome.Failed or WriteOutcome.Uncertain)
            {
                Abandon(results, writes, index + 1, StoppedAfter + WriteOutcomes.ToWire(result.Outcome));
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Records every write from <paramref name="from"/> onwards as never reached.
    /// </summary>
    /// <remarks>
    /// They are recorded rather than omitted. A plan that simply left them out
    /// would read as though the analysis had never planned them, which is the one
    /// thing an abandoned run must not be able to look like.
    /// </remarks>
    private static void Abandon(
        List<WriteResult> results,
        IReadOnlyList<PlannedWrite> writes,
        int from,
        string reason)
    {
        for (var index = from; index < writes.Count; index++)
        {
            results.Add(WriteResult.NotAttempted(writes[index], reason));
        }
    }
}
