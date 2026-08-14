namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// What kind of thing a current item is, as far as this plugin cares.
/// </summary>
public enum ItemKind
{
    /// <summary>Anything that is not a supported recovery target.</summary>
    Other = 0,

    /// <summary>A concrete movie.</summary>
    Movie = 1,

    /// <summary>A concrete episode.</summary>
    Episode = 2,
}

/// <summary>
/// Wire names for <see cref="ItemKind"/>, spelled out for the same reason every
/// other wire mapping here is: a rename in C# must not change the plan schema,
/// and because array contents feed the canonical plan ID, it would change the
/// hash of a plan whose meaning had not moved at all.
/// </summary>
public static class ItemKinds
{
    private static readonly Dictionary<ItemKind, string> Names = new()
    {
        [ItemKind.Other] = "other",
        [ItemKind.Movie] = "movie",
        [ItemKind.Episode] = "episode",
    };

    /// <summary>Gets every kind.</summary>
    public static IReadOnlyList<ItemKind> All { get; } = [.. Names.Keys];

    /// <summary>
    /// Maps a kind to its stable wire name.
    /// </summary>
    /// <param name="kind">The kind to map.</param>
    /// <returns>The snake_case name used in plans and logs.</returns>
    public static string ToWire(ItemKind kind) =>
        Names.TryGetValue(kind, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(kind));
}

/// <summary>
/// A current Jellyfin item, as seen by the analyzer.
/// </summary>
/// <remarks>
/// <para><see cref="UserDataKeys"/> holds the values Jellyfin's own
/// <c>GetUserDataKeys()</c> returned. The analyzer joins on those and nothing
/// else; it never manufactures a key from provider metadata (DESIGN §7.2).</para>
/// <para><see cref="ProviderIds"/> and <see cref="SeriesProviderIds"/> exist only
/// to <em>classify</em> a key's identity evidence, which can restrict what is
/// applied but can never widen it.</para>
/// </remarks>
public sealed record CurrentItemSnapshot
{
    /// <summary>Gets the current Jellyfin item ID.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the item kind.</summary>
    public required ItemKind Kind { get; init; }

    /// <summary>Gets the display name, for the report.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the media path.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets a value indicating whether the media path resolves to something that
    /// exists on the host filesystem.
    /// </summary>
    public bool PathExists { get; init; }

    /// <summary>Gets a value indicating whether Jellyfin considers this a virtual (missing) item.</summary>
    public bool IsVirtualItem { get; init; }

    /// <summary>Gets a value indicating whether this is an extra, trailer, or other non-primary entry.</summary>
    public bool IsExtraOrTrailer { get; init; }

    /// <summary>Gets the collection folders (libraries) this item belongs to.</summary>
    public IReadOnlyList<Guid> LibraryIds { get; init; } = [];

    /// <summary>Gets the item's real user-data keys, in the order Jellyfin returned them.</summary>
    public required IReadOnlyList<string> UserDataKeys { get; init; }

    /// <summary>Gets the item's provider IDs.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the owning series' provider IDs, for episodes.</summary>
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the owning series' item ID, for episodes.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the season number, for episodes.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number, for episodes.</summary>
    public int? EpisodeNumber { get; init; }
}
