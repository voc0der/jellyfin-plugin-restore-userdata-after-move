using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// The check made against a recovery target immediately before writing to it.
/// </summary>
/// <remarks>
/// Analysis and apply run in one pass but not in one instant. Everything here is
/// about that gap: a target admitted seconds ago can have been refreshed, moved,
/// unmounted, or re-identified before the write reaches it, and the only thing
/// the write path used to re-check was whether the item still had default user
/// state.
/// </remarks>
public class RevalidationTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");
    private static readonly Guid SeriesId = new("2a4c31c9-0000-0000-0000-00000000000a");
    private static readonly Guid EpisodeId = new("6d1e5aa0-0000-0000-0000-00000000000b");

    [Fact]
    public void ATargetThatHasNotChangedStillQualifies()
    {
        var movie = Scenario.Movie(MovieId);

        Assert.Null(TargetRevalidation.Evaluate(movie, Scenario.Options(), ["tt0133093"]));
    }

    [Fact]
    public void ATargetThatStoppedReportingTheMatchedKeyIsRejected()
    {
        // The case that motivates the whole check: a metadata refresh between
        // analysis and apply replaces the item's IMDb ID. The item still exists, is
        // still in scope, and still has no user state — and is no longer the item
        // the stranded row was matched to.
        var refreshed = Scenario.Movie(MovieId, imdb: "tt9999999");

        var reason = TargetRevalidation.Evaluate(refreshed, Scenario.Options(), ["tt0133093"]);

        Assert.Equal("key_no_longer_reported:tt0133093", reason);
    }

    [Fact]
    public void LosingOneOfSeveralMatchedKeysIsEnoughToReject()
    {
        // The evidence rule weighed the keys as a set — two corroborating provider
        // keys is one of the ways a write is admitted at all — so a target that has
        // stopped answering to half of them no longer satisfies what admitted it.
        var partial = Scenario.Movie(MovieId, tmdb: null);

        var reason = TargetRevalidation.Evaluate(partial, Scenario.Options(), ["603", "tt0133093"]);

        Assert.Equal("key_no_longer_reported:603", reason);
    }

    [Fact]
    public void AWriteCarryingNoKeysAtAllIsRejectedRatherThanPassingVacuously()
    {
        // A revalidation with nothing to revalidate against would report every
        // target as fine, which is worse than not checking: it looks checked.
        var reason = TargetRevalidation.Evaluate(Scenario.Movie(MovieId), Scenario.Options(), []);

        Assert.Equal("key_no_longer_reported:none_recorded", reason);
    }

    [Fact]
    public void ATargetMovedOutOfTheConfiguredLibrariesIsRejected()
    {
        var moved = Scenario.Movie(MovieId, libraryId: Scenario.OtherLibraryId);

        var reason = TargetRevalidation.Evaluate(moved, Scenario.Options(), ["tt0133093"]);

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.LibraryNotConfigured), reason);
    }

    [Fact]
    public void ATargetWhosePathLeftTheFinalScopeIsRejected()
    {
        var relocated = Scenario.Movie(MovieId, path: "/somewhere/else/Test Movie (2020).mkv");

        var reason = TargetRevalidation.Evaluate(relocated, Scenario.Options(), ["tt0133093"]);

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.PathOutsideFinalScope), reason);
    }

    [Fact]
    public void ATargetWhoseFileVanishedIsRejectedWhenThePathMustExist()
    {
        // A mount dropping out mid-run. Recovering onto an item whose media is gone
        // re-strands the data on the next scan.
        var gone = Scenario.Movie(MovieId) with { PathExists = false };

        var reason = TargetRevalidation.Evaluate(gone, Scenario.Options(requirePathExists: true), ["tt0133093"]);

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.MissingPath), reason);
        Assert.Null(TargetRevalidation.Evaluate(gone, Scenario.Options(requirePathExists: false), ["tt0133093"]));
    }

    [Fact]
    public void ATargetThatBecameVirtualIsRejected()
    {
        var virtualItem = Scenario.Movie(MovieId) with { IsVirtualItem = true };

        var reason = TargetRevalidation.Evaluate(virtualItem, Scenario.Options(), ["tt0133093"]);

        Assert.Equal(ItemExclusions.ToWire(ItemExclusion.VirtualOrExtra), reason);
    }

    [Fact]
    public void EveryPlannedWriteCarriesTheKeysThatMatchedIt()
    {
        // Without this wiring the revalidation above checks nothing at run time: an
        // empty key set would reach it and it would have no identity to test.
        var result = Scenario.Analyze(
            [
                Scenario.Row(Scenario.UserA, "tt0133093"),
                Scenario.Row(Scenario.UserA, "603"),
            ],
            [Scenario.Movie(MovieId)]);

        var write = Assert.Single(result.Writes);

        Assert.Equal(["603", "tt0133093"], write.SourceKeys);
        Assert.Null(TargetRevalidation.Evaluate(Scenario.Movie(MovieId), Scenario.Options(), write.SourceKeys));
    }

    [Fact]
    public void AnEpisodeWriteCarriesTheSeriesDerivedKeyItMatchedOn()
    {
        var result = Scenario.Analyze(
            [Scenario.Row(Scenario.UserA, "tt0903747001001")],
            [Scenario.Episode(EpisodeId, SeriesId)]);

        var write = Assert.Single(result.Writes);
        var episode = Scenario.Episode(EpisodeId, SeriesId);

        Assert.Equal(["tt0903747001001"], write.SourceKeys);
        Assert.Null(TargetRevalidation.Evaluate(episode, Scenario.Options(), write.SourceKeys));

        // The series being re-identified is the episode equivalent of the movie
        // refresh above, and it changes every episode key at once.
        var reidentified = Scenario.Episode(EpisodeId, SeriesId, seriesImdb: "tt0111161");
        Assert.Equal(
            "key_no_longer_reported:tt0903747001001",
            TargetRevalidation.Evaluate(reidentified, Scenario.Options(), write.SourceKeys));
    }
}
