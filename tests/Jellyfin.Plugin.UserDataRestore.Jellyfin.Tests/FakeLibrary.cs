using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.MediaInfo;
using NSubstitute;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

/// <summary>
/// A server's catalogue, small enough to hold in a test and real enough to answer
/// the questions the adapter actually asks.
/// </summary>
/// <remarks>
/// <para>The items are Jellyfin's own <see cref="Movie"/>, <see cref="Episode"/>
/// and <see cref="Series"/>, not stand-ins, so the keys under test come out of
/// Jellyfin's real <c>GetUserDataKeys()</c>. That is the whole point: the core
/// tests already prove what the analyzer does with a given key set, and what they
/// cannot prove is that a live server hands this plugin the key set they assume.
/// An episode resolves its series through the static
/// <c>BaseItem.LibraryManager</c>, which is why this installs itself there.</para>
/// <para><see cref="ILibraryManager"/> is answered by a substitute rather than a
/// hand-written stub — it has well over a hundred members and this needs six of
/// them. The query handling below is deliberately partial: it honours exactly the
/// filters this plugin passes, and a query using anything else would silently be
/// over-answered, which is the direction that cannot turn a skipped write into a
/// performed one.</para>
/// </remarks>
internal sealed class FakeLibrary
{
    private readonly List<BaseItem> _items = [];
    private readonly Dictionary<Guid, List<Guid>> _ancestors = [];
    private readonly Dictionary<Guid, List<Folder>> _collectionFolders = [];

    private FakeLibrary()
    {
        Manager = Substitute.For<ILibraryManager>();

        Manager.GetItemById(Arg.Any<Guid>())
            .Returns(call => _items.Find(item => item.Id.Equals(call.Arg<Guid>())));

        Manager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(call => Query(call.Arg<InternalItemsQuery>()));

        Manager.GetItemIds(Arg.Any<InternalItemsQuery>())
            .Returns(call => [.. Query(call.Arg<InternalItemsQuery>()).Select(item => item.Id)]);

        Manager.GetCollectionFolders(Arg.Any<BaseItem>())
            .Returns(call => _collectionFolders.TryGetValue(call.Arg<BaseItem>().Id, out var folders) ? folders : []);

        // Jellyfin's entities reach for a handful of statics on the way to
        // answering the two questions this plugin asks them. Each is filled with
        // the answer a plain library item on disk would produce on a real server;
        // none of them is a fact under test, and leaving any of them null makes
        // the entity throw rather than misreport, so a missing one cannot pass
        // quietly.
        //
        // LibraryManager is the load-bearing one: Episode.Series resolves through
        // it, and without it every episode here reports only its own GUID — a key
        // set no assertion about series-derived keys could tell apart from a
        // genuine bug.
        BaseItem.LibraryManager = Manager;

        var mediaSources = Substitute.For<IMediaSourceManager>();
        mediaSources.GetPathProtocol(Arg.Any<string>()).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSources;

        // Video.SourceType asks whether the file is a recording in progress, and a
        // recording's user-data key is its external ID rather than anything this
        // plugin matches on.
        Video.RecordingsManager = Substitute.For<IRecordingsManager>();
    }

    public ILibraryManager Manager { get; }

    public static FakeLibrary Create() => new();

    /// <summary>Adds a series, which contributes its provider IDs to its episodes' keys.</summary>
    public Series AddSeries(string name, Guid libraryId, Dictionary<string, string>? providerIds = null)
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = "/data/library/tv/" + name,
            ProviderIds = providerIds ?? [],
        };

        return Add(series, libraryId);
    }

    public Episode AddEpisode(Series series, Guid libraryId, int season, int episode, Dictionary<string, string>? providerIds = null)
    {
        var item = new Episode
        {
            Id = Guid.NewGuid(),
            Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}"),
            Path = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"/data/library/tv/{series.Name}/Season {season:00}/S{season:00}E{episode:00}.mkv"),
            SeriesId = series.Id,
            ParentIndexNumber = season,
            IndexNumber = episode,
            ProviderIds = providerIds ?? [],
        };

        Add(item, libraryId);
        _ancestors[item.Id] = [libraryId, series.Id];
        return item;
    }

    public Movie AddMovie(string name, Guid libraryId, Dictionary<string, string>? providerIds = null, string? path = null)
    {
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = path ?? "/data/library/movies/" + name + "/" + name + ".mkv",
            ProviderIds = providerIds ?? [],
        };

        return Add(item, libraryId);
    }

    /// <summary>Renumbers an existing episode, the way a metadata refresh does.</summary>
    public static void Renumber(Episode episode, int season, int number)
    {
        episode.ParentIndexNumber = season;
        episode.IndexNumber = number;
    }

    private T Add<T>(T item, Guid libraryId)
        where T : BaseItem
    {
        _items.Add(item);
        _ancestors[item.Id] = [libraryId];

        var folder = new Folder { Id = libraryId };
        _collectionFolders[item.Id] = [folder];
        return item;
    }

    private IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
    {
        IEnumerable<BaseItem> results = _items;

        if (query.IncludeItemTypes.Length > 0)
        {
            results = results.Where(item => query.IncludeItemTypes.Contains(KindOf(item)));
        }

        if (query.AncestorIds.Length > 0)
        {
            results = results.Where(item =>
                _ancestors.TryGetValue(item.Id, out var ancestors) && ancestors.Intersect(query.AncestorIds).Any());
        }

        if (query.ExcludeItemIds.Length > 0)
        {
            results = results.Where(item => !query.ExcludeItemIds.Contains(item.Id));
        }

        // Any, not all: the name says so, and the plugin relies on it — an item
        // sharing one of a series' several provider IDs is a contender.
        if (query.HasAnyProviderId is { Count: > 0 } wanted)
        {
            results = results.Where(item => wanted.Any(pair =>
                item.ProviderIds.TryGetValue(pair.Key, out var value)
                && string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase)));
        }

        return [.. results];
    }

    private static BaseItemKind KindOf(BaseItem item) => item switch
    {
        Episode => BaseItemKind.Episode,
        Series => BaseItemKind.Series,
        Movie => BaseItemKind.Movie,
        _ => BaseItemKind.Folder,
    };
}
