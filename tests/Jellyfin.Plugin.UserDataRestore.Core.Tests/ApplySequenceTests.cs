using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Where a run stops, and what it records about the writes it never reached
/// (DESIGN §4 invariant 8, §9.3, §12.4).
/// </summary>
/// <remarks>
/// Failures are injected directly. That is the point: the only way to prove the
/// writes after a failure were never attempted is to fail one on purpose and
/// then look at whether the rest were touched, and no real server can be asked
/// to do that on demand.
/// </remarks>
public class ApplySequenceTests
{
    [Fact]
    public async Task EveryWriteIsAttemptedWhenNothingGoesWrong()
    {
        var run = await RunAsync(3, _ => WriteOutcome.Restored);

        Assert.Equal([0, 1, 2], run.Attempted);
        Assert.All(run.Results, result => Assert.Equal(WriteOutcome.Restored, result.Outcome));
        Assert.Equal(run.Results, run.Recorded);
    }

    [Fact]
    public async Task ASkipDoesNotStopTheRun()
    {
        // A guard declining is the guard working. The regression in the other
        // direction would be a plugin that gives up the moment one target has
        // gained state, which on a rerun is most of them.
        var run = await RunAsync(3, index => index == 0 ? WriteOutcome.Skipped : WriteOutcome.Restored);

        Assert.Equal([0, 1, 2], run.Attempted);
        Assert.Equal(
            [WriteOutcome.Skipped, WriteOutcome.Restored, WriteOutcome.Restored],
            run.Results.Select(result => result.Outcome));
    }

    [Theory]
    [InlineData(WriteOutcome.Uncertain)]
    [InlineData(WriteOutcome.Failed)]
    public async Task LaterWritesAreNeverAttemptedAfterALostWrite(WriteOutcome outcome)
    {
        var run = await RunAsync(4, index => index == 1 ? outcome : WriteOutcome.Restored);

        // The assertion the invariant is actually about: the third and fourth
        // items were not merely recorded as untouched, they were never handed to
        // the writer at all.
        Assert.Equal([0, 1], run.Attempted);

        Assert.Equal(
            [WriteOutcome.Restored, outcome, WriteOutcome.NotAttempted, WriteOutcome.NotAttempted],
            run.Results.Select(result => result.Outcome));
    }

    [Theory]
    [InlineData(WriteOutcome.Uncertain, "stopped_after_uncertain")]
    [InlineData(WriteOutcome.Failed, "stopped_after_failed")]
    public async Task TheAbandonedWritesSayWhyTheyWereAbandoned(WriteOutcome outcome, string expected)
    {
        var run = await RunAsync(3, index => index == 0 ? outcome : WriteOutcome.Restored);

        Assert.All(
            run.Results.Where(result => result.Outcome == WriteOutcome.NotAttempted),
            result => Assert.Equal(expected, result.Detail));
    }

    [Fact]
    public async Task AFailureOnTheLastWriteLeavesNothingAbandoned()
    {
        var run = await RunAsync(2, index => index == 1 ? WriteOutcome.Failed : WriteOutcome.Restored);

        Assert.Equal([0, 1], run.Attempted);
        Assert.DoesNotContain(run.Results, result => result.Outcome == WriteOutcome.NotAttempted);
    }

    [Fact]
    public async Task EveryPlannedWriteIsAccountedForNoMatterWhereTheRunStopped()
    {
        // A plan that simply omitted the abandoned writes would read as though the
        // analysis had never planned them.
        var run = await RunAsync(5, index => index == 0 ? WriteOutcome.Uncertain : WriteOutcome.Restored);

        Assert.Equal(5, run.Results.Count);
        Assert.Equal(run.Writes, run.Results.Select(result => result.Write));
    }

    [Fact]
    public async Task AScanStartingMidRunAbandonsTheRestWithoutAttemptingThem()
    {
        var scanning = false;
        var run = await RunAsync(
            3,
            index =>
            {
                if (index == 0)
                {
                    scanning = true;
                }

                return WriteOutcome.Restored;
            },
            libraryScanIsRunning: () => scanning);

        Assert.Equal([0], run.Attempted);
        Assert.Equal(
            [WriteOutcome.Restored, WriteOutcome.NotAttempted, WriteOutcome.NotAttempted],
            run.Results.Select(result => result.Outcome));
        Assert.All(
            run.Results.Skip(1),
            result => Assert.Equal(ApplySequence.LibraryScanStarted, result.Detail));
    }

    [Fact]
    public async Task AScanAlreadyRunningAttemptsNothing()
    {
        var run = await RunAsync(2, _ => WriteOutcome.Restored, libraryScanIsRunning: () => true);

        Assert.Empty(run.Attempted);
        Assert.All(run.Results, result => Assert.Equal(WriteOutcome.NotAttempted, result.Outcome));
    }

