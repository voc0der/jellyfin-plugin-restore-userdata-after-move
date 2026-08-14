using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Item eligibility and path scoping (DESIGN §6.1, §7.2, §12.1).
/// </summary>
public class ScopeTests
{
    private static readonly Guid MovieId = new("74f9957e-b453-7dbb-b614-d528834acab2");

    [Theory]
    [InlineData("/data/library/tv/Show/S01E01.mkv", "/data/library/tv", true)]
    [InlineData("/data/library/tv", "/data/library/tv", true)]
    [InlineData("/data/library/tv/", "/data/library/tv", true)]
    [InlineData("/data/library/tv2/Show/S01E01.mkv", "/data/library/tv", false)]
    [InlineData("/data/library/tvshows", "/data/library/tv", false)]
    [InlineData("/data/library", "/data/library/tv", false)]
    [InlineData("/data/library/tv/../movies/x.mkv", "/data/library/tv", false)]
    [InlineData("/data/library//tv//Show/x.mkv", "/data/library/tv", true)]
    public void PrefixTestsAreComponentAware(string path, string prefix, bool expected) =>
        Assert.Equal(expected, PathScope.IsBeneath(path, prefix, StringComparison.Ordinal));

    [Fact]
    public void PrefixTestsHonourCaseSensitivity()
    {
        Assert.False(PathScope.IsBeneath("/Data/Library/TV/x.mkv", "/data/library/tv", StringComparison.Ordinal));
        Assert.True(PathScope.IsBeneath("/Data/Library/TV/x.mkv", "/data/library/tv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyPathsAndPrefixesNeverMatch()
    {
        Assert.False(PathScope.IsBeneath(null, "/data", StringComparison.Ordinal));
        Assert.False(PathScope.IsBeneath("/data/x", null, StringComparison.Ordinal));
        Assert.False(PathScope.IsBeneath("   ", "/data", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetOutsideTheFinalPathsIsReportedAsSuch()
    {
        var movie = Scenario.Movie(MovieId, path: "/mnt/staging/Test Movie (2020).mkv");

        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "tt0133093")], [movie]);

        Assert.Equal(ReasonCode.PathOutsideFinalScope, Assert.Single(result.SourceRows).Reason);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void ATargetInAnUnconfiguredLibraryIsUnsupported()
    {
        var movie = Scenario.Movie(MovieId, libraryId: Scenario.OtherLibraryId);

        var result = Scenario.Analyze([Scenario.Row(Scenario.UserA, "tt0133093")], [movie]);

        Assert.Equal(ReasonCode.UnsupportedCurrentItem, Assert.Single(result.SourceRows).Reason);
    }

    [Fact]
    public void VirtualItemsAndExtrasAreNotTargets()
    {
        var virtualItem = Scenario.Movie(MovieId) with { IsVirtualItem = true };
        var extra = Scenario.Movie(MovieId) with { IsExtraOrTrailer = true };

        Assert.Equal(ItemExclusion.VirtualOrExtra, ItemEligibility.Evaluate(virtualItem, Scenario.Options()));
        Assert.Equal(ItemExclusion.VirtualOrExtra, ItemEligibility.Evaluate(extra, Scenario.Options()));
    }

    [Fact]
    public void ItemsThatAreNotMoviesOrEpisodesAreNotTargets()
    {
        var series = Scenario.Movie(MovieId) with { Kind = ItemKind.Other };

        Assert.Equal(ItemExclusion.UnsupportedType, ItemEligibility.Evaluate(series, Scenario.Options()));
    }

    [Fact]
    public void AMissingFileIsNotATargetWhenExistenceIsRequired()
    {
        var movie = Scenario.Movie(MovieId) with { PathExists = false };

        Assert.Equal(ItemExclusion.MissingPath, ItemEligibility.Evaluate(movie, Scenario.Options(requirePathExists: true)));
        Assert.Equal(ItemExclusion.None, ItemEligibility.Evaluate(movie, Scenario.Options(requirePathExists: false)));
    }

    [Fact]
    public void MoviesAndEpisodesInScopeAreTargets()
    {
        var movie = Scenario.Movie(MovieId);
        var episode = Scenario.Episode(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(ItemExclusion.None, ItemEligibility.Evaluate(movie, Scenario.Options()));
        Assert.Equal(ItemExclusion.None, ItemEligibility.Evaluate(episode, Scenario.Options()));
    }

    [Fact]
    public void NothingIsEligibleUntilTheScopeIsConfigured()
    {
        var unconfigured = new AnalysisOptions();

        Assert.False(unconfigured.IsScopeConfigured);
        Assert.Equal(ItemExclusion.LibraryNotConfigured, ItemEligibility.Evaluate(Scenario.Movie(MovieId), unconfigured));
    }

    [Fact]
    public void TypedFoldersWinOverTheLibrariesOwnLocations()
    {
        var resolved = ScopeDefaults.ResolvePrefixes(["/data/only-here"], ["/data/library/movies", "/data/library/tv"]);

        Assert.Equal(["/data/only-here"], resolved);
    }

    [Fact]
    public void NoTypedFoldersMeansTheLibrariesOwnLocations()
    {
        // The whole point of the default: the server already knows these, and a
        // hand-typed host path that the server sees as a container path silently
        // excludes everything.
        var resolved = ScopeDefaults.ResolvePrefixes([], ["/data/library/tv", "/data/library/movies"]);

        Assert.Equal(["/data/library/movies", "/data/library/tv"], resolved);
    }

    [Fact]
    public void ResolvedFoldersAreTrimmedDeduplicatedAndOrdered()
    {
        // A configured location can arrive with a trailing separator, and the
        // prefix test is component-aware: "/data/tv/" would match nothing.
        var resolved = ScopeDefaults.ResolvePrefixes(null, ["/data/tv/", "  /data/tv  ", "/data/movies"]);

        Assert.Equal(["/data/movies", "/data/tv"], resolved);
    }

    [Fact]
    public void AServerWithNothingConfiguredResolvesToNoScope()
    {
        Assert.Empty(ScopeDefaults.ResolvePrefixes([], []));
        Assert.False(new AnalysisOptions { EligibleLibraryIds = [Scenario.LibraryId] }.IsScopeConfigured);
    }

    [Fact]
    public void NoTickedLibrariesMeansNothingIsInScope()
    {
        // Not "every library". An empty selection is the answer the page's own
        // default state posts, and reading it as the widest possible scope made
        // unticking everything the way to write into everything.
        Assert.Equal(LibrarySelectionKind.None, LibrarySelection.Parse(null).Kind);
        Assert.Equal(LibrarySelectionKind.None, LibrarySelection.Parse([]).Kind);
        Assert.Empty(LibrarySelection.Parse([]).LibraryIds);
    }

    [Fact]
    public void ConfiguredLibrariesAreParsedAndDeduplicated()
    {
        var selection = LibrarySelection.Parse(
            [Scenario.LibraryId.ToString("D"), Scenario.LibraryId.ToString("N"), Scenario.OtherLibraryId.ToString("D")]);

        Assert.Equal(LibrarySelectionKind.Explicit, selection.Kind);
        Assert.Equal([Scenario.LibraryId, Scenario.OtherLibraryId], selection.LibraryIds);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AnUnreadableSelectionIsNotAnAbsentOne(string value)
    {
        // The regression this exists for: parse, drop what failed, then ask
        // whether anything is left. Nothing is, so the run reads it as "nothing
        // ticked" and reports a clean no-op against a page still showing ticked
        // boxes — the failure this codebase keeps having to fix.
        var selection = LibrarySelection.Parse([value]);

        Assert.Equal(LibrarySelectionKind.Malformed, selection.Kind);
        Assert.Empty(selection.LibraryIds);
        Assert.Equal([value], selection.MalformedValues);
    }

    [Fact]
    public void OneUnreadableValueCondemnsTheWholeSelection()
    {
        // A partial read is still a guess about which libraries were meant, and
        // the only thing that writes this field posts IDs the server supplied.
        var selection = LibrarySelection.Parse([Scenario.LibraryId.ToString("D"), "not-a-guid"]);

        Assert.Equal(LibrarySelectionKind.Malformed, selection.Kind);
        Assert.Empty(selection.LibraryIds);
        Assert.Equal(["not-a-guid"], selection.MalformedValues);
    }

    [Fact]
    public void ItemsDroppedForAMissingFileAreCounted()
    {
        // Without this count, an unmounted share and an empty library produce the
        // same output: zero eligible targets and a successful run.
        var present = Scenario.Movie(MovieId);
        var missing = Scenario.Movie(Guid.NewGuid(), imdb: "tt7654321", tmdb: "604") with { PathExists = false };

        var index = CurrentKeyIndex.Build([present, missing], Scenario.Options(requirePathExists: true));

        Assert.Equal(1, index.EligibleItemCount);
        Assert.Equal(1, index.ExclusionCounts[ItemExclusion.MissingPath]);
        Assert.Equal(1, index.ExclusionCounts[ItemExclusion.None]);
    }
}
