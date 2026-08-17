using Jellyfin.Data.Enums;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
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
/// <para>Collecting every item is not the same as touching every item's storage.
/// Keys are read from all of them; the media file is stat-ed only for items in a
/// selected library, because that is the only place the answer changes a verdict
/// (see <see cref="ProbeIfWorthIt"/>).</para>
/// <para>Keys come from each item's own <c>GetUserDataKeys()</c>. This class does
/// not know how Jellyfin builds them and must not learn.</para>
/// </remarks>
public sealed class LibraryItemCollector(ILibraryManager libraryManager, Func<string?, bool>? probePath = null)
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

    // Injected so a test can count the calls. What matters about this predicate
    // is not only what it answers but how often it is asked: it is a synchronous
    // stat, and the whole of DESIGN's cost story for a narrowly scoped run rests
    // on it never touching storage the operator left out of scope.
    private readonly Func<string?, bool> _probePath = probePath ?? PathExists;

    /// <summary>
    /// Collects the current catalog.
    /// </summary>
    /// <param name="configuredLibraryIds">The libraries an operator marked eligible.</param>
    /// <param name="checkPathExists">
    /// Whether the media file behind an in-scope item must be stat-ed. When false
    /// the filesystem is not touched at all; when true it is touched only for
    /// items in a selected library.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One snapshot per current movie and episode.</returns>
    public IReadOnlyList<CurrentItemSnapshot> Collect(
        IReadOnlyList<Guid> configuredLibraryIds,
        bool checkPathExists,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuredLibraryIds);

        var membership = BuildMembership(configuredLibraryIds, cancellationToken);
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

    /// <summary>
    /// Snapshots one item the caller already holds, reading every fact live.
    /// </summary>
    /// <param name="item">The item, freshly read from the library manager.</param>
    /// <param name="configuredLibraryIds">The libraries an operator marked eligible.</param>
    /// <param name="checkPathExists">Whether to stat the media file.</param>
    /// <returns>The snapshot.</returns>
    /// <remarks>
    /// <para>For revalidating a single recovery target immediately before writing
    /// to it, where re-collecting the whole catalogue would cost more than the
    /// run.</para>
    /// <para>Library membership is asked of the item itself rather than looked up
    /// in a map built earlier in the run. A map is a photograph, and checking a
    /// target against the same photograph that admitted it checks nothing; one
    /// indexed walk up this item's ancestors is both cheaper and actually
    /// current.</para>
    /// <para>The owning series' provider IDs are left empty: they only classify a
    /// key's identity evidence, which was settled during analysis, and nothing that
    /// reads this snapshot looks at them.</para>
    /// </remarks>
    public CurrentItemSnapshot Snapshot(
        BaseItem item,
        IReadOnlyList<Guid> configuredLibraryIds,
        bool checkPathExists)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(configuredLibraryIds);

        var configured = configuredLibraryIds.ToHashSet();
        var membership = new Dictionary<Guid, List<Guid>>
        {
            [item.Id] = [.. _libraryManager.GetCollectionFolders(item)
                .Select(folder => folder.Id)
                .Where(configured.Contains)
                .Distinct()],
        };

        return ToSnapshot(item, membership, new Dictionary<Guid, Dictionary<string, string>>(), checkPathExists);
    }

    /// <summary>
    /// Indexes which current items report which user-data keys.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ownership index.</returns>
    /// <remarks>
    /// The catalogue-wide half of revalidation, and deliberately the cheapest pass
    /// in this class: no library membership, no series provider IDs, and above all
    /// no filesystem — the stat is the slowest thing in a run, and whether a file
    /// is present has no bearing on which items claim a key. One query plus
    /// <c>GetUserDataKeys()</c> per item.
    /// </remarks>
    public KeyOwnership BuildKeyOwnership(CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            DtoOptions = ItemFieldsNeeded,
        });

        var snapshots = new List<CurrentItemSnapshot>(items.Count);
        var membership = new Dictionary<Guid, List<Guid>>();
        var seriesProviderIds = new Dictionary<Guid, Dictionary<string, string>>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(ToSnapshot(item, membership, seriesProviderIds, checkPathExists: false));
        }

        return KeyOwnership.Build(snapshots);
    }

    /// <summary>
    /// Finds the current items that could plausibly report the same keys as one
    /// target, right now.
    /// </summary>
    /// <param name="item">The target, freshly read from the library manager.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Snapshots of the contenders, never including the target itself.</returns>
    /// <remarks>
    /// <para>Uniqueness is the one condition behind a write that is a property of
    /// the whole catalogue: no amount of looking at the target reveals that a
    /// second item has started answering to its key.
    /// <see cref="BuildKeyOwnership"/> settles that for a whole run in one pass,
    /// which is affordable once and not once per write — so this exists to ask
    /// the same question about one item, immediately before writing to it, at the
    /// cost of an indexed lookup instead of a full catalogue scan.</para>
    /// <para><b>The provider query narrows; it does not judge.</b> That
    /// separation is the whole design. Jellyfin builds user-data keys from
    /// provider IDs, but this class refuses to know how, so the query is used
    /// only to produce a short list of items that might collide — and every one
    /// of them is then asked for its own <c>GetUserDataKeys()</c>, which is what
    /// actually decides. A query that returns too much costs a few key
    /// comparisons and changes no verdict. A query that returns too little leaves
    /// exactly the gap the run-level index already covers, so a write must still
    /// satisfy both and this can only ever make a run more conservative, never
    /// less.</para>
    /// <para>An episode is asked about three ways over, because an episode's own
    /// provider IDs are usually empty and its user-data identity is built from
    /// its series plus its season and episode numbers. Its own siblings are
    /// contenders, since a refresh that renumbers one of them onto this episode's
    /// slot gives the two the same derived key without either item's provider IDs
    /// changing at all — nothing about this episode reveals that, and no
    /// provider-ID query finds it, because there are no provider IDs to query on.
    /// A second series carrying the same IMDb or TMDb ID then makes every one of
    /// <i>its</i> episodes a contender too.</para>
    /// </remarks>
    public IReadOnlyList<CurrentItemSnapshot> FindKeyContenders(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var contenders = new Dictionary<Guid, BaseItem>();

        foreach (var candidate in ItemsSharingProviderIds(item.ProviderIds, [item.Id]))
        {
            contenders[candidate.Id] = candidate;
        }

        if (item is Episode { SeriesId: var seriesId } && !seriesId.Equals(Guid.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // This episode's own siblings, which is the set the run-wide index is
            // least able to keep current: it is built once, and a renumbering
            // afterwards needs no library scan, so none of the guards that abandon
            // the batch fire.
            AddEpisodes(contenders, seriesId, item.Id);

            if (_libraryManager.GetItemById(seriesId) is { } series)
            {
                foreach (var rival in ItemsSharingProviderIds(series.ProviderIds, [seriesId]))
                {
                    AddEpisodes(contenders, rival.Id, item.Id);
                }
            }
        }

        var membership = new Dictionary<Guid, List<Guid>>();
        var seriesProviderIds = new Dictionary<Guid, Dictionary<string, string>>();

        return [.. contenders.Values.Select(candidate =>
            ToSnapshot(candidate, membership, seriesProviderIds, checkPathExists: false))];
    }

    private IReadOnlyList<BaseItem> ItemsSharingProviderIds(
        IReadOnlyDictionary<string, string>? providerIds,
        IReadOnlyList<Guid> exclude)
    {
        // Nothing to narrow by. The run-level index remains the only answer for
        // this item, which is what it was before this check existed.
        if (providerIds is null || providerIds.Count == 0)
        {
            return [];
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series],
            Recursive = true,
            HasAnyProviderId = providerIds.ToDictionary(StringComparer.OrdinalIgnoreCase),
            ExcludeItemIds = [.. exclude],
            DtoOptions = ItemFieldsNeeded,
        });
    }

    private void AddEpisodes(Dictionary<Guid, BaseItem> contenders, Guid seriesId, Guid exclude)
    {
        foreach (var candidate in EpisodesOf(seriesId))
        {
            if (!candidate.Id.Equals(exclude))
            {
                contenders[candidate.Id] = candidate;
            }
        }
    }

    private IReadOnlyList<BaseItem> EpisodesOf(Guid seriesId) =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            AncestorIds = [seriesId],
            DtoOptions = ItemFieldsNeeded,
        });

    private Dictionary<Guid, List<Guid>> BuildMembership(
        IReadOnlyList<Guid> configuredLibraryIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuredLibraryIds);

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

        // Only ever holds libraries the operator selected — BuildMembership walks
        // the selection, not the server — so an empty list is exactly "this item
        // is out of scope".
        IReadOnlyList<Guid> libraries = membership.TryGetValue(item.Id, out var owning) ? owning : [];

        return new CurrentItemSnapshot
        {
            ItemId = item.Id,
            Kind = ClassifyKind(item),
            Name = item.Name,
            Path = item.Path,
            PathExists = ProbeIfWorthIt(item, libraries, checkPathExists),
            IsVirtualItem = item.IsVirtualItem,
            IsExtraOrTrailer = item.ExtraType.HasValue,
            LibraryIds = libraries,
            UserDataKeys = item.GetUserDataKeys(),
            ProviderIds = item.ProviderIds ?? [],
            SeriesProviderIds = seriesProviders,
            SeriesId = seriesId,
            SeasonNumber = episode?.ParentIndexNumber,
            EpisodeNumber = episode?.IndexNumber,
        };
    }

    /// <summary>
    /// Stats the media file, but only when some verdict depends on the answer.
    /// </summary>
    /// <remarks>
    /// <para>Two ways out without touching the filesystem, and both report the
    /// item as present because that is the value <see cref="ItemEligibility"/>
    /// will not consult.</para>
    /// <para>The check being off is the simple one: "do not require the path to
    /// exist" means exactly that, and paying for an answer nothing reads is pure
    /// waste — this runs once per movie and episode on the server, and on a
    /// network mount the stat is the slowest thing in the pass.</para>
    /// <para>The item being outside the selection is the one that matters. Every
    /// movie and episode is collected, in scope or not, because a key claimed by
    /// an unselected item still makes that key ambiguous and the run still has to
    /// see it. Its <i>file</i> decides nothing: membership is checked first, so an
    /// unselected item is excluded whatever the stat would have said. Asking anyway
    /// dragged a whole run through storage the operator had deliberately left out
    /// of scope — a slow or unavailable NFS mount could stall a task scoped to one
    /// small local library for minutes, and every missing file out there counted
    /// towards the missing-mount warning about the libraries that were ticked.</para>
    /// </remarks>
    private bool ProbeIfWorthIt(BaseItem item, IReadOnlyList<Guid> libraries, bool checkPathExists) =>
        !checkPathExists || libraries.Count == 0 || (item.IsFileProtocol && _probePath(item.Path));

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
