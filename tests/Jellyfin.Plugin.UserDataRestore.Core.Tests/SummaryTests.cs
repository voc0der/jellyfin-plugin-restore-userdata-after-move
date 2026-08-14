using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.Core.Reporting;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// The operator-facing run summary (DESIGN §10).
/// </summary>
/// <remarks>
/// This is the only account of a run most operators will ever read — the plan
/// file is opened deliberately, the log line arrives whether or not anyone asked.
/// So it is worth asserting on its wording, not just its counts.
/// </remarks>
public class SummaryTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");

    [Fact]
    public void ARunThatRestoredNothingSaysSoWithoutPromisingMore()
    {
        var summary = Render([Scenario.Row(Scenario.UserA, "tt9999999")]);

        Assert.Contains("Nothing was restored", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Run '", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatRestoredEverythingSaysWhatItDid()
    {
        // The regression this exists for: any run with a ready candidate used to
        // end "Run 'Apply detached user-data recovery' to restore them." That task
        // had been folded into this one, so it named something that did not exist
        // and told the operator the restore was still to come, moments after it
        // had happened.
        var summary = Render([Scenario.Row(Scenario.UserA, "tt0133093")]);

        Assert.Contains("Restored 1 of 1 recoverable snapshots.", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Apply detached user-data recovery", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("to restore them", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialRunCannotReadAsAWholeOne()
    {
        var summary = Render(
            [
                Scenario.Row(Scenario.UserA, "tt0133093"),
                Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
            ],
            result =>
            [
                new WriteResult(result.Writes[0], WriteOutcome.Restored, null),
                new WriteResult(result.Writes[1], WriteOutcome.Uncertain, "save_threw"),
            ]);

        Assert.Contains("Restored 1 of 2 recoverable snapshots (1 uncertain).", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOutcomeThatOccurredIsNamed()
    {
        var summary = Render(
            [
                Scenario.Row(Scenario.UserA, "tt0133093"),
                Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
            ],
            result =>
            [
                new WriteResult(result.Writes[0], WriteOutcome.Skipped, "row_exists"),
                new WriteResult(result.Writes[1], WriteOutcome.NotAttempted, ApplySequence.Cancelled),
            ]);

        Assert.Contains("Restored 0 of 2 recoverable snapshots (1 skipped, 1 not_attempted).", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSummaryStillCarriesTheClassificationCountsAndThePlanId()
    {
        var summary = Render([Scenario.Row(Scenario.UserA, "tt0133093")]);

        Assert.Contains("Inspected 1 detached rows", summary, StringComparison.Ordinal);
        Assert.Contains("Candidates: ready=1", summary, StringComparison.Ordinal);
        Assert.Contains("Plan test-plan-id written to /plans/test.json", summary, StringComparison.Ordinal);
    }

    private static string Render(
        IReadOnlyList<Model.DetachedUserDataRow> rows,
        Func<AnalysisResult, IReadOnlyList<WriteResult>>? outcomes = null)
    {
        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        return AnalysisSummary.Render(
            result,
            outcomes is null
                ? [.. result.Writes.Select(write => new WriteResult(write, WriteOutcome.Restored, null))]
                : outcomes(result),
            "test-plan-id",
            "/plans/test.json");
    }
}
