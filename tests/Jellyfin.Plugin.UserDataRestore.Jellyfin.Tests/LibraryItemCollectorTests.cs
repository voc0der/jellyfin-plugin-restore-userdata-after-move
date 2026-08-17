using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Jellyfin;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

/// <summary>
/// What the adapter hands the analyzer, and what it touches to produce it.
/// </summary>
/// <remarks>
/// The core tests reason about key sets someone typed into a fixture. These
/// reason about the key sets a real Jellyfin <c>Episode</c> and <c>Movie</c>
/// produce, and about the questions this plugin asks the server on the way —
/// which is where both of the defects below lived, invisible to every core test
/// that assumed the collector had already found what it was given.
/// </remarks>
public class LibraryItemCollectorTests
{
    private static readonly Guid Selected = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Unselected = new("22222222-2222-2222-2222-222222222222");

    private static AnalysisOptions Options(bool requirePathExists = true) => new()
    {
        EligibleLibraryIds = [Selected],
        FinalPathPrefixes = ["/data/library/movies", "/data/library/tv"],
        PathComparison = StringComparison.Ordinal,
        RequirePathExists = requirePathExists,
        NowUtc = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void TheMediaFileIsStatedOnlyForItemsInASelectedLibrary()
    {
        // The cost this guards is not hypothetical: an unselected library on an
        // unavailable network mount used to be stat-ed item by item by a run
        // scoped to one small local library, which is how a task with nothing to
        // do takes minutes and looks hung.
        var library = FakeLibrary.Create();
        var inScope = library.AddMovie("The Matrix", Selected, new() { ["Imdb"] = "tt0133093" });
        var outOfScope = library.AddMovie("Fight Club", Unselected, new() { ["Imdb"] = "tt0137523" });

        var probed = new List<string?>();
        var collector = new LibraryItemCollector(library.Manager, path =>
        {
            probed.Add(path);
            return true;
        });

        collector.Collect([Selected], checkPathExists: true, CancellationToken.None);

        Assert.Equal([inScope.Path], probed);
        Assert.DoesNotContain(outOfScope.Path, probed);
    }

    [Fact]
    public void NothingIsStatedAtAllWhenTheCheckIsOff()
    {
        var library = FakeLibrary.Create();
        library.AddMovie("The Matrix", Selected);

        var probed = 0;
        var collector = new LibraryItemCollector(library.Manager, _ =>
        {
            probed++;
            return true;
        });

        collector.Collect([Selected], checkPathExists: false, CancellationToken.None);

        Assert.Equal(0, probed);
    }

    [Fact]
    public void AnUnselectedItemIsStillCollectedAndStillMakesASharedKeyAmbiguous()
    {
        // The reason every item is collected in the first place. Skipping the
        // stat must not turn into skipping the item: a second copy of the same
        // title in a library nobody ticked means a stranded row genuinely cannot
        // be attributed to one of them, and narrowing the index would turn that
        // ambiguity into a confident wrong answer.
        var library = FakeLibrary.Create();
        library.AddMovie("The Matrix", Selected, new() { ["Imdb"] = "tt0133093" });
        library.AddMovie("The Matrix", Unselected, new() { ["Imdb"] = "tt0133093" });

        var collector = new LibraryItemCollector(library.Manager, _ => true);
        var snapshots = collector.Collect([Selected], checkPathExists: true, CancellationToken.None);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(KeyMatchKind.Ambiguous, CurrentKeyIndex.Build(snapshots, Options()).Lookup("tt0133093").Kind);
    }

    [Fact]
    public void AMissingFileOutsideTheSelectionIsNotEvidenceOfAMissingMount()
    {
        // The missing-mount warning fires when the items dropped for a missing
        // file outnumber the ones that qualified. Counting titles on a mount the
        // operator deliberately left out of scope made that warning fire about
        // libraries whose files were all present.
        var library = FakeLibrary.Create();
        library.AddMovie("The Matrix", Selected, new() { ["Imdb"] = "tt0133093" });
        library.AddMovie("Fight Club", Unselected, new() { ["Imdb"] = "tt0137523" });
        library.AddMovie("Heat", Unselected, new() { ["Imdb"] = "tt0113277" });

        // Nothing is where it claims to be, in or out of scope.
        var collector = new LibraryItemCollector(library.Manager, _ => false);
        var index = CurrentKeyIndex.Build(
            collector.Collect([Selected], checkPathExists: true, CancellationToken.None),
            Options());

        Assert.Equal(1, index.ExclusionCounts[ItemExclusion.MissingPath]);
        Assert.Equal(2, index.ExclusionCounts[ItemExclusion.LibraryNotConfigured]);
        Assert.Equal(0, index.EligibleItemCount);
    }

    [Fact]
    public void AnEpisodeReportsTheKeysItsSeriesDerives()
    {
        // The assumption every core fixture is written against, asserted once
        // against Jellyfin's own GetUserDataKeys() rather than restated.
        var library = FakeLibrary.Create();
        var series = library.AddSeries("Breaking Bad", Selected, new() { ["Imdb"] = "tt0903747" });
        var episode = library.AddEpisode(series, Selected, season: 1, episode: 1);

        var snapshot = new LibraryItemCollector(library.Manager, _ => true)
            .Snapshot(episode, [Selected], checkPathExists: true);

        Assert.Contains("tt0903747001001", snapshot.UserDataKeys);
        Assert.Contains(episode.Id.ToString("D"), snapshot.UserDataKeys);
    }

    [Fact]
    public void ASiblingRenumberedOntoTheTargetIsFoundBeforeTheWrite()
    {
        // Two episodes of one series, neither carrying provider IDs of its own —
        // the ordinary case, since an episode's user-data identity comes from its
        // series plus its numbering.
        var library = FakeLibrary.Create();
        var series = library.AddSeries("Breaking Bad", Selected, new() { ["Imdb"] = "tt0903747" });
        var target = library.AddEpisode(series, Selected, season: 1, episode: 1);
        var sibling = library.AddEpisode(series, Selected, season: 1, episode: 2);

        var collector = new LibraryItemCollector(library.Manager, _ => true);

        // The run-wide ownership index, built once at the top of the apply pass,
        // while the two episodes still hold different keys.
        var runWide = collector.BuildKeyOwnership(CancellationToken.None);

        // A metadata refresh lands mid-run. It needs no library scan, so none of
        // the guards that abandon the batch fire.
        FakeLibrary.Renumber(sibling, season: 1, number: 1);

        var snapshot = collector.Snapshot(target, [Selected], checkPathExists: true);
        var contested = "tt0903747001001";

        // The run-wide index is now a photograph of a catalogue that no longer
        // exists, and it still says the write is fine. That is what makes the live
        // lookup the only thing standing between this and a write onto a key with
        // two owners.
        Assert.Null(TargetRevalidation.Evaluate(snapshot, Options(), [contested], runWide));

        var contenders = collector.FindKeyContenders(target, CancellationToken.None);
        Assert.Contains(contenders, candidate => candidate.ItemId.Equals(sibling.Id));

        var live = KeyOwnership.Build([snapshot, .. contenders]);
        var verdict = TargetRevalidation.Evaluate(snapshot, Options(), [contested], live);

        Assert.NotNull(verdict);
        Assert.StartsWith(TargetRevalidation.KeyNoLongerUnique, verdict, StringComparison.Ordinal);
        Assert.Contains(contested, verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntouchedSiblingIsNotMistakenForAContestedKey()
    {
        // The other half of the same claim: the sibling lookup widens the search,
        // and widening a search must not by itself disqualify anything. Episodes
        // that still hold their own numbers own their own keys.
        var library = FakeLibrary.Create();
        var series = library.AddSeries("Breaking Bad", Selected, new() { ["Imdb"] = "tt0903747" });
        var target = library.AddEpisode(series, Selected, season: 1, episode: 1);
        library.AddEpisode(series, Selected, season: 1, episode: 2);

        var collector = new LibraryItemCollector(library.Manager, _ => true);
        var snapshot = collector.Snapshot(target, [Selected], checkPathExists: true);
        var contenders = collector.FindKeyContenders(target, CancellationToken.None);

        Assert.NotEmpty(contenders);
        Assert.Null(TargetRevalidation.Evaluate(
            snapshot, Options(), ["tt0903747001001"], KeyOwnership.Build([snapshot, .. contenders])));
    }

    [Fact]
    public void AnEpisodeOfARivalSeriesSharingAProviderIdIsStillAContender()
    {
        // The case the narrowing query was written for, kept honest now that the
        // target's own series is enumerated as well.
        var library = FakeLibrary.Create();
        var series = library.AddSeries("Breaking Bad", Selected, new() { ["Imdb"] = "tt0903747" });
        var duplicate = library.AddSeries("Breaking Bad", Unselected, new() { ["Imdb"] = "tt0903747" });
        var target = library.AddEpisode(series, Selected, season: 1, episode: 1);
        var rival = library.AddEpisode(duplicate, Unselected, season: 1, episode: 1);

        var collector = new LibraryItemCollector(library.Manager, _ => true);
        var snapshot = collector.Snapshot(target, [Selected], checkPathExists: true);
        var contenders = collector.FindKeyContenders(target, CancellationToken.None);

        Assert.Contains(contenders, candidate => candidate.ItemId.Equals(rival.Id));
        Assert.NotNull(TargetRevalidation.Evaluate(
            snapshot, Options(), ["tt0903747001001"], KeyOwnership.Build([snapshot, .. contenders])));
    }
}
