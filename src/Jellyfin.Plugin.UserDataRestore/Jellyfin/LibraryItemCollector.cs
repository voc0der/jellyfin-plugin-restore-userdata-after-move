using Jellyfin.Data.Enums;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>
/// Snapshots the current catalog and its real user-data keys (DESIGN §7.2).
/// </summary>
/// <remarks>
/// <para>Every current movie and episode is collected, not only those in
/// configured libraries. Eligibility is decided afterwards, in the core, for two
/// reasons: a key that matches only an excluded item can then be reported as
/// <c>unsupported_current_item</c> or <c>path_outside_final_scope</c> instead of
/// a bare <c>no_current_key_match</c>, and a key exposed by both an eligible item
/// and an out-of-scope one is correctly ambiguous rather than confidently
/// wrong.</para>
/// <para>Keys come from each item's own <c>GetUserDataKeys()</c>. This class does
/// not know how Jellyfin builds them and must not learn.</para>
/// </remarks>
public sealed class LibraryItemCollector(ILibraryManager libraryManager)
{
    /// <summary>
    /// Provider IDs must be hydrated, or the whole plugin quietly does nothing.
    /// </summary>
    /// <remarks>
    /// <c>Movie.GetUserDataKeys()</c> prepends the item's IMDb and TMDb IDs to the
    /// keys it inherits — but only from the provider IDs actually loaded onto the
    /// instance. Query with fields off and every item reports exactly one key, its
    /// own GUID, so every stranded provider-keyed row lands in
    /// <c>no_current_key_match</c> and the run reports "nothing recoverable"
    /// without an error anywhere. Observed on a live 10.11.11 server.
    /// </remarks>
    private static DtoOptions ItemFieldsNeeded => new(false)
    {
        Fields = [ItemFields.ProviderIds],
        EnableImages = false,
    };

    private readonly ILibraryManager _libraryManager = libraryManager;

    /// <summary>
    /// Collects the current catalog.
    /// </summary>
    /// <param name="configuredLibraryIds">The libraries an operator marked eligible.</param>
    /// <param name="checkPathExists">
    /// Whether the media file behind each item must be stat-ed. When false the
    /// filesystem is not touched at all: this runs once per movie and episode on
    /// the server, and on a network mount it is the slowest thing in the pass, so
    /// paying for an answer nothing will read is pure waste.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One snapshot per current movie and episode.</returns>
    public IReadOnlyList<CurrentItemSnapshot> Collect(
        IReadOnlyList<Guid> configuredLibraryIds,
        bool checkPathExists,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuredLibraryIds);

        var membership = BuildLibraryMembership(configuredLibraryIds, cancellationToken);
        var seriesProviderIds = BuildSeriesProviderIds(cancellationToken);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            DtoOptions = ItemFieldsNeeded,
        });

        var snapshots = new List<CurrentItemSnapshot>(items.Count);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(ToSnapshot(item, membership, seriesProviderIds, checkPathExists));
        }

        return snapshots;
    }

    private static ItemKind ClassifyKind(BaseItem item) => item switch
    {
        Episode => ItemKind.Episode,
        MediaBrowser.Controller.Entities.Movies.Movie => ItemKind.Movie,
        _ => ItemKind.Other,
    };

    private static bool PathExists(string? path) =>
        !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    private CurrentItemSnapshot ToSnapshot(
        BaseItem item,
        IReadOnlyDictionary<Guid, List<Guid>> membership,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> seriesProviderIds,
        bool checkPathExists)
    {
        var episode = item as Episode;
        var seriesId = episode?.SeriesId;

        Dictionary<string, string> seriesProviders = [];
        if (seriesId is { } id && seriesProviderIds.TryGetValue(id, out var found))
        {
            seriesProviders = found;
        }

        return new CurrentItemSnapshot
        {
            ItemId = item.Id,
            Kind = ClassifyKind(item),
            Name = item.Name,
            Path = item.Path,
            // Not stat-ed at all when the check is off: an item is then treated as
            // present, which is what "do not require the path to exist" means, and
            // ItemEligibility will not look at this field either way.
            PathExists = !checkPathExists || (item.IsFileProtocol && PathExists(item.Path)),
            IsVirtualItem = item.IsVirtualItem,
            IsExtraOrTrailer = item.ExtraType.HasValue,
            LibraryIds = membership.TryGetValue(item.Id, out var libraries) ? libraries : [],
            UserDataKeys = item.GetUserDataKeys(),
            ProviderIds = item.ProviderIds ?? [],
            SeriesProviderIds = seriesProviders,
            SeriesId = seriesId,
            SeasonNumber = episode?.ParentIndexNumber,
            EpisodeNumber = episode?.IndexNumber,
        };
    }

    private Dictionary<Guid, List<Guid>> BuildLibraryMembership(
        IReadOnlyList<Guid> configuredLibraryIds,
        CancellationToken cancellationToken)
    {
        var membership = new Dictionary<Guid, List<Guid>>();

        foreach (var libraryId in configuredLibraryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                AncestorIds = [libraryId],
                DtoOptions = ItemFieldsNeeded,
            });

            foreach (var id in ids)
            {
                if (!membership.TryGetValue(id, out var libraries))
                {
                    libraries = [];
                    membership[id] = libraries;
                }

                libraries.Add(libraryId);
            }
        }

        return membership;
    }

    private Dictionary<Guid, Dictionary<string, string>> BuildSeriesProviderIds(CancellationToken cancellationToken)
    {
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true,
            DtoOptions = ItemFieldsNeeded,
        });

        var map = new Dictionary<Guid, Dictionary<string, string>>();
        foreach (var item in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            map[item.Id] = item.ProviderIds is null
                ? []
                : new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }
}
