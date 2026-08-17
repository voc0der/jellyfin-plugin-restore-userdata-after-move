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
    private static readonly Guid OtherEpisodeId = new("bd21e6b4-0000-0000-0000-0000000000bb");
    private static readonly Guid OtherSeriesId = new("7c4e1a90-0000-0000-0000-0000000000cc");

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
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "603"), Scenario.Row(Scenario.UserA, "9977")],
            [ProviderOnlyMovie()]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(IdentityEvidenceRule.CorroboratingProviderKeysRule, write.EvidenceRule);
    }

    [Fact]
    public void ProviderKeysWithDifferentRetentionStampsDoNotCorroborate()
    {
        // Same state, different detach moments: the two rows are not evidence that
        // they described the same item.
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "603", retention: new DateTime(2026, 8, 12, 14, 22, 9, DateTimeKind.Utc)),
            Scenario.Row(Scenario.UserA, "9977", retention: new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc)),
        };

        var result = Scenario.Analyze(rows, [ProviderOnlyMovie()]);

        Assert.Empty(result.Writes);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.InsufficientIdentityEvidence, row.Reason));
    }

    [Fact]
    public void ProviderKeysWithNoRetentionStampAtAllDoNotCorroborate()
    {
        // Two absences are not an agreement. Everything else about these rows
        // matches — which is unremarkable, since "watched once, no rating" is what
        // most rows look like — so the stamp is the entire corroboration, and
        // reading two missing stamps as equal admits exactly the wrong-namespace
        // attribution the rule exists to refuse.
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "603") with { RetentionDate = null },
            Scenario.Row(Scenario.UserA, "9977") with { RetentionDate = null },
        };

        var result = Scenario.Analyze(rows, [ProviderOnlyMovie()]);

        Assert.Empty(result.Writes);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.InsufficientIdentityEvidence, row.Reason));
    }

    [Fact]
    public void AProviderKeyWithNoRetentionStampCannotCorroborateOneThatHasOne()
    {
        var rows = new[]
        {
            Scenario.Row(Scenario.UserA, "603"),
            Scenario.Row(Scenario.UserA, "9977") with { RetentionDate = null },
        };

        var result = Scenario.Analyze(rows, [ProviderOnlyMovie()]);

        Assert.Empty(result.Writes);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.InsufficientIdentityEvidence, row.Reason));
    }

    [Fact]
    public void ARowWithNoRetentionStampIsStillRecoverableOnItsOwnEvidence()
    {
        // The stamp is required to corroborate, not to be valid. An IMDb key needs
        // no corroboration, so losing the stamp costs this row nothing.
        var rows = new[] { Scenario.Row(Scenario.UserA, "tt0133093") with { RetentionDate = null } };

        var result = Scenario.Analyze(rows, [Scenario.Movie(MovieId)]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(IdentityEvidenceRule.ImdbRule, write.EvidenceRule);
    }

    /// <summary>A movie identified only by provider keys, with no IMDb ID to short-circuit case 3.</summary>
    private static CurrentItemSnapshot ProviderOnlyMovie() => new()
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
    public void OneEpisodeSnapshotResolvingToTwoEpisodesIsRefused()
    {
        // Jellyfin fans one save across every key the episode reported, so this
        // pair of rows is one snapshot written twice: same user, same state, same
        // retention stamp, different key. Complementary metadata after a move can
        // send the two halves to different current episodes — the episode-level
        // IMDb key to A, the series IMDb plus SSSEEE to B.
        //
        // Each half then satisfies the IMDb rule on its own, and the duplicate-key
        // guard never fires because A and B share no raw key. Grouping by
        // (user, target) put the contradiction between the two groups instead of
        // inside either, and the run copied one old snapshot onto two episodes.
        var a = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: null, path: "/data/library/tv/A/S01E01.mkv") with
        {
            UserDataKeys = ["tt7654321", EpisodeId.ToString("D")],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt7654321" },
        };

        var b = Scenario.Episode(OtherEpisodeId, OtherSeriesId, seriesImdb: "tt0903747", path: "/data/library/tv/B/S01E01.mkv");

        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt7654321"),
                Scenario.Row(Scenario.UserA, "tt0903747001001"),
            ],
            [a, b]);

        Assert.Empty(result.Writes);
        Assert.Equal(0, result.CandidateCounts[ReasonCode.Ready]);
        Assert.Equal(2, result.CandidateCounts[ReasonCode.AmbiguousSourceAttribution]);
        Assert.All(result.SourceRows, row => Assert.Equal(ReasonCode.AmbiguousSourceAttribution, row.Reason));
    }

    [Fact]
    public void AnExactGuidSettlesItsOwnSideAndRefusesTheOther()
    {
        // The same divergence, with one extra row: episode A's snapshot also
        // survives under A's own item GUID. That key is identity itself, so where
        // that snapshot belongs is no longer in question — which leaves B's claim
        // on the same payload the only doubtful one.
        //
        // The first version of this check excluded any candidate carrying a GUID
        // key, on the reasoning that such a row is not in doubt. True of the row,
        // and backwards as a rule: it took the strongest evidence on the page as a
        // reason to stop looking, so the pair was skipped and *both* writes went
        // through — the exact outcome the check exists to prevent.
        var a = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: null, path: "/data/library/tv/A/S01E01.mkv") with
        {
            UserDataKeys = ["tt7654321", EpisodeId.ToString("D")],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt7654321" },
        };

        var b = Scenario.Episode(OtherEpisodeId, OtherSeriesId, seriesImdb: "tt0903747", path: "/data/library/tv/B/S01E01.mkv");

        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt7654321"),
                Scenario.Row(Scenario.UserA, EpisodeId.ToString("D")),
                Scenario.Row(Scenario.UserA, "tt0903747001001"),
            ],
            [a, b]);

        var write = Assert.Single(result.Writes);
        Assert.Equal(EpisodeId, write.ItemId);
        Assert.Equal(IdentityEvidenceRule.CurrentItemGuidRule, write.EvidenceRule);
        Assert.Equal(1, result.CandidateCounts[ReasonCode.AmbiguousSourceAttribution]);
    }

    [Fact]
    public void TwoEpisodesEachProvenByTheirOwnGuidAreBothRecovered()
    {
        // Both sides carry a row written under their own item GUID, so each
        // snapshot is accounted for by the item that names it. Matching payloads
        // across the two are a batch deletion, not a contradiction, and refusing
        // them would throw away the strongest evidence this analyzer has.
        var a = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: null, path: "/data/library/tv/A/S01E01.mkv") with
        {
            UserDataKeys = ["tt7654321", EpisodeId.ToString("D")],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt7654321" },
        };

        var b = Scenario.Episode(OtherEpisodeId, OtherSeriesId, seriesImdb: "tt0903747", path: "/data/library/tv/B/S01E01.mkv");

        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt7654321"),
                Scenario.Row(Scenario.UserA, EpisodeId.ToString("D")),
                Scenario.Row(Scenario.UserA, "tt0903747001001"),
                Scenario.Row(Scenario.UserA, OtherEpisodeId.ToString("D")),
            ],
            [a, b]);

        Assert.Equal(0, result.CandidateCounts[ReasonCode.AmbiguousSourceAttribution]);
        Assert.Equal(2, result.Writes.Count);
    }

    [Fact]
    public void TwoEpisodesStrandedTogetherAreStillBothRecovered()
    {
        // The false positive the rule above must not produce. A batch deletion
        // stamps every row with one retention date, and "played once, never
        // resumed" is what most rows look like — so two episodes deleted in the
        // same sweep can be identical in every field but their keys, exactly like
        // one episode's fan-out.
        //
        // What tells them apart is that a real fan-out arrives whole: each of
        // these episodes keeps both its own IMDb key and its series-derived one,
        // so neither is the half-a-snapshot shape the contradiction is about.
        var first = Scenario.Episode(EpisodeId, SeriesId, path: "/data/library/tv/Show/S01E01.mkv") with
        {
            UserDataKeys = ["tt0903747001001", "tt1000001", EpisodeId.ToString("D")],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt1000001" },
        };

        var second = Scenario.Episode(OtherEpisodeId, SeriesId, episode: 2, path: "/data/library/tv/Show/S01E02.mkv") with
        {
            UserDataKeys = ["tt0903747001002", "tt1000002", OtherEpisodeId.ToString("D")],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt1000002" },
        };

        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt0903747001001"),
                Scenario.Row(Scenario.UserA, "tt1000001"),
                Scenario.Row(Scenario.UserA, "tt0903747001002"),
                Scenario.Row(Scenario.UserA, "tt1000002"),
            ],
            [first, second]);

        Assert.Equal(0, result.CandidateCounts[ReasonCode.AmbiguousSourceAttribution]);
        Assert.Equal(2, result.Writes.Count);
        Assert.Contains(result.Writes, write => write.ItemId.Equals(EpisodeId));
        Assert.Contains(result.Writes, write => write.ItemId.Equals(OtherEpisodeId));
    }

    [Fact]
    public void DivergentAttributionForDifferentSnapshotsIsNotAContradiction()
    {
        // Same two half-groups as the refusal above, but the rows disagree about
        // what happened: different play counts, different stamps. They are two
        // snapshots, not one written twice, so there is nothing contradictory
        // about them landing on two episodes.
        var a = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: null, path: "/data/library/tv/A/S01E01.mkv") with
        {
            UserDataKeys = ["tt7654321", EpisodeId.ToString("D")],
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt7654321" },
        };

        var b = Scenario.Episode(OtherEpisodeId, OtherSeriesId, seriesImdb: "tt0903747", path: "/data/library/tv/B/S01E01.mkv");

        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt7654321", playCount: 3, retention: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
                Scenario.Row(Scenario.UserA, "tt0903747001001", playCount: 9, retention: new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc)),
            ],
            [a, b]);

        Assert.Equal(0, result.CandidateCounts[ReasonCode.AmbiguousSourceAttribution]);
        Assert.Equal(2, result.Writes.Count);
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
