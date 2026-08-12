using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>How a detached key resolved against the current catalog.</summary>
public enum KeyMatchKind
{
    /// <summary>No current item reports this key.</summary>
    NoMatch = 0,

    /// <summary>Exactly one current item reports it, and that item is an eligible target.</summary>
    Unique = 1,

    /// <summary>Exactly one current item reports it, but that item is excluded.</summary>
    UniqueExcluded = 2,

    /// <summary>More than one current item reports it.</summary>
    Ambiguous = 3,
}

/// <summary>A current item that reported a key, with the reason it is or is not eligible.</summary>
/// <param name="ItemId">The current item ID.</param>
/// <param name="Kind">The item kind.</param>
/// <param name="Exclusion">Why the item is not an eligible target, if it is not.</param>
/// <param name="Name">The item name, for the report.</param>
/// <param name="Path">The item path, for the report.</param>
public readonly record struct MatchedItemRef(Guid ItemId, ItemKind Kind, ItemExclusion Exclusion, string? Name, string? Path);

/// <summary>The outcome of one reverse-index lookup.</summary>
/// <param name="Kind">How the key resolved.</param>
/// <param name="Target">The eligible target, when <see cref="Kind"/> is <see cref="KeyMatchKind.Unique"/>.</param>
/// <param name="Matches">Every current item that reported the key.</param>
public readonly record struct KeyLookup(KeyMatchKind Kind, CurrentItemSnapshot? Target, IReadOnlyList<MatchedItemRef> Matches);

/// <summary>
/// The ordinal, case-sensitive reverse index from <c>CustomDataKey</c> to current
/// items (DESIGN §7.2).
/// </summary>
/// <remarks>
/// <para>Keys are indexed exactly as Jellyfin produced them. Nothing is
/// normalized, lowercased, stripped, or synthesized.</para>
/// <para>Uniqueness is judged across <em>every</em> current movie and episode on
/// the server, not only those in configured libraries. This is stricter than
/// DESIGN §7.2 requires, and deliberately so: if the same title also exists in an
/// unconfigured library or still lingers at a vacated path mid-migration, a
/// stranded row genuinely cannot be attributed to one of them. Restricting the
/// scope would turn that ambiguity into a confident wrong answer.</para>
/// </remarks>
public sealed class CurrentKeyIndex
{
    private readonly Dictionary<string, List<CurrentItemSnapshot>> _byKey;
    private readonly Dictionary<Guid, ItemExclusion> _exclusions;

    private CurrentKeyIndex(
        Dictionary<string, List<CurrentItemSnapshot>> byKey,
        Dictionary<Guid, ItemExclusion> exclusions,
        int eligibleItemCount,
        int eligibleItemsWithProviderKeyCount)
    {
        _byKey = byKey;
        _exclusions = exclusions;
        EligibleItemCount = eligibleItemCount;
        EligibleItemsWithProviderKeyCount = eligibleItemsWithProviderKeyCount;
    }

    /// <summary>Gets the number of distinct keys in the index.</summary>
    public int DistinctKeyCount => _byKey.Count;

    /// <summary>Gets the number of items that are eligible recovery targets.</summary>
    public int EligibleItemCount { get; }

    /// <summary>
    /// Gets the number of eligible targets reporting a key other than their own GUID.
    /// </summary>
    /// <remarks>
    /// Zero here, with eligible targets present, means no provider-derived key
    /// exists to match against — either the library genuinely has no provider IDs,
    /// or the host handed over items whose metadata was never hydrated. The second
    /// case looks exactly like "nothing is recoverable" and is worth shouting
    /// about; it happened during the first live run of this plugin.
    /// </remarks>
    public int EligibleItemsWithProviderKeyCount { get; }

    /// <summary>
    /// Builds the index from the current catalog.
    /// </summary>
    /// <param name="items">Every current movie and episode known to the server.</param>
    /// <param name="options">The configured scope, used to mark eligibility.</param>
    /// <returns>The reverse index.</returns>
    public static CurrentKeyIndex Build(IEnumerable<CurrentItemSnapshot> items, AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        var byKey = new Dictionary<string, List<CurrentItemSnapshot>>(StringComparer.Ordinal);
        var exclusions = new Dictionary<Guid, ItemExclusion>();
        var eligible = 0;
        var eligibleWithProviderKey = 0;

        foreach (var item in items)
        {
            var exclusion = ItemEligibility.Evaluate(item, options);
            exclusions[item.ItemId] = exclusion;
            if (exclusion == ItemExclusion.None)
            {
                eligible++;
                if (item.UserDataKeys.Any(key => !IsOwnGuid(key, item.ItemId)))
                {
                    eligibleWithProviderKey++;
                }
            }

            foreach (var key in item.UserDataKeys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!byKey.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    byKey[key] = bucket;
                }

                // One item can list the same key twice; count it once.
                if (!bucket.Any(existing => existing.ItemId.Equals(item.ItemId)))
                {
                    bucket.Add(item);
                }
            }
        }

        return new CurrentKeyIndex(byKey, exclusions, eligible, eligibleWithProviderKey);
    }

    private static bool IsOwnGuid(string key, Guid itemId) =>
        Guid.TryParse(key, out var parsed) && parsed.Equals(itemId);

    /// <summary>
    /// Resolves one detached key.
    /// </summary>
    /// <param name="key">The key exactly as stored in the detached row.</param>
    /// <returns>The lookup outcome.</returns>
    public KeyLookup Lookup(string? key)
    {
        if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var bucket) || bucket.Count == 0)
        {
            return new KeyLookup(KeyMatchKind.NoMatch, null, []);
        }

        var matches = bucket
            .Select(item => new MatchedItemRef(item.ItemId, item.Kind, _exclusions[item.ItemId], item.Name, item.Path))
            .ToArray();

        if (bucket.Count > 1)
        {
            return new KeyLookup(KeyMatchKind.Ambiguous, null, matches);
        }

        var only = bucket[0];
        return _exclusions[only.ItemId] == ItemExclusion.None
            ? new KeyLookup(KeyMatchKind.Unique, only, matches)
            : new KeyLookup(KeyMatchKind.UniqueExcluded, null, matches);
    }
}
