using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// The check made against a recovery target immediately before writing to it.
/// </summary>
/// <remarks>
/// Analysis and apply run in one pass but not in one instant. Everything here is
/// about that gap: a target admitted seconds ago can have been refreshed, moved,
/// unmounted, re-identified, or joined by a duplicate before the write reaches it,
/// and the only thing the write path used to re-check was whether the item still
/// had default user state.
/// </remarks>
public class RevalidationTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly Guid OtherMovieId = new("0f2b6d13-0000-0000-0000-00000000000c");
    private static readonly Guid SeriesId = new("2a4c31c9-0000-0000-0000-00000000000a");
    private static readonly Guid EpisodeId = new("6d1e5aa0-0000-0000-0000-00000000000b");

    [Fact]
    public void ATargetThatHasNotChangedStillQualifies()
    {
        var movie = Scenario.Movie(MovieId);

        Assert.Null(TargetRevalidation.Evaluate(
            movie, Scenario.Options(), ["tt0133093"], Scenario.Ownership(movie)));
    }

    [Fact]
    public void ATargetThatStoppedReportingTheMatchedKeyIsRejected()
    {
        // The case that motivates the whole check: a metadata refresh between
        // analysis and apply replaces the item's IMDb ID. The item still exists, is
        // still in scope, and still has no user state — and is no longer the item
        // the stranded row was matched to.
        var refreshed = Scenario.Movie(MovieId, imdb: "tt9999999");

        var reason = TargetRevalidation.Evaluate(
            refreshed, Scenario.Options(), ["tt0133093"], Scenario.Ownership(refreshed));

        Assert.Equal("key_no_longer_reported:tt0133093", reason);
    }

    [Fact]
    public void LosingOneOfSeveralMatchedKeysIsEnoughToReject()
    {
        // The evidence rule weighed the keys as a set — two corroborating provider
        // keys is one of the ways a write is admitted at all — so a target that has
        // stopped answering to half of them no longer satisfies what admitted it.
        var partial = Scenario.Movie(MovieId, tmdb: null);

        var reason = TargetRevalidation.Evaluate(
            partial, Scenario.Options(), ["603", "tt0133093"], Scenario.Ownership(partial));

        Assert.Equal("key_no_longer_reported:603", reason);
    }

    [Fact]
    public void AWriteCarryingNoKeysAtAllIsRejectedRatherThanPassingVacuously()
    {
        // A revalidation with nothing to revalidate against would report every
        // target as fine, which is worse than not checking: it looks checked.
        var movie = Scenario.Movie(MovieId);

        var reason = TargetRevalidation.Evaluate(movie, Scenario.Options(), [], Scenario.Ownership(movie));

        Assert.Equal("key_no_longer_reported:none_recorded", reason);
    }

    [Fact]
    public void ASecondItemClaimingTheMatchedKeyIsRejected()
    {
        // Nothing about the target itself changed. Another item acquired its IMDb
        // ID — a refresh landing on a duplicate, a re-identification, a second copy
        // arriving in another library — and the key that carried the whole identity
        // argument now points at two things. The analysis would have called this
        // ambiguous and refused to guess; so does the write.
        var target = Scenario.Movie(MovieId);
        var duplicate = Scenario.Movie(OtherMovieId, tmdb: null, libraryId: Scenario.OtherLibraryId);

        var reason = TargetRevalidation.Evaluate(
            target, Scenario.Options(), ["tt0133093"], Scenario.Ownership(target, duplicate));

        Assert.Equal("key_no_longer_unique:tt0133093", reason);
    }

    [Fact]
    public void AKeyThatMovedToAnotherItemEntirelyIsRejected()
    {
        // The target still reports the key it was matched on, but the catalogue says
        // that key belongs to something else now. One of the two is wrong, and this
        // is not the moment to decide which.
        var target = Scenario.Movie(MovieId);
        var claimant = Scenario.Movie(OtherMovieId, tmdb: null, libraryId: Scenario.OtherLibraryId);

        var reason = TargetRevalidation.Evaluate(
            target, Scenario.Options(), ["tt0133093"], Scenario.Ownership(claimant));

        Assert.Equal("key_no_longer_unique:tt0133093", reason);
    }

    [Fact]
    public void AKeyNoCurrentItemClaimsIsRejected()
    {
        // The target reports the key and the catalogue has never heard of it, which
        // means the two disagree. Writing on the strength of a key nothing claims is
        // not a weaker version of the argument; it is the absence of one.
        var target = Scenario.Movie(MovieId);

        var reason = TargetRevalidation.Evaluate(
            target, Scenario.Options(), ["tt0133093"], Scenario.Ownership());

        Assert.Equal("key_no_longer_unique:tt0133093", reason);
    }

    [Fact]
    public void ATargetMovedOutOfTheConfiguredLibrariesIsRejected()
    {
        var moved = Scenario.Movie(MovieId, libraryId: Scenario.OtherLibraryId);

        var reason = TargetRevalidation.Evaluate(
            moved, Scenario.Options(), ["tt0133093"], Scenario.Ownership(moved));

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.LibraryNotConfigured), reason);
    }

    [Fact]
    public void ATargetWhosePathLeftTheFinalScopeIsRejected()
    {
        var relocated = Scenario.Movie(MovieId, path: "/somewhere/else/Test Movie (2020).mkv");

        var reason = TargetRevalidation.Evaluate(
            relocated, Scenario.Options(), ["tt0133093"], Scenario.Ownership(relocated));

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.PathOutsideFinalScope), reason);
    }

    [Fact]
    public void ATargetWhoseFileVanishedIsRejectedWhenThePathMustExist()
    {
        // A mount dropping out mid-run. Recovering onto an item whose media is gone
        // re-strands the data on the next scan.
        var gone = Scenario.Movie(MovieId) with { PathExists = false };
        var ownership = Scenario.Ownership(gone);

        var reason = TargetRevalidation.Evaluate(
            gone, Scenario.Options(requirePathExists: true), ["tt0133093"], ownership);

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.MissingPath), reason);
        Assert.Null(TargetRevalidation.Evaluate(
            gone, Scenario.Options(requirePathExists: false), ["tt0133093"], ownership));
    }

    [Fact]
    public void ATargetThatBecameVirtualIsRejected()
    {
        var virtualItem = Scenario.Movie(MovieId) with { IsVirtualItem = true };

        var reason = TargetRevalidation.Evaluate(
            virtualItem, Scenario.Options(), ["tt0133093"], Scenario.Ownership(virtualItem));

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.VirtualOrExtra), reason);
    }

    [Fact]
    public void EveryPlannedWriteCarriesTheKeysThatMatchedIt()
    {
        // Without this wiring the revalidation above checks nothing at run time: an
        // empty key set would reach it and it would have no identity to test.
        var movie = Scenario.Movie(MovieId);
        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt0133093"),
                Scenario.Row(Scenario.UserA, "603"),
            ],
            [movie]);

        var write = Assert.Single(result.Writes);

        Assert.Equal(["603", "tt0133093"], write.SourceKeys);
        Assert.Null(TargetRevalidation.Evaluate(
            movie, Scenario.Options(), write.SourceKeys, Scenario.Ownership(movie)));
    }

    [Fact]
    public void AnEpisodeWriteCarriesTheSeriesDerivedKeyItMatchedOn()
    {
        var episode = Scenario.Episode(EpisodeId, SeriesId);
        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "tt0903747001001")], [episode]);

        var write = Assert.Single(result.Writes);

        Assert.Equal(["tt0903747001001"], write.SourceKeys);
        Assert.Null(TargetRevalidation.Evaluate(
            episode, Scenario.Options(), write.SourceKeys, Scenario.Ownership(episode)));

        // The series being re-identified is the episode equivalent of the movie
        // refresh above, and it changes every episode key at once.
        var reidentified = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: "tt0111161");
        Assert.Equal(
            "key_no_longer_reported:tt0903747001001",
            TargetRevalidation.Evaluate(
                reidentified, Scenario.Options(), write.SourceKeys, Scenario.Ownership(reidentified)));
    }

    [Fact]
    public void AnEpisodeGainingASiblingWithTheSameSeriesAndNumberIsRejected()
    {
        // Episode keys are the series' provider ID plus zero-padded season and
        // episode numbers, so two episodes of the same series numbered alike collide
        // — a duplicate rip left behind mid-migration is the usual way.
        var episode = Scenario.Episode(EpisodeId, SeriesId);
        var twin = Scenario.Episode(OtherMovieId, SeriesId, path: "/data/library/tv/Test Show/Season 01/dupe.mkv");

        var reason = TargetRevalidation.Evaluate(
            episode, Scenario.Options(), ["tt0903747001001"], Scenario.Ownership(episode, twin));

        Assert.Equal("key_no_longer_unique:tt0903747001001", reason);
    }

    [Fact]
    public void EveryKeyIsCheckedForUniquenessAndTheyAreReportedTogether()
    {
        var target = Scenario.Movie(MovieId);
        var duplicate = Scenario.Movie(OtherMovieId, libraryId: Scenario.OtherLibraryId);

        var reason = TargetRevalidation.Evaluate(
            target, Scenario.Options(), ["603", "tt0133093"], Scenario.Ownership(target, duplicate));

        Assert.Equal("key_no_longer_unique:603 tt0133093", reason);
    }

    [Fact]
    public void OwnershipCountsAnItemOnceForAKeyItReportsTwice()
    {
        // GetUserDataKeys() is free to repeat itself, and CurrentKeyIndex already
        // reads a repeated key as one match. If ownership disagreed, an item would
        // be ambiguous with itself and every one of its writes would be skipped.
        var doubled = Scenario.Movie(MovieId) with { UserDataKeys = ["tt0133093", "tt0133093", "603"] };

        var ownership = Scenario.Ownership(doubled);

        Assert.True(ownership.IsOwnedOnlyBy("tt0133093", MovieId));
        Assert.Equal([MovieId], ownership.Owners("tt0133093"));
        Assert.Null(TargetRevalidation.Evaluate(doubled, Scenario.Options(), ["tt0133093"], ownership));
    }

    [Fact]
    public void AnIndexOverJustTheTargetAdmitsIt()
    {
        // The safety property behind the per-write uniqueness check. It is built
        // from a narrowed set of contenders, and when the narrowing finds none —
        // an item with no provider IDs to look them up by, a server that answers
        // the query differently than expected — the index holds only the target.
        // That must read as "nobody else claims these keys", not as a rejection,
        // or the check would silently stop a run from restoring anything.
        var movie = Scenario.Movie(MovieId);

        Assert.Null(TargetRevalidation.Evaluate(
            movie, Scenario.Options(), ["tt0133093", "603"], Scenario.Ownership(movie)));
    }

    [Fact]
    public void AContenderSharingOneKeyIsEnoughToStopTheWrite()
    {
        // A metadata refresh during the run gives a second item the same IMDb ID.
        // No library scan is involved, so nothing else in the write path notices.
        var target = Scenario.Movie(MovieId);
        var arrival = Scenario.Movie(OtherMovieId, path: "/data/library/movies/Test Movie (2020) [2]/movie.mkv");

        var verdict = TargetRevalidation.Evaluate(
            target, Scenario.Options(), ["tt0133093"], Scenario.Ownership(target, arrival));

        Assert.NotNull(verdict);
        Assert.StartsWith(TargetRevalidation.KeyNoLongerUnique, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void AContenderSharingNoKeysChangesNothing()
    {
        // The narrowing query is allowed to over-return: what decides is the keys
        // the contenders actually report, so an unrelated item costs a comparison
        // and no verdict.
        var target = Scenario.Movie(MovieId);
        var unrelated = Scenario.Movie(OtherMovieId, imdb: "tt7654321", tmdb: "999");

        Assert.Null(TargetRevalidation.Evaluate(
            target, Scenario.Options(), ["tt0133093"], Scenario.Ownership(target, unrelated)));
    }

    [Fact]
    public void AnEpisodeIsContestedByAnotherSeriesCarryingItsSeriesKey()
    {
        // Why episodes are looked up twice over: an episode's own provider IDs are
        // usually empty and its keys come from its series, so the duplicate that
        // matters is a second series with the same IMDb ID.
        var target = Scenario.Episode(EpisodeId, SeriesId);
        var rival = Scenario.Episode(new Guid("6d1e5aa0-0000-0000-0000-0000000000ff"), SeriesId);

        var key = Assert.Single(target.UserDataKeys, k => k.StartsWith("tt", StringComparison.Ordinal));
        var verdict = TargetRevalidation.Evaluate(
            target, Scenario.Options(), [key], Scenario.Ownership(target, rival));

        Assert.NotNull(verdict);
        Assert.StartsWith(TargetRevalidation.KeyNoLongerUnique, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipIndexesEveryItemItIsGivenAndNothingElse()
    {
        var ownership = Scenario.Ownership(Scenario.Movie(MovieId), Scenario.Episode(EpisodeId, SeriesId));

        Assert.Equal(2, ownership.ItemCount);

        // A movie's IMDb, TMDb and GUID keys, plus an episode's series-derived and
        // GUID keys.
        Assert.Equal(5, ownership.DistinctKeyCount);
        Assert.Empty(ownership.Owners("tt0111161"));
        Assert.Empty(ownership.Owners(null));
    }

    [Fact]
    public void SourcesThatHaveNotChangedStillAuthoriseTheWrite()
    {
        var rows = Sources();

        Assert.Null(SourceRevalidation.Evaluate(Fingerprints(rows), rows));
    }

    [Fact]
    public void ASourceDeletedAfterTheAnalysisStopsTheWrite()
    {
        // Jellyfin's own CleanupUserDataTask deletes sentinel rows past a
        // retention age. It needs no library scan, so the guard that abandons a
        // run mid-rebuild does not cover it, and the run would otherwise restore
        // an in-memory copy of state the server has finished with.
        var planned = Fingerprints(Sources());
        var live = Sources().Take(1).ToArray();

        var verdict = SourceRevalidation.Evaluate(planned, live);

        Assert.NotNull(verdict);
        Assert.StartsWith(SourceRevalidation.SourceGone, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySourceDisappearingStopsTheWrite()
    {
        var verdict = SourceRevalidation.Evaluate(Fingerprints(Sources()), []);

        Assert.NotNull(verdict);
        Assert.StartsWith(SourceRevalidation.SourceGone, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceSupersededAfterTheAnalysisStopsTheWrite()
    {
        // Deleting another copy of the same title replaces the sentinel row under
        // the same (user, key) with a newer snapshot. Writing the older one is not
        // just wrong once: the newer source then reads as current_state_conflict
        // against what this run left behind, so it is never restored on any later
        // run either. The stale answer becomes sticky.
        var planned = Fingerprints(Sources());
        var live = new[]
        {
            Scenario.Row(Scenario.UserA, "tt0133093", playCount: 41),
            Scenario.Row(Scenario.UserA, "603", playCount: 41),
        };

        var verdict = SourceRevalidation.Evaluate(planned, live);

        Assert.NotNull(verdict);
        Assert.StartsWith(SourceRevalidation.SourceReplaced, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceAppearingUnderTheSameKeysStopsTheWrite()
    {
        // Nothing in the analysis authorised this row, so nothing here knows what
        // it means. Declining costs a run; guessing costs the state.
        var rows = Sources();
        var live = new[] { rows[0], rows[1], Scenario.Row(Scenario.UserA, "tt0133093", favorite: false) };

        var verdict = SourceRevalidation.Evaluate(Fingerprints(rows), live);

        Assert.NotNull(verdict);
        Assert.StartsWith(SourceRevalidation.SourceAppeared, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteRecordingNoSourceAtAllIsRejectedRatherThanPassingVacuously()
    {
        Assert.Equal(SourceRevalidation.NoSourceRecorded, SourceRevalidation.Evaluate([], Sources()));
    }

    private static DetachedUserDataRow[] Sources() =>
    [
        Scenario.Row(Scenario.UserA, "tt0133093"),
        Scenario.Row(Scenario.UserA, "603"),
    ];

    private static string[] Fingerprints(IEnumerable<DetachedUserDataRow> rows) =>
        [.. rows.Select(row => row.Fingerprint).Order(StringComparer.Ordinal)];
}
