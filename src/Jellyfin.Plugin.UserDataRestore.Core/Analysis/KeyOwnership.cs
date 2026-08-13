using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Which current items answer to each user-data key, and nothing else.
/// </summary>
/// <remarks>
/// <para>The uniqueness half of <see cref="CurrentKeyIndex"/>, split out so the
/// apply pass can re-establish it without paying for the rest. Uniqueness is the
/// one condition behind a write that is a property of the whole catalogue rather
/// than of the target: no amount of looking at one item reveals that a second item
/// has started reporting its key.</para>
/// <para>Deliberately cheap. It carries item IDs rather than snapshots, and the
/// host builds it without stat-ing a single file, because the filesystem check is
/// the slowest thing in a run and eligibility is not what this answers. Judged
/// across <em>every</em> movie and episode on the server, the same wider scope
/// <see cref="CurrentKeyIndex"/> uses and for the same reason: a key claimed by an
/// item in an unconfigured library is still claimed.</para>
/// </remarks>
public sealed class KeyOwnership
{
    private readonly Dictionary<string, List<Guid>> _owners;

    private KeyOwnership(Dictionary<string, List<Guid>> owners, int itemCount)
    {
        _owners = owners;
        ItemCount = itemCount;
    }

    /// <summary>Gets the number of distinct keys any current item reports.</summary>
    public int DistinctKeyCount => _owners.Count;

    /// <summary>Gets the number of current items that were indexed.</summary>
    public int ItemCount { get; }

    /// <summary>
    /// Indexes the current catalog by key.
    /// </summary>
    /// <param name="items">Every current movie and episode known to the server.</param>
    /// <returns>The ownership index.</returns>
    public static KeyOwnership Build(IEnumerable<CurrentItemSnapshot> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var owners = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var count = 0;

        foreach (var item in items)
        {
            count++;

            foreach (var key in item.UserDataKeys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!owners.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    owners[key] = bucket;
                }

                // One item can list the same key twice; it is still one owner.
                if (!bucket.Contains(item.ItemId))
                {
                    bucket.Add(item.ItemId);
                }
            }
        }

        return new KeyOwnership(owners, count);
    }

    /// <summary>
    /// The current items reporting one key.
    /// </summary>
    /// <param name="key">The key exactly as stored in the detached row.</param>
    /// <returns>The items reporting it, empty when none do.</returns>
    public IReadOnlyList<Guid> Owners(string? key) =>
        key is not null && _owners.TryGetValue(key, out var bucket) ? bucket : [];

    /// <summary>
    /// Whether one item is the only current item reporting a key.
    /// </summary>
    /// <param name="key">The key exactly as stored in the detached row.</param>
    /// <param name="itemId">The item that is supposed to own it.</param>
    /// <returns><see langword="true"/> when that item, and only that item, reports it.</returns>
    /// <remarks>
    /// A key nobody reports fails this too. The caller is about to write on the
    /// strength of the key belonging to this item, and "no current item claims it"
    /// does not support that.
    /// </remarks>
    public bool IsOwnedOnlyBy(string? key, Guid itemId)
    {
        var owners = Owners(key);
        return owners.Count == 1 && owners[0].Equals(itemId);
    }
}
