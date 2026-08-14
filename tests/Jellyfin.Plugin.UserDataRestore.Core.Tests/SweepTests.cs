using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Sweep;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Guards on the sweep generator (<c>evidence/sweep/</c>).
/// </summary>
/// <remarks>
/// The generator's output is only worth reading if its model holds, and two of
/// its properties are easy to break silently: episodes must inherit their series'
/// provider IDs rather than draw their own, and a removed item's GUID must never
/// coincide with a live one. The second of those was a real bug — the stranded
/// rows matched live items by DESIGN §7.3 case 1, which pushed reported recovery
/// above 100% and put a floor under libraries with no IMDb coverage at all.
/// </remarks>
public class SweepTests
{
    /// <summary>Seeds averaged when asserting on a distribution's mean.</summary>
    private const int Seeds = 12;

    /// <summary>
    /// Series drawn per seed at the longest configured mean. The estimator's
    /// error falls with the number of *series*, not the number of episodes, so
    /// this is the figure the population is sized from.
    /// </summary>
    private const int SeriesPerSeed = 200;

    [Fact]
    public void EveryEpisodeOfASeriesSharesItsProviderIds()
    {
        var population = PopulationGenerator.Generate(new LibraryShape
        {
            Titles = 400,
            EpisodeShare = 1,
            MeanEpisodesPerSeries = 12,
        });

        var bySeries = population.Items
            .Where(item => item.Kind == ItemKind.Episode)
            .GroupBy(item => item.SeriesId!.Value);

        Assert.NotEmpty(bySeries);

        foreach (var series in bySeries)
        {
            var expected = series.First().SeriesProviderIds;

            foreach (var episode in series)
            {
                Assert.Equal(
                    expected.OrderBy(entry => entry.Key, StringComparer.Ordinal),
                    episode.SeriesProviderIds.OrderBy(entry => entry.Key, StringComparer.Ordinal));
            }
        }
    }