    [Fact]
    public async Task CancellationAfterASuccessfulWriteReturnsRatherThanThrowing()
    {
        // The regression this exists for: cancellation escaped the loop, so it
        // escaped ExecuteAsync too - past the closing fingerprint and past the
        // plan. A run stopped from Scheduled Tasks left restored state behind with
        // no artifact recording that it had ever run.
        using var cancellation = new CancellationTokenSource();

        var run = await RunAsync(
            3,
            index =>
            {
                if (index == 0)
                {
                    cancellation.Cancel();
                }

                return WriteOutcome.Restored;
            },
            cancellationToken: cancellation.Token);

        Assert.Equal([0], run.Attempted);
        Assert.Equal(
            [WriteOutcome.Restored, WriteOutcome.NotAttempted, WriteOutcome.NotAttempted],
            run.Results.Select(result => result.Outcome));
        Assert.True(ApplySequence.WasCancelled(run.Results));
    }

    [Fact]
    public async Task CancellationReachingTheWriteItselfLeavesThatWriteUnattempted()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var writes = Enumerable.Range(0, 2).Select(Write).ToArray();
        var attempted = 0;

        var results = await ApplySequence.RunAsync(
            writes,
            (_, token) =>
            {
                attempted++;
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            },
            () => false,
            _ => { },
            cancellation.Token);

        Assert.Equal(0, attempted);
        Assert.All(results, result => Assert.Equal(WriteOutcome.NotAttempted, result.Outcome));
        Assert.True(ApplySequence.WasCancelled(results));
    }

    [Fact]
    public async Task EveryResultIsReportedAsItIsDecided()
    {
        // What the run ledger is built on. A callback that only fired for
        // attempted writes, or only after the loop, would leave the abandoned
        // ones out of the durable record and put the whole record after the last
        // mutation - which is the position the plan is already in, and the reason
        // it is not enough on its own.
        var run = await RunAsync(4, index => index == 1 ? WriteOutcome.Uncertain : WriteOutcome.Restored);

        Assert.Equal(run.Results, run.Recorded);
        Assert.Equal(4, run.Recorded.Count);
    }

    [Fact]
    public async Task AResultIsRecordedBeforeTheNextWriteIsAttempted()
    {
        // Ordering, not just coverage: a line flushed after the following write
        // has already landed describes a database state that has moved on.
        var order = new List<string>();
        var writes = Enumerable.Range(0, 3).Select(Write).ToArray();

        await ApplySequence.RunAsync(
            writes,
            (write, _) =>
            {
                order.Add("attempt");
                return Task.FromResult(new WriteResult(write, WriteOutcome.Restored, null));
            },
            () => false,
            _ => order.Add("record"),
            CancellationToken.None);

        Assert.Equal(["attempt", "record", "attempt", "record", "attempt", "record"], order);
    }

    [Fact]
    public async Task AbandonedWritesAreRecordedToo()
    {
        var run = await RunAsync(3, index => index == 0 ? WriteOutcome.Failed : WriteOutcome.Restored);

        Assert.Equal(
            [WriteOutcome.Failed, WriteOutcome.NotAttempted, WriteOutcome.NotAttempted],
            run.Recorded.Select(result => result.Outcome));
    }

    [Fact]
    public async Task ARunThatFinishedWasNotCancelled()
    {
        var run = await RunAsync(2, _ => WriteOutcome.Restored);

        Assert.False(ApplySequence.WasCancelled(run.Results));
    }

    [Fact]
    public async Task AbandoningForAScanIsNotReportedAsCancellation()
    {
        var run = await RunAsync(2, _ => WriteOutcome.Restored, libraryScanIsRunning: () => true);

        Assert.False(ApplySequence.WasCancelled(run.Results));
    }

    private static async Task<SequenceRun> RunAsync(
        int count,
        Func<int, WriteOutcome> outcomeAt,
        Func<bool>? libraryScanIsRunning = null,
        CancellationToken cancellationToken = default)
    {
        var writes = Enumerable.Range(0, count).Select(Write).ToArray();
        var attempted = new List<int>();
        var recorded = new List<WriteResult>();

        var results = await ApplySequence.RunAsync(
            writes,
            (write, _) =>
            {
                var index = Array.IndexOf(writes, write);

                // Recorded-before-attempted is the property the ledger depends
                // on, so the fake captures the ordering rather than assuming it.
                attempted.Add(index);
                return Task.FromResult(new WriteResult(write, outcomeAt(index), null));
            },
            libraryScanIsRunning ?? (() => false),
            recorded.Add,
            cancellationToken);

        return new SequenceRun(writes, results, attempted, recorded);
    }

    private static PlannedWrite Write(int index) => new()
    {
        UserId = Scenario.UserA,
        ItemId = new Guid(index + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
        State = new RecoveryState { Played = true, PlayCount = 1 },
        EvidenceRule = IdentityEvidenceRule.ImdbRule,
        SourceFingerprints = ["fingerprint-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        SourceKeys = ["tt000000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        TargetKeys = ["tt000000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        SentinelFingerprints = ["fingerprint-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
    };

    private sealed record SequenceRun(
        IReadOnlyList<PlannedWrite> Writes,
        IReadOnlyList<WriteResult> Results,
        IReadOnlyList<int> Attempted,
        IReadOnlyList<WriteResult> Recorded);
}
