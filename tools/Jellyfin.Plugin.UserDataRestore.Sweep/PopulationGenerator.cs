using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>One generated installation, ready to hand to the analyzer.</summary>
/// <param name="Items">Every current movie and episode on the server.</param>
/// <param name="DetachedRows">The sentinel rows a path change left behind.</param>
/// <param name="CurrentRows">Live user-data rows on the current items.</param>
/// <param name="UserIds">The surviving users.</param>
/// <param name="Opportunities">
/// The number of (user, title) pairs that had stranded state — the denominator
/// the recovery rate is measured against.
/// </param>
public sealed record Population(
    IReadOnlyList<CurrentItemSnapshot> Items,
    IReadOnlyList<DetachedUserDataRow> DetachedRows,
    IReadOnlyList<CurrentUserDataRow> CurrentRows,
    IReadOnlySet<Guid> UserIds,
    int Opportunities);

/// <summary>
/// Builds a synthetic installation from a <see cref="LibraryShape"/>.
/// </summary>
/// <remarks>
/// The keys are the ones Jellyfin itself produces, in the order it produces them:
/// a movie reports IMDb, then a bare TMDb number, then its own GUID; an episode
/// reports the series' provider ID with zero-padded season and episode appended,
/// then its own GUID. Nothing here invents a key shape the live runs did not show
/// (DESIGN §17.4).
/// </remarks>
public static class PopulationGenerator
{
    /// <summary>The library the analyzer is configured to write into.</summary>
    public static readonly Guid ConfiguredLibrary = new("11111111-1111-1111-1111-111111111111");

    /// <summary>A library the operator did not configure, where duplicates live.</summary>
    public static readonly Guid UnconfiguredLibrary = new("22222222-2222-2222-2222-222222222222");

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

        var random = new Random(shape.Seed);
        var items = new List<CurrentItemSnapshot>();
        var detached = new List<DetachedUserDataRow>();
        var current = new List<CurrentUserDataRow>();
        var users = Enumerable.Range(0, shape.Users).Select(NthUser).ToArray();
        var opportunities = 0;

