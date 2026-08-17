using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Why a current item cannot be a recovery target.
/// </summary>
public enum ItemExclusion
{
    /// <summary>The item is an eligible target.</summary>
    None = 0,

    /// <summary>Not a concrete movie or episode.</summary>
    UnsupportedType,

    /// <summary>A virtual item, extra, or trailer.</summary>
    VirtualOrExtra,

    /// <summary>No media path, or the path does not exist.</summary>
    MissingPath,

    /// <summary>Not in a configured library.</summary>
    LibraryNotConfigured,

    /// <summary>Not beneath a configured final path prefix.</summary>
    PathOutsideFinalScope,
}

/// <summary>
/// Wire names for <see cref="ItemExclusion"/>, spelled out for the same reason
/// <see cref="ReasonCodes"/> are: a rename in C# must not change the plan schema.
/// </summary>
public static class ItemExclusions
{
    private static readonly Dictionary<ItemExclusion, string> Names = new()
    {
        [ItemExclusion.None] = "eligible",
        [ItemExclusion.UnsupportedType] = "unsupported_type",
        [ItemExclusion.VirtualOrExtra] = "virtual_or_extra",
        [ItemExclusion.MissingPath] = "missing_media_file",
        [ItemExclusion.LibraryNotConfigured] = "library_not_configured",
        [ItemExclusion.PathOutsideFinalScope] = "path_outside_final_scope",
    };

    /// <summary>Gets every exclusion reason.</summary>
    public static IReadOnlyList<ItemExclusion> All { get; } = [.. Names.Keys];

    /// <summary>
    /// Maps an exclusion to its stable wire name.
    /// </summary>
    /// <param name="exclusion">The exclusion to map.</param>
    /// <returns>The snake_case name used in plans and logs.</returns>
    public static string ToWire(ItemExclusion exclusion) =>
        Names.TryGetValue(exclusion, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(exclusion));
}

/// <summary>
/// Decides which current items may receive recovered state (DESIGN §7.2).
/// </summary>
public static class ItemEligibility
{
    /// <summary>
    /// Evaluates one item against the configured scope.
    /// </summary>
    /// <param name="item">The current item.</param>
    /// <param name="options">The configured scope.</param>
    /// <returns><see cref="ItemExclusion.None"/> when the item is an eligible target.</returns>
    public static ItemExclusion Evaluate(CurrentItemSnapshot item, AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);

        if (item.Kind is not (ItemKind.Movie or ItemKind.Episode))
        {
            return ItemExclusion.UnsupportedType;
        }

        if (item.IsVirtualItem || item.IsExtraOrTrailer)
        {
            return ItemExclusion.VirtualOrExtra;
        }

        // Membership before the path, and the order is load-bearing rather than
        // arbitrary. Every current movie and episode on the server is collected,
        // most of them in libraries nobody ticked, and asking about a file in one
        // of those answers a question no verdict depends on: an item outside the
        // selection is excluded whatever its file is doing. Asking anyway made a
        // library the operator deliberately left out — an offline NFS share, a
        // detached USB disk — count towards the "a mount is probably missing"
        // warning about the libraries they did tick.
        if (!item.LibraryIds.Intersect(options.EligibleLibraryIds).Any())
        {
            return ItemExclusion.LibraryNotConfigured;
        }

        if (string.IsNullOrWhiteSpace(item.Path) || (options.RequirePathExists && !item.PathExists))
        {
            return ItemExclusion.MissingPath;
        }

        if (!PathScope.IsBeneathAny(item.Path, options.FinalPathPrefixes, options.PathComparison))
        {
            return ItemExclusion.PathOutsideFinalScope;
        }

        return ItemExclusion.None;
    }

    /// <summary>
    /// Maps an exclusion to the reason code reported for a detached row whose
    /// only match was that item.
    /// </summary>
    /// <param name="exclusion">The exclusion to map.</param>
    /// <returns>The reason code for the report.</returns>
    public static ReasonCode ToReasonCode(ItemExclusion exclusion) => exclusion switch
    {
        ItemExclusion.PathOutsideFinalScope => ReasonCode.PathOutsideFinalScope,
        ItemExclusion.None => throw new ArgumentOutOfRangeException(nameof(exclusion)),
        _ => ReasonCode.UnsupportedCurrentItem,
    };
}
