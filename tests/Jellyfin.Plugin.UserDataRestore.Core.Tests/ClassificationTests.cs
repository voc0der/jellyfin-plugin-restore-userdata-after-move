using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Source-state collapse, user resolution, and current-target inspection
/// (DESIGN §7.3-§7.6, §12.1).
/// </summary>
public class ClassificationTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");

    [Fact]
    public void RowsForADeletedUserAreReportedNotMatched()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Guid.NewGuid(), "tt0133093")],
            [Scenario.Movie(MovieId)]);

        Assert.Equal(ReasonCode.UnknownUser, Assert.Single(result.SourceRows).Reason);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void OneUsersFailureDoesNotInvalidateAnother()
    {
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "tt0133093", played: true, rating: 10),
            Scenario.Row(Guid.NewGuid(), "tt0133093"),
            Scenario.Row(Scenario.UserB, "tt0133093", played: false, rating: 1),
        };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        Assert.Equal(2, result.Writes.Count);
        Assert.Contains(result.Writes, write => write.UserId == Scenario.UserA && write.State.Rating == 10);
        Assert.Contains(result.Writes, write => write.UserId == Scenario.UserB && write.State.Rating == 1);
        Assert.Equal(1, result.RowCounts[ReasonCode.UnknownUser]);
    }

    [Fact]
    public void DisagreeingRowsInOneGroupAreInconsistentNotMerged()
    {
        // No winner is chosen from retention date, highest play count, or furthest
        // position: those policies silently combine different moments in time.
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "tt0133093", playCount: 3),
            Scenario.Row(Scenario.UserA, MovieId.ToString("D"), playCount: 7),
        };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        Assert.Empty(result.Writes);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.InconsistentSourceState, row.Reason));
        var candidate = Assert.Single(result.Candidates);
        Assert.Null(candidate.RecoveredState);
    }

    [Fact]
    public void EntirelyDefaultSourceStateProducesNoWrite()
    {
        var rows = new[] { Scenario.RowWith(Scenario.UserA, "tt0133093", RecoveryState.Default) };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        Assert.Equal(ReasonCode.SourceHasNoEffect, Assert.Single(result.SourceRows).Reason);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void DefaultStateIsReportedAsNoEffectEvenWhenEvidenceIsAlsoWeak()
    {
        // Precedence matters: counting this as an evidence failure would overstate
        // what the identity rule costs in the go/no-go numbers.
        var rows = new[] { Scenario.RowWith(Scenario.UserA, "603", RecoveryState.Default) };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        Assert.Equal(ReasonCode.SourceHasNoEffect, Assert.Single(result.SourceRows).Reason);
    }

    [Theory]
    [InlineData(-1, 0, null, "negative_play_count")]
    [InlineData(1, -5, null, "negative_position")]
    [InlineData(1, 0, 11d, "rating_out_of_range")]
    [InlineData(1, 0, -0.5d, "rating_out_of_range")]
    public void InvalidRowsAreRejectedWithAReason(int playCount, long ticks, double? rating, string expected)
    {
        var row = Scenario.Row(Scenario.UserA, "tt0133093", playCount: playCount, ticks: ticks, rating: rating);

        var result = Scenario.Analyze([row], [Scenario.Movie(MovieId)]);

        var record = Assert.Single(result.SourceRows);
        Assert.Equal(ReasonCode.InvalidSourceState, record.Reason);
        Assert.StartsWith(expected, record.Violation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RowsWithoutAKeyAreInvalid(string? key)
    {
        var row = Scenario.Row(Scenario.UserA, "tt0133093") with { CustomDataKey = key };

        var result = Scenario.Analyze([row], [Scenario.Movie(MovieId)]);

        var record = Assert.Single(result.SourceRows);
        Assert.Equal(ReasonCode.InvalidSourceState, record.Reason);
        Assert.Equal("missing_key", record.Violation);
    }

    [Fact]
    public void ImpossibleLastPlayedDatesAreInvalid()
    {
        var row = Scenario.Row(
            Scenario.UserA,
            "tt0133093",
            lastPlayed: new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = Scenario.Analyze([row], [Scenario.Movie(MovieId)]);

        var record = Assert.Single(result.SourceRows);
        Assert.Equal(ReasonCode.InvalidSourceState, record.Reason);
        Assert.StartsWith("implausible_last_played", record.Violation);
    }

    [Fact]
    public void ATargetWithNoRowsIsReady()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: []);

        Assert.Single(result.Writes);
        Assert.Equal(ReasonCode.Ready, Assert.Single(result.Candidates).Reason);
    }

    [Fact]
    public void ATargetAlreadyHoldingTheStateIsAlreadyApplied()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: [Scenario.CurrentRow(Scenario.UserA, MovieId)]);

        Assert.Empty(result.Writes);
        Assert.Equal(ReasonCode.AlreadyApplied, Assert.Single(result.Candidates).Reason);
    }

    [Fact]
    public void AlreadyAppliedToleratesSubSecondDateDrift()
    {
        // The comparison against current state has to survive a round trip through
        // the database and the user-data manager.
        var current = Scenario.CurrentRow(
            Scenario.UserA,
            MovieId,
            lastPlayed: new DateTime(2026, 1, 1, 12, 0, 0, 400, DateTimeKind.Utc));

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: [current]);

        Assert.Equal(ReasonCode.AlreadyApplied, Assert.Single(result.Candidates).Reason);
    }

    [Fact]
    public void ATargetWithDifferentStateIsAConflict()
    {
        var current = Scenario.CurrentRow(Scenario.UserA, MovieId, playCount: 99);

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: [current]);

        Assert.Empty(result.Writes);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(ReasonCode.CurrentStateConflict, candidate.Reason);
        Assert.Equal(1, candidate.CurrentRowCount);
    }

    [Fact]
    public void ARowHoldingDefaultsIsAConflictNotAnEmptyTarget()
    {
        // A default-looking row may record an explicit unwatch or unfavorite. Row
        // existence is the signal, not the values in it.
        var current = Scenario.CurrentRowWith(Scenario.UserA, MovieId, RecoveryState.Default);

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: [current]);

        Assert.Empty(result.Writes);
        Assert.Equal(ReasonCode.CurrentStateConflict, Assert.Single(result.Candidates).Reason);
    }

    [Fact]
    public void APartiallyPopulatedTargetIsNotMerged()
    {
        var current = Scenario.CurrentRowWith(
            Scenario.UserA,
            MovieId,
            RecoveryState.Default with { IsFavorite = true });

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: [current]);

        Assert.Equal(ReasonCode.CurrentStateConflict, Assert.Single(result.Candidates).Reason);
    }

    [Fact]
    public void CurrentRowsThatDisagreeWithEachOtherAreAConflict()
    {
        var rows = new[]
        {
            Scenario.CurrentRow(Scenario.UserA, MovieId),
            Scenario.CurrentRow(Scenario.UserA, MovieId, key: "tt0133093", playCount: 42),
        };

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)],
            currentRows: rows);

        Assert.Equal(ReasonCode.CurrentStateConflict, Assert.Single(result.Candidates).Reason);
    }

    [Fact]
    public void EveryRowAndCandidateEndsInExactlyOneCategory()
    {
        var otherId = Guid.NewGuid();
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "tt0133093"),
            Scenario.Row(Scenario.UserA, "603"),
            Scenario.Row(Scenario.UserB, "unmatched-key"),
            Scenario.Row(Guid.NewGuid(), "tt0133093"),
            Scenario.Row(Scenario.UserB, "tt0133093", playCount: -3),
        };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId), Scenario.Movie(otherId, tmdb: "550", imdb: "tt0137523")]);

        Assert.Equal(rows.Length, result.RowCounts.Values.Sum());
        Assert.Equal(result.Candidates.Count, result.CandidateCounts.Values.Sum());
        Assert.Equal(result.SourceRows.Count, rows.Length);
    }
}
