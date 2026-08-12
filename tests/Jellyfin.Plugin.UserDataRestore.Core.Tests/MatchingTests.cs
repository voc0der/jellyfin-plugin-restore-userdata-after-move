using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Key matching and the identity-evidence rule (DESIGN §7.2, §7.3, §12.1).
/// </summary>
public class MatchingTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly Guid OtherMovieId = new("5fc90611-0000-0000-0000-00000000000f");
    private static readonly Guid EpisodeId = new("e20d6d96-c126-ffa4-af28-fd74a4da81b2");
    private static readonly Guid SeriesId = new("03cb098f-0000-0000-0000-0000000000aa");

    [Fact]
    public void KeyMatchingIsOrdinalAndCaseSensitive()
    {
        var movie = Scenario.Movie(MovieId, imdb: "tt0133093");

        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "TT0133093")], [movie]);

        Assert.Equal(ReasonCode.NoCurrentKeyMatch, Assert.Single(result.SourceRows).Reason);
    }

    [Fact]
    public void UnmatchedKeyIsReportedAsNoMatch()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt9999999")],
            [Scenario.Movie(MovieId)]);

        Assert.Equal(ReasonCode.NoCurrentKeyMatch, Assert.Single(result.SourceRows).Reason);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void SingleMatchWithImdbKeyIsReady()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [Scenario.Movie(MovieId)]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(MovieId, write.ItemId);
        Assert.Equal(Scenario.UserA, write.UserId);
        Assert.Equal(IdentityEvidenceRule.ImdbRule, write.EvidenceRule);
    }

    [Fact]
    public void TwoCurrentItemsSharingAKeyAreAmbiguous()
    {
        // Exactly what DESIGN §17.6 produces mid-migration: the item at the vacated
        // path lingers until a later scan removes it, so both spellings of the same
        // title exist at once.
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [
                Scenario.Movie(MovieId),
                Scenario.Movie(OtherMovieId, path: "/data/library/movies/Test Movie (2020) [v2]/movie.mkv"),
            ]);

        var row = Assert.Single(result.SourceRows);
        Assert.Equal(ReasonCode.AmbiguousCurrentKey, row.Reason);
        Assert.Equal(2, row.Matches.Count);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void AKeyExposedByAnIneligibleItemTooIsStillAmbiguous()
    {
        // The duplicate sits in an unconfigured library. Restricting uniqueness to
        // configured libraries would turn a genuine "cannot tell" into a confident
        // wrong answer.
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0133093")],
            [
                Scenario.Movie(MovieId),
                Scenario.Movie(OtherMovieId, libraryId: Scenario.OtherLibraryId, path: "/mnt/other/Test Movie.mkv"),
            ]);

        Assert.Equal(ReasonCode.AmbiguousCurrentKey, Assert.Single(result.SourceRows).Reason);
    }

    [Fact]
    public void OneSnapshotUnderGuidImdbAndTmdbKeysCollapsesToOneWrite()
    {
        // The movie case from DESIGN §17.5: three rows per user, one snapshot.
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "603"),
            Scenario.Row(Scenario.UserA, MovieId.ToString("D")),
            Scenario.Row(Scenario.UserA, "tt0133093"),
        };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(3, write.SourceFingerprints.Count);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.Ready, row.Reason));
        Assert.Equal(1, result.CandidateCounts[ReasonCode.Ready]);
        Assert.Equal(3, result.RowCounts[ReasonCode.Ready]);
    }

    [Fact]
    public void EpisodeKeysDerivedFromSeriesImdbAreSufficient()
    {
        // The episode case from DESIGN §17.4: item GUID plus series IMDb + S001E001.
        var episode = Scenario.Episode(EpisodeId, SeriesId);
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "tt0903747001001"),
            Scenario.Row(Scenario.UserA, EpisodeId.ToString("D")),
        };

        var result = Scenario.Analyze(rows, [episode]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(EpisodeId, write.ItemId);
    }

    [Fact]
    public void ExactCurrentItemGuidIsSufficientOnItsOwn()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, MovieId.ToString("D"))],
            [Scenario.Movie(MovieId)]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(IdentityEvidenceRule.CurrentItemGuidRule, write.EvidenceRule);
    }

    [Fact]
    public void SingleBareNumericKeyIsInsufficientEvenWhenItsTargetIsUnique()
    {
        // A bare number carries no provider namespace and no item type, so a key
        // that is unique among current items may still have belonged to a
        // now-absent item of a different type.
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "603")],
            [Scenario.Movie(MovieId)]);

        var row = Assert.Single(result.SourceRows);
        Assert.Equal(ReasonCode.InsufficientIdentityEvidence, row.Reason);
        Assert.Equal(KeyEvidence.OtherProvider, row.Evidence);
        Assert.Equal(MovieId, row.TargetItemId);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void TwoCorroboratingProviderKeysAreSufficient()
    {
        var movie = new CurrentItemSnapshot
        {
            ItemId = MovieId,
            Kind = ItemKind.Movie,
            Name = "Provider Only",
            Path = Scenario.DefaultMoviePath,
            PathExists = true,
            LibraryIds = [Scenario.LibraryId],
            UserDataKeys = ["603", "9977"],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tmdb"] = "603",
                ["Tvdb"] = "9977",
            },
        };

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "603"), Scenario.Row(Scenario.UserA, "9977")],
            [movie]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(IdentityEvidenceRule.CorroboratingProviderKeysRule, write.EvidenceRule);
    }

    [Fact]
    public void ProviderKeysWithDifferentRetentionStampsDoNotCorroborate()
    {
        // Same state, different detach moments: the two rows are not evidence that
        // they described the same item.
        var movie = new CurrentItemSnapshot
        {
            ItemId = MovieId,
            Kind = ItemKind.Movie,
            Path = Scenario.DefaultMoviePath,
            PathExists = true,
            LibraryIds = [Scenario.LibraryId],
            UserDataKeys = ["603", "9977"],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tmdb"] = "603",
                ["Tvdb"] = "9977",
            },
        };

        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "603", retention: new DateTime(2026, 8, 12, 14, 22, 9, DateTimeKind.Utc)),
            Scenario.Row(Scenario.UserA, "9977", retention: new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc)),
        };

        var result = Scenario.Analyze(rows, [movie]);

        Assert.Empty(result.Writes);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.InsufficientIdentityEvidence, row.Reason));
    }

    [Fact]
    public void SeriesGuidDerivedEpisodeKeyIsRecordedButNotAdmitted()
    {
        var episode = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: null, includeSeriesGuidKey: true);

        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, SeriesId.ToString("D") + "001001")],
            [episode]);

        var row = Assert.Single(result.SourceRows);
        Assert.Equal(ReasonCode.InsufficientIdentityEvidence, row.Reason);
        Assert.True(row.SeriesGuidEpisodeDerived);
        Assert.Equal(1, result.Diagnostics.SeriesGuidEpisodeDerivedRows);
        Assert.Equal(1, result.Diagnostics.CandidatesBlockedOnlyBySeriesGuidEvidence);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void ItemsReportingOnlyTheirOwnGuidAreCounted()
    {
        // The canary for a host that hands back items without their metadata: the
        // run then looks exactly like "nothing is recoverable".
        var keyless = Scenario.Movie(MovieId, tmdb: null, imdb: null);
        var normal = Scenario.Movie(OtherMovieId, tmdb: "550", imdb: "tt0137523", path: "/data/library/movies/Other/Other.mkv");

        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "603")], [keyless, normal]);

        Assert.Equal(2, result.Diagnostics.EligibleTargetCount);
        Assert.Equal(1, result.Diagnostics.EligibleTargetsWithProviderKeys);
    }

    [Fact]
    public void AnalyzerNeverInventsKeysFromProviderMetadata()
    {
        // The movie carries TMDb 603, but Jellyfin never wrote a key for it — the
        // key set is the only thing that may be joined on.
        var movie = Scenario.Movie(MovieId, tmdb: null, imdb: "tt0133093");
        movie = movie with { ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt0133093", ["Tmdb"] = "603" } };

        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "603")], [movie]);

        Assert.Equal(ReasonCode.NoCurrentKeyMatch, Assert.Single(result.SourceRows).Reason);
    }
}
