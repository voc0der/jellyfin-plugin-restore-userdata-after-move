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
}
