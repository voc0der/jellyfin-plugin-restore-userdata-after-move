using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Tests;

/// <summary>
/// Builders for analysis scenarios.
/// </summary>
/// <remarks>
/// The fixture data mirrors what the disposable servers actually produced
/// (DESIGN §17.4): a movie carries a bare TMDb number, its own GUID, and an IMDb
/// ID; an episode carries its own GUID and the series IMDb ID with zero-padded
/// season and episode numbers.
/// </remarks>
internal static class Scenario
{
    public const string DefaultMoviePath = "/data/library/movies/Test Movie (2020)/Test Movie (2020).mkv";
    public const string DefaultEpisodePath = "/data/library/tv/Test Show/Season 01/S01E01.mkv";

    public static readonly Guid LibraryId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherLibraryId = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid UserA = new("a7fb7734-0000-0000-0000-000000000001");
    public static readonly Guid UserB = new("18fe613b-0000-0000-0000-000000000002");

    public static AnalysisOptions Options(bool requirePathExists = false) => new()
    {
        EligibleLibraryIds = [LibraryId],
        FinalPathPrefixes = ["/data/library/movies", "/data/library/tv"],
        PathComparison = StringComparison.Ordinal,
        RequirePathExists = requirePathExists,
        NowUtc = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc),
    };

    public static CurrentItemSnapshot Movie(
        Guid id,
        string? tmdb = "603",
        string? imdb = "tt0133093",
        string path = DefaultMoviePath,
        Guid? libraryId = null,
        bool includeGuidKey = true,
        string? name = "Test Movie")
    {
        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (imdb is not null)
        {
            providerIds["Imdb"] = imdb;
        }

        if (tmdb is not null)
        {
            providerIds["Tmdb"] = tmdb;
        }

        // Jellyfin's Video.GetUserDataKeys() puts IMDb first, then TMDb, then the
        // keys BaseItem contributed — the item GUID.
        var keys = new List<string>();
        if (imdb is not null)
        {
            keys.Add(imdb);
        }

        if (tmdb is not null)
        {
            keys.Add(tmdb);
        }

        if (includeGuidKey)
        {
            keys.Add(id.ToString("D"));
        }

        return new CurrentItemSnapshot
        {
            ItemId = id,
            Kind = ItemKind.Movie,
            Name = name,
            Path = path,
            PathExists = true,
            LibraryIds = [libraryId ?? LibraryId],
            UserDataKeys = keys,
            ProviderIds = providerIds,
        };
    }

    public static CurrentItemSnapshot Episode(
        Guid id,
        Guid seriesId,
        string? seriesImdb = "tt0903747",
        string? seriesTmdb = null,
        int season = 1,
        int episode = 1,
        string path = DefaultEpisodePath,
        Guid? libraryId = null,
        bool includeSeriesGuidKey = false)
    {
        var seriesProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (seriesImdb is not null)
        {
            seriesProviders["Imdb"] = seriesImdb;
        }

        if (seriesTmdb is not null)
        {
            seriesProviders["Tmdb"] = seriesTmdb;
        }

        var suffix = season.ToString("000") + episode.ToString("000");
        var keys = new List<string>();
        if (seriesImdb is not null)
        {
            keys.Add(seriesImdb + suffix);
        }

        if (seriesTmdb is not null)
        {
            keys.Add(seriesTmdb + suffix);
        }

        if (includeSeriesGuidKey)
        {
            keys.Add(seriesId.ToString("D") + suffix);
        }

        keys.Add(id.ToString("D"));

        return new CurrentItemSnapshot
        {
            ItemId = id,
            Kind = ItemKind.Episode,
            Name = "S01E01",
            Path = path,
            PathExists = true,
            LibraryIds = [libraryId ?? LibraryId],
            UserDataKeys = keys,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SeriesProviderIds = seriesProviders,
            SeriesId = seriesId,
            SeasonNumber = season,
            EpisodeNumber = episode,
        };
    }

    /// <summary>
    /// The key-ownership index the apply pass revalidates against, over exactly
    /// the items given.
    /// </summary>
    public static KeyOwnership Ownership(params CurrentItemSnapshot[] items) => KeyOwnership.Build(items);

    public static DetachedUserDataRow Row(
        Guid userId,
        string key,
        bool played = true,
        int playCount = 3,
        long ticks = 12345,
        bool favorite = true,
        double? rating = 9,
        DateTime? lastPlayed = null,
        DateTime? retention = null) => new()
        {
            UserId = userId,
            CustomDataKey = key,
            Played = played,
            PlayCount = playCount,
            PlaybackPositionTicks = ticks,
            IsFavorite = favorite,
            LastPlayedDate = lastPlayed ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Rating = rating,
            RetentionDate = retention ?? new DateTime(2026, 8, 12, 14, 22, 9, DateTimeKind.Utc),
        };

    /// <summary>
    /// A detached row carrying an exact state, including "nothing at all", which
    /// the optional-argument form cannot express.
    /// </summary>
    public static DetachedUserDataRow RowWith(
        Guid userId,
        string key,
        RecoveryState state,
        DateTime? retention = null) => new()
        {
            UserId = userId,
            CustomDataKey = key,
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = state.LastPlayedDate,
            Rating = state.Rating,
            RetentionDate = retention ?? new DateTime(2026, 8, 12, 14, 22, 9, DateTimeKind.Utc),
        };

    /// <summary>A live row carrying an exact state.</summary>
    public static CurrentUserDataRow CurrentRowWith(
        Guid userId,
        Guid itemId,
        RecoveryState state,
        string key = "603") => new()
        {
            UserId = userId,
            ItemId = itemId,
            CustomDataKey = key,
            Played = state.Played,
            PlayCount = state.PlayCount,
            PlaybackPositionTicks = state.PlaybackPositionTicks,
            IsFavorite = state.IsFavorite,
            LastPlayedDate = state.LastPlayedDate,
            Rating = state.Rating,
        };

    public static CurrentUserDataRow CurrentRow(
        Guid userId,
        Guid itemId,
        string key = "603",
        bool played = true,
        int playCount = 3,
        long ticks = 12345,
        bool favorite = true,
        double? rating = 9,
        DateTime? lastPlayed = null) => new()
        {
            UserId = userId,
            ItemId = itemId,
            CustomDataKey = key,
            Played = played,
            PlayCount = playCount,
            PlaybackPositionTicks = ticks,
            IsFavorite = favorite,
            LastPlayedDate = lastPlayed ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Rating = rating,
        };

    public static AnalysisResult Analyze(
        IReadOnlyList<DetachedUserDataRow> rows,
        IReadOnlyList<CurrentItemSnapshot> items,
        IReadOnlyList<CurrentUserDataRow>? currentRows = null,
        AnalysisOptions? options = null,
        IReadOnlySet<Guid>? users = null)
    {
        var candidates = DetachedUserDataAnalyzer.BuildCandidates(new AnalysisInput
        {
            DetachedRows = rows,
            CurrentItems = items,
            KnownUserIds = users ?? new HashSet<Guid> { UserA, UserB },
            Options = options ?? Options(),
        });

        return DetachedUserDataAnalyzer.Complete(candidates, currentRows ?? []);
    }
}
