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
        Assert.Equal([1, 2, 3], run.Progress);
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

    private static async Task<SequenceRun> RunAsync(
        int count,
        Func<int, WriteOutcome> outcomeAt,
        Func<bool>? libraryScanIsRunning = null)
    {
        var writes = Enumerable.Range(0, count).Select(Write).ToArray();
        var attempted = new List<int>();
        var progress = new List<int>();

        var results = await ApplySequence.RunAsync(
            writes,
            (write, _) =>
            {
                var index = Array.IndexOf(writes, write);
                attempted.Add(index);
                return Task.FromResult(new WriteResult(write, outcomeAt(index), null));
            },
            libraryScanIsRunning ?? (() => false),
            progress.Add,
            CancellationToken.None);

        return new SequenceRun(writes, results, attempted, progress);
    }

    private static PlannedWrite Write(int index) => new()
    {
        UserId = Scenario.UserA,
        ItemId = new Guid(index + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
        State = new RecoveryState { Played = true, PlayCount = 1 },
        EvidenceRule = IdentityEvidenceRule.ImdbRule,
        SourceFingerprints = ["fingerprint-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        SourceKeys = ["tt000000" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
    };

    private sealed record SequenceRun(
        IReadOnlyList<PlannedWrite> Writes,
        IReadOnlyList<WriteResult> Results,
        IReadOnlyList<int> Attempted,
        IReadOnlyList<int> Progress);
}
