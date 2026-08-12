using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>One generated installation, ready to hand to the analyzer.</summary>
/// <param name="Items">Every current movie and episode on the server.</param>
/// <param name="DetachedRows">The sentinel rows a path change left behind.</param>
/// <param name="CurrentRows">Live user-data rows on the current items.</param>
/// <param name="UserIds">The surviving users.</param>
/// <param name="Opportunities">(user, item) pairs that had stranded state.</param>
/// <param name="OpportunitiesWithImdbKey">
/// Of those, the ones whose current item exposes an IMDb-derived key — the
/// coverage figure that actually predicts recovery.
/// </param>
/// <param name="CatalogItems">Current items in the configured libraries.</param>
/// <param name="CatalogItemsWithImdbKey">Of those, the ones exposing an IMDb-derived key.</param>
/// <param name="SeriesCount">How many series were generated.</param>
/// <param name="EpisodeCount">How many episodes were generated.</param>
public sealed record Population(
    IReadOnlyList<CurrentItemSnapshot> Items,
    IReadOnlyList<DetachedUserDataRow> DetachedRows,
    IReadOnlyList<CurrentUserDataRow> CurrentRows,
    IReadOnlySet<Guid> UserIds,
    int Opportunities,
    int OpportunitiesWithImdbKey,
    int CatalogItems,
    int CatalogItemsWithImdbKey,
    int SeriesCount,
    int EpisodeCount)
{
    /// <summary>
    /// Gets the mean episodes per series actually produced, which is what the
    /// series-length curve must be plotted against rather than the requested mean.
    /// </summary>
    public double RealizedEpisodesPerSeries => SeriesCount == 0 ? 0 : (double)EpisodeCount / SeriesCount;

    /// <summary>Gets IMDb coverage weighted by stranded state, the predictive measure.</summary>
    public double OpportunityWeightedImdbCoverage =>
        Opportunities == 0 ? 0 : (double)OpportunitiesWithImdbKey / Opportunities;

    /// <summary>Gets IMDb coverage weighted by catalog item, the measurable proxy.</summary>
    public double ItemWeightedImdbCoverage =>
        CatalogItems == 0 ? 0 : (double)CatalogItemsWithImdbKey / CatalogItems;
}

/// <summary>
/// Builds a synthetic installation from a <see cref="LibraryShape"/>.
/// </summary>
/// <remarks>
/// <para>Keys are the ones Jellyfin emits, in the order it emits them: a movie
/// reports IMDb, a bare TMDb number, then its own GUID; an episode reports the
/// <em>series'</em> IMDb ID with zero-padded season and episode, then its own
/// GUID. In <c>evidence/alpha</c> the episode's series carried both an IMDb and a
/// TMDb ID and Jellyfin still emitted only the IMDb composite, so an episode
/// never has a second provider key to corroborate with.</para>
/// <para>Series are real entities here: coverage is drawn once per series and
/// inherited by all of its episodes, and series lengths vary. That coupling is the
/// point — it is what makes catalog-level coverage an unreliable predictor of how
/// much watch history comes back.</para>
/// </remarks>
public static class PopulationGenerator
{
    /// <summary>The library the analyzer is configured to write into.</summary>
    public static readonly Guid ConfiguredLibrary = new("11111111-1111-1111-1111-111111111111");

    /// <summary>A library the operator did not configure, where duplicates live.</summary>
    public static readonly Guid UnconfiguredLibrary = new("22222222-2222-2222-2222-222222222222");

    private const int EpisodesPerSeason = 13;

    private static readonly DateTime Retention = new(2026, 8, 12, 14, 22, 9, DateTimeKind.Utc);
    private static readonly DateTime LastPlayed = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Generates one installation.
    /// </summary>
    /// <param name="shape">The library shape to realize.</param>
    /// <returns>The generated population.</returns>
    public static Population Generate(LibraryShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var builder = new Builder(shape);
        builder.AddMovies();
        builder.AddSeries();
        return builder.Build();
    }