    [Fact]
    public void AnEpisodeKeyIsTheSeriesImdbIdWithPaddedSeasonAndEpisode()
    {
        var population = PopulationGenerator.Generate(new LibraryShape { Titles = 200, EpisodeShare = 1 });

        var episode = population.Items.First(item =>
            item.Kind == ItemKind.Episode && item.SeriesProviderIds.ContainsKey("Imdb"));

        var expected = episode.SeriesProviderIds["Imdb"]
            + episode.SeasonNumber!.Value.ToString("000", System.Globalization.CultureInfo.InvariantCulture)
            + episode.EpisodeNumber!.Value.ToString("000", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains(expected, episode.UserDataKeys);

        // Never a TMDb composite: evidence/alpha shows Jellyfin emits only the
        // IMDb one even when the series carries both IDs.
        Assert.All(
            episode.UserDataKeys.Where(key => !Guid.TryParse(key, out _)),
            key => Assert.StartsWith("tt", key, StringComparison.Ordinal));
    }

    [Fact]
    public void SeriesLengthsVary()
    {
        var population = PopulationGenerator.Generate(new LibraryShape
        {
            Titles = 1000,
            EpisodeShare = 1,
            MeanEpisodesPerSeries = 18,
        });

        var lengths = population.Items
            .Where(item => item.Kind == ItemKind.Episode)
            .GroupBy(item => item.SeriesId!.Value)
            .Select(series => series.Count())
            .ToArray();

        Assert.True(lengths.Distinct().Count() > 3, "series lengths should not be uniform");
        Assert.True(lengths.Max() > lengths.Min() * 2, "series lengths should span a wide range");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(6.0)]
    [InlineData(18.0)]
    [InlineData(60.0)]
    [InlineData(150.0)]
    public void SeriesLengthsHaveTheConfiguredMean(double configured)
    {
        // The series-length curve is plotted against this number, so it has to be
        // the number. An earlier sampler rounded a continuous exponential up and
        // capped at 400: a configured 1 produced 1.57 and a configured 150
        // produced 133.
        //
        // Every point the published sweep reports is covered, 150 included, and
        // that one is the reason this test exists: it is where a reintroduced cap
        // or a truncated tail would show first, and the only point where the
        // original defect was large enough to see by eye.
        var realized = Enumerable.Range(1, Seeds)
            .Select(seed => PopulationGenerator.Generate(new LibraryShape
            {
                // Sized against the longest configured mean rather than left at a
                // fixed count. A geometric with mean 150 drawn a handful of times
                // per seed has a standard error of tens of episodes, so a
                // population that is generous at mean 6 is noise at mean 150 —
                // and a flapping test about tail shape is worse than none.
                Titles = (int)Math.Max(4000, configured * SeriesPerSeed),
                EpisodeShare = 1,
                MeanEpisodesPerSeries = configured,
                Seed = seed,
            }).RealizedEpisodesPerSeries)
            .Average();

        Assert.InRange(realized, configured * 0.9, configured * 1.1);
    }

    [Fact]
    public void NoStrandedGuidKeyMatchesALiveItem()
    {
        var population = PopulationGenerator.Generate(new LibraryShape { MovesPerTitle = 8 });
        var live = population.Items.Select(item => item.ItemId.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var collisions = population.DetachedRows
            .Where(row => Guid.TryParse(row.CustomDataKey, out _))
            .Count(row => live.Contains(row.CustomDataKey!));

        Assert.Equal(0, collisions);
    }

    [Fact]
    public void OpportunitiesCountUserItemPairsThatHadStrandedState()
    {
        var population = PopulationGenerator.Generate(new LibraryShape { Titles = 300, MovesPerTitle = 3 });

        // One opportunity per (user, item) with rows, however many keys it left.
        var pairs = population.DetachedRows
            .Select(row => (row.UserId, row.CustomDataKey))
            .Distinct()
            .GroupBy(pair => pair.UserId)
            .Sum(user => user.Count());

        Assert.True(population.Opportunities > 0);
        Assert.True(pairs >= population.Opportunities * 3, "each opportunity should leave at least its dead GUID rows");
        Assert.InRange(population.OpportunityWeightedImdbCoverage, 0, 1);
        Assert.Equal(
            (double)population.OpportunitiesWithImdbKey / population.Opportunities,
            population.OpportunityWeightedImdbCoverage,
            12);
    }

    [Fact]
    public void TheSameSeedProducesTheSamePopulationAndDifferentSeedsDoNot()
    {
        var shape = new LibraryShape { Titles = 200, Seed = 7 };

        var first = PopulationGenerator.Generate(shape);
        var second = PopulationGenerator.Generate(shape);
        var other = PopulationGenerator.Generate(shape with { Seed = 8 });

        Assert.Equal(Fingerprint(first), Fingerprint(second));
        Assert.NotEqual(Fingerprint(first), Fingerprint(other));
    }

    [Fact]
    public void ChangingOneParameterLeavesTheRestOfThePopulationAlone()
    {
        // Sweep points are only comparable if the population underneath them is
        // held fixed. A single sequential RNG fails this: consuming one more draw
        // reshuffles every title after it.
        var shape = new LibraryShape { Titles = 300 };

        var withoutDuplicates = PopulationGenerator.Generate(shape);
        var withDuplicates = PopulationGenerator.Generate(shape with { Duplication = 0.5 });

        var before = withoutDuplicates.Items.Select(item => item.ItemId).ToHashSet();
        var after = withDuplicates.Items.Select(item => item.ItemId).ToHashSet();

        Assert.True(before.IsSubsetOf(after), "raising duplication must only add items");
        Assert.Equal(withoutDuplicates.Opportunities, withDuplicates.Opportunities);
        Assert.Equal(
            withoutDuplicates.OpportunityWeightedImdbCoverage,
            withDuplicates.OpportunityWeightedImdbCoverage,
            10);
    }

    [Fact]
    public void RecoveryEqualsOpportunityWeightedCoverageWhenNothingElseInterferes()
    {
        // With no duplicates and no pre-existing state, every opportunity whose
        // item exposes an IMDb key is recoverable and no other is. This is the
        // generator's arithmetic, checked end to end through the real analyzer —
        // not evidence about real libraries.
        var shape = new LibraryShape { Titles = 600, ImdbCoverage = 0.6 };
        var population = PopulationGenerator.Generate(shape);

        var candidates = DetachedUserDataAnalyzer.BuildCandidates(new AnalysisInput
        {
            DetachedRows = population.DetachedRows,
            CurrentItems = population.Items,
            KnownUserIds = population.UserIds,
            Options = PopulationGenerator.Options(),
        });

        var result = DetachedUserDataAnalyzer.Complete(candidates, population.CurrentRows);
        var ready = result.CandidateCounts[ReasonCode.Ready];

        Assert.Equal(population.OpportunitiesWithImdbKey, ready);
    }

    private static string Fingerprint(Population population) => string.Join(
        ";",
        population.Items.Select(item => item.ItemId.ToString("N") + ":" + string.Join(",", item.UserDataKeys)));
}