        for (var t = 0; t < shape.Titles; t++)
        {
            var isEpisode = random.NextDouble() < shape.EpisodeShare;
            var imdb = random.NextDouble() < shape.ImdbCoverage ? Imdb(t) : null;
            var tmdb = random.NextDouble() < shape.TmdbCoverage ? Tmdb(t) : null;
            var itemId = NextGuid(random);

            var item = isEpisode
                ? Episode(itemId, NextGuid(random), imdb, tmdb, t)
                : Movie(itemId, imdb, tmdb, t);

            items.Add(item);

            // A second current item reporting the same provider keys: another copy
            // in an unconfigured library, or the old one still sitting at a vacated
            // path mid-migration. Either way the key stops being unique.
            if (random.NextDouble() < shape.Duplication)
            {
                var twinId = NextGuid(random);
                items.Add(isEpisode
                    ? Episode(twinId, NextGuid(random), imdb, tmdb, t, UnconfiguredLibrary)
                    : Movie(twinId, imdb, tmdb, t, UnconfiguredLibrary));
            }

            // The provider keys survive a move unchanged, because the provider IDs
            // do. These are the only keys that can ever match.
            var providerKeys = ProviderKeys(item, isEpisode);

            foreach (var user in users)
            {
                if (random.NextDouble() >= shape.WatchedFraction)
                {
                    continue;
                }

                opportunities++;

                // One row per provider key, all holding the same snapshot: what
                // §17.5 observed, and what lets two bare keys corroborate.
                foreach (var key in providerKeys)
                {
                    detached.Add(StrandedRow(user, key));
                }

                // Every removed item leaves its own GUID key behind. No current item
                // reports it, so these are unmappable by construction — they are in
                // the population because they are in real databases.
                for (var move = 0; move < shape.MovesPerTitle; move++)
                {
                    detached.Add(StrandedRow(user, NextGuid(random).ToString("D", CultureInfo.InvariantCulture)));
                }

                if (random.NextDouble() < shape.CurrentStateFraction)
                {
                    current.Add(new CurrentUserDataRow
                    {
                        UserId = user,
                        ItemId = item.ItemId,
                        CustomDataKey = providerKeys.Count > 0 ? providerKeys[0] : item.ItemId.ToString("D", CultureInfo.InvariantCulture),
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

        return new Population(items, detached, current, users.ToHashSet(), opportunities);
    }

    /// <summary>
    /// The scope the analyzer runs under. Both roots are configured, so nothing is
    /// excluded for being out of scope; the sweep is about matching, not scoping.
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

    private static IReadOnlyList<string> ProviderKeys(CurrentItemSnapshot item, bool isEpisode)
    {
        var suffix = isEpisode
            ? (item.SeasonNumber ?? 0).ToString("000", CultureInfo.InvariantCulture)
                + (item.EpisodeNumber ?? 0).ToString("000", CultureInfo.InvariantCulture)
            : string.Empty;

        var providers = isEpisode ? item.SeriesProviderIds : item.ProviderIds;
        var keys = new List<string>();

        if (providers.TryGetValue("Imdb", out var imdb))
        {
            keys.Add(imdb + suffix);
        }

        // Movies only. In evidence/alpha the episode's series carried both an IMDb
        // and a TMDb ID, and Jellyfin still emitted exactly one provider key — the
        // IMDb composite. An episode therefore has no second key to corroborate
        // with, which is why DESIGN §7.3 case 3 can never rescue a series that
        // lacks an IMDb ID.
        if (!isEpisode && providers.TryGetValue("Tmdb", out var tmdb))
        {
            keys.Add(tmdb);
        }

        return keys;
    }

    private static CurrentItemSnapshot Movie(Guid id, string? imdb, string? tmdb, int index, Guid? library = null)
    {
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();

        if (imdb is not null)
        {
            providers["Imdb"] = imdb;
            keys.Add(imdb);
        }

        if (tmdb is not null)
        {
            providers["Tmdb"] = tmdb;
            keys.Add(tmdb);
        }

        keys.Add(id.ToString("D", CultureInfo.InvariantCulture));

        return new CurrentItemSnapshot
        {
            ItemId = id,
            Kind = ItemKind.Movie,
            Name = "Movie " + index.ToString(CultureInfo.InvariantCulture),
            Path = "/data/movies/Movie " + index.ToString(CultureInfo.InvariantCulture) + "/movie.mkv",
            PathExists = true,
            LibraryIds = [library ?? ConfiguredLibrary],
            UserDataKeys = keys,
            ProviderIds = providers,
        };
    }

    private static CurrentItemSnapshot Episode(Guid id, Guid seriesId, string? imdb, string? tmdb, int index, Guid? library = null)
    {
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var season = 1 + (index % 5);
        var episode = 1 + (index % 13);
        var suffix = season.ToString("000", CultureInfo.InvariantCulture) + episode.ToString("000", CultureInfo.InvariantCulture);
        var keys = new List<string>();

        if (imdb is not null)
        {
            providers["Imdb"] = imdb;
            keys.Add(imdb + suffix);
        }

        // Recorded on the series, but it produces no key: see ProviderKeys.
        if (tmdb is not null)
        {
            providers["Tmdb"] = tmdb;
        }

        keys.Add(id.ToString("D", CultureInfo.InvariantCulture));

        return new CurrentItemSnapshot
        {
            ItemId = id,
            Kind = ItemKind.Episode,
            Name = "Episode " + index.ToString(CultureInfo.InvariantCulture),
            Path = "/data/tv/Show " + index.ToString(CultureInfo.InvariantCulture) + "/S01E01.mkv",
            PathExists = true,
            LibraryIds = [library ?? ConfiguredLibrary],
            UserDataKeys = keys,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SeriesProviderIds = providers,
            SeriesId = seriesId,
            SeasonNumber = season,
            EpisodeNumber = episode,
        };
    }

    private static DetachedUserDataRow StrandedRow(Guid user, string key) => new()
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

    private static string Imdb(int index) => "tt" + index.ToString("0000000", CultureInfo.InvariantCulture);

    // Bare, with no provider namespace, exactly as Jellyfin stores it. Offset well
    // clear of the IMDb digits so a TMDb key can never collide with one.
    private static string Tmdb(int index) => (500000 + index).ToString(CultureInfo.InvariantCulture);

    private static Guid NthUser(int index)
    {
        var bytes = new byte[16];
        bytes[0] = (byte)(index + 1);
        bytes[15] = 0xAA;
        return new Guid(bytes);
    }

    private static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