    /// <summary>
    /// The scope the analyzer runs under.
    /// </summary>
    /// <returns>The analysis options.</returns>
    public static AnalysisOptions Options() => new()
    {
        EligibleLibraryIds = [ConfiguredLibrary],
        FinalPathPrefixes = ["/data/movies", "/data/tv"],
        PathComparison = StringComparison.Ordinal,
        RequirePathExists = false,
        NowUtc = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc),
    };

    private static string Imdb(Deterministic.Kind kind, int index) =>
        "tt" + ((int)kind).ToString(CultureInfo.InvariantCulture) + index.ToString("0000000", CultureInfo.InvariantCulture);

    // Bare, with no provider namespace, exactly as Jellyfin stores it.
    private static string Tmdb(Deterministic.Kind kind, int index) =>
        (((int)kind * 10_000_000) + 500_000 + index).ToString(CultureInfo.InvariantCulture);

    private sealed class Builder(LibraryShape shape)
    {
        private readonly List<CurrentItemSnapshot> _items = [];
        private readonly List<DetachedUserDataRow> _detached = [];
        private readonly List<CurrentUserDataRow> _current = [];
        private readonly Guid[] _users = [.. Enumerable.Range(0, shape.Users).Select(NthUser)];

        private int _opportunities;
        private int _opportunitiesWithImdb;
        private int _catalogItems;
        private int _catalogItemsWithImdb;
        private int _removedItems;
        private int _duplicates;
        private int _seriesCount;
        private int _episodeCount;

        public void AddMovies()
        {
            var count = (int)Math.Round(shape.Titles * (1 - shape.EpisodeShare));

            for (var index = 0; index < count; index++)
            {
                var imdb = Draw(Deterministic.Kind.Movie, index, Deterministic.Slot.Imdb) < shape.ImdbCoverage
                    ? Imdb(Deterministic.Kind.Movie, index)
                    : null;
                var tmdb = Draw(Deterministic.Kind.Movie, index, Deterministic.Slot.Tmdb) < shape.TmdbCoverage
                    ? Tmdb(Deterministic.Kind.Movie, index)
                    : null;

                var itemId = Deterministic.Identity(shape.Seed, Deterministic.Kind.Movie, index);
                var providers = Providers(imdb, tmdb);
                var keys = new List<string>();

                if (imdb is not null)
                {
                    keys.Add(imdb);
                }

                if (tmdb is not null)
                {
                    keys.Add(tmdb);
                }

                var item = new CurrentItemSnapshot
                {
                    ItemId = itemId,
                    Kind = ItemKind.Movie,
                    Name = "Movie " + index.ToString(CultureInfo.InvariantCulture),
                    Path = "/data/movies/Movie " + index.ToString(CultureInfo.InvariantCulture) + "/movie.mkv",
                    PathExists = true,
                    LibraryIds = [ConfiguredLibrary],
                    UserDataKeys = [.. keys, itemId.ToString("D", CultureInfo.InvariantCulture)],
                    ProviderIds = providers,
                };

                Place(item, keys, imdb is not null, Deterministic.Kind.Movie, index, providers, null);
            }
        }

        public void AddSeries()
        {
            var target = (int)Math.Round(shape.Titles * shape.EpisodeShare);
            var produced = 0;

            // Whole series only. Truncating the last one to hit an item target
            // biases the realized length distribution downwards — most visibly at
            // long means, where the truncated series is the largest — so the item
            // count overshoots instead, by at most one series.
            for (var series = 0; produced < target; series++)
            {
                var episodeCount = EpisodeCount(series);
                _seriesCount++;
                var imdb = Draw(Deterministic.Kind.Series, series, Deterministic.Slot.Imdb) < shape.ImdbCoverage
                    ? Imdb(Deterministic.Kind.Series, series)
                    : null;
                var tmdb = Draw(Deterministic.Kind.Series, series, Deterministic.Slot.Tmdb) < shape.TmdbCoverage
                    ? Tmdb(Deterministic.Kind.Series, series)
                    : null;

                var seriesId = Deterministic.Identity(shape.Seed, Deterministic.Kind.Series, series);
                var providers = Providers(imdb, tmdb);

                for (var episode = 0; episode < episodeCount; episode++, produced++)
                {
                    var season = 1 + (episode / EpisodesPerSeason);
                    var number = 1 + (episode % EpisodesPerSeason);
                    var suffix = season.ToString("000", CultureInfo.InvariantCulture)
                        + number.ToString("000", CultureInfo.InvariantCulture);

                    // The series' ID, inherited. This is the coupling that matters:
                    // one absent series IMDb takes every episode with it.
                    var keys = imdb is null ? new List<string>() : [imdb + suffix];
                    var itemId = Deterministic.Identity(shape.Seed, Deterministic.Kind.Episode, series, episode);

                    var item = new CurrentItemSnapshot
                    {
                        ItemId = itemId,
                        Kind = ItemKind.Episode,
                        Name = "S" + season.ToString(CultureInfo.InvariantCulture) + "E" + number.ToString(CultureInfo.InvariantCulture),
                        Path = "/data/tv/Show " + series.ToString(CultureInfo.InvariantCulture) + "/episode.mkv",
                        PathExists = true,
                        LibraryIds = [ConfiguredLibrary],
                        UserDataKeys = [.. keys, itemId.ToString("D", CultureInfo.InvariantCulture)],
                        ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        SeriesProviderIds = providers,
                        SeriesId = seriesId,
                        SeasonNumber = season,
                        EpisodeNumber = number,
                    };

                    _episodeCount++;
                    Place(item, keys, imdb is not null, Deterministic.Kind.Episode, series, providers, episode);
                }
            }
        }

        public Population Build() => new(
            _items,
            _detached,
            _current,
            _users.ToHashSet(),
            _opportunities,
            _opportunitiesWithImdb,
            _catalogItems,
            _catalogItemsWithImdb,
            _seriesCount,
            _episodeCount);

        private static Guid NthUser(int index)
        {
            var bytes = new byte[16];
            bytes[0] = (byte)(index + 1);
            bytes[15] = 0xAA;
            return new Guid(bytes);
        }

        private static Dictionary<string, string> Providers(string? imdb, string? tmdb)
        {
            var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (imdb is not null)
            {
                providers["Imdb"] = imdb;
            }

            if (tmdb is not null)
            {
                providers["Tmdb"] = tmdb;
            }

            return providers;
        }

        private double Draw(Deterministic.Kind kind, int index, Deterministic.Slot slot, int sub = 0) =>
            Deterministic.NextDouble(shape.Seed, kind, index, slot, sub);

        // Geometric on {1, 2, ...}, so lengths vary the way real catalogs do: many
        // short series, a few very long ones.
        //
        // Its mean is exactly the configured value. An earlier version rounded a
        // continuous exponential up, which is a different distribution: at a
        // configured mean of 1 it produced 1.57, and a 400-episode cap pulled the
        // long tail down so a configured 150 produced 133. A parameter that does
        // not mean what it says makes every curve plotted against it wrong.
        private int EpisodeCount(int series)
        {
            var mean = shape.MeanEpisodesPerSeries;
            if (mean <= 1)
            {
                return 1;
            }

            var p = 1 / mean;
            var u = Math.Clamp(Draw(Deterministic.Kind.Series, series, Deterministic.Slot.EpisodeCount), 1e-12, 1 - 1e-12);

            // Inverse CDF. The cap is a guard against pathological configuration,
            // not a shaping parameter: at a mean of 150 it excludes a tail of
            // weight e^-33.
            var length = (int)Math.Ceiling(Math.Log(1 - u) / Math.Log(1 - p));
            return Math.Clamp(length, 1, 5000);
        }

        private void Place(
            CurrentItemSnapshot item,
            IReadOnlyList<string> providerKeys,
            bool hasImdbKey,
            Deterministic.Kind kind,
            int index,
            IReadOnlyDictionary<string, string> providers,
            int? sub)
        {
            _items.Add(item);
            _catalogItems++;
            if (hasImdbKey)
            {
                _catalogItemsWithImdb++;
            }

            var episodeSub = sub ?? 0;

            if (Draw(kind, index, Deterministic.Slot.Duplicate, episodeSub) < shape.Duplication)
            {
                _items.Add(Twin(item, providers));
            }

            for (var user = 0; user < _users.Length; user++)
            {
                if (Draw(kind, index, Deterministic.Slot.Watched, (episodeSub * 64) + user) >= shape.WatchedFraction)
                {
                    continue;
                }

                _opportunities++;
                if (hasImdbKey)
                {
                    _opportunitiesWithImdb++;
                }

                foreach (var key in providerKeys)
                {
                    _detached.Add(StrandedRow(_users[user], key));
                }

                // Every removed item leaves its own GUID key behind, unmappable by
                // construction. Drawn from its own identity space and a counter no
                // live item shares: a collision with a live GUID would satisfy
                // §7.3 case 1 and be counted as a recovery.
                for (var move = 0; move < shape.MovesPerTitle; move++)
                {
                    var dead = Deterministic.Identity(shape.Seed, Deterministic.Kind.RemovedItem, _removedItems++);
                    _detached.Add(StrandedRow(_users[user], dead.ToString("D", CultureInfo.InvariantCulture)));
                }

                if (Draw(kind, index, Deterministic.Slot.CurrentState, (episodeSub * 64) + user) < shape.CurrentStateFraction)
                {
                    _current.Add(new CurrentUserDataRow
                    {
                        UserId = _users[user],
                        ItemId = item.ItemId,
                        CustomDataKey = providerKeys.Count > 0
                            ? providerKeys[0]
                            : item.ItemId.ToString("D", CultureInfo.InvariantCulture),
                        Played = false,
                        PlayCount = 1,
                        PlaybackPositionTicks = 999,
                        IsFavorite = false,
                        LastPlayedDate = LastPlayed.AddDays(1),
                        Rating = 4,
                    });
                }
            }
        }

        private CurrentItemSnapshot Twin(CurrentItemSnapshot item, IReadOnlyDictionary<string, string> providers)
        {
            var twinId = Deterministic.Identity(shape.Seed, Deterministic.Kind.Duplicate, _duplicates++);

            return item with
            {
                ItemId = twinId,
                LibraryIds = [UnconfiguredLibrary],
                Path = "/data/other/copy.mkv",
                UserDataKeys = [.. item.UserDataKeys
                    .Where(key => !key.Equals(item.ItemId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)),
                    twinId.ToString("D", CultureInfo.InvariantCulture)],
                ProviderIds = item.Kind == ItemKind.Movie ? providers : item.ProviderIds,
            };
        }

        private DetachedUserDataRow StrandedRow(Guid user, string key) => new()
        {
            UserId = user,
            CustomDataKey = key,
            Played = true,
            PlayCount = 3,
            PlaybackPositionTicks = 12345,
            IsFavorite = true,
            LastPlayedDate = LastPlayed,
            Rating = 9,
            RetentionDate = Retention,
        };
    }
}
