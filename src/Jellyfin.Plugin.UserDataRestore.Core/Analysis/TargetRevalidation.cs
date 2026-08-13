using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Re-checks a recovery target against the facts that admitted it, immediately
/// before it is written to.
/// </summary>
/// <remarks>
/// <para>Analysis and apply happen in one pass, but not in one instant. Between
/// the two, a metadata refresh can rewrite an item's provider IDs, a library edit
/// can move it out of scope, and a file can vanish. The write would still land,
/// because the only thing checked at that point was whether the item still had
/// default user state — not whether it was still the item the evidence pointed
/// at.</para>
/// <para>So every condition is evaluated again: kind, virtual/extra status, path,
/// path existence, library membership, final-path prefix, whether the item still
/// reports the keys the stranded rows matched on, and whether it is still the only
/// item on the server that does. Anything short of all of them fails closed and
/// the write is skipped; the stranded row is untouched, so the next run
/// reconsiders it from scratch.</para>
/// <para>The first seven are properties of the target and are re-read from the
/// live item for each write. The eighth — uniqueness — is a property of the whole
/// catalogue, so it is answered from a <see cref="KeyOwnership"/> index the caller
/// rebuilds once at the start of the apply pass rather than once per write:
/// establishing it costs a pass over every movie and episode on the server, which
/// is affordable per run and not per write. Drift inside the loop itself is
/// therefore not caught by this check, and the caller covers the case that
/// produces it in bulk — a library scan — by refusing to start while one is
/// running and abandoning the remaining writes if one begins.</para>
/// </remarks>
public static class TargetRevalidation
{
    /// <summary>The wire prefix reported when the target stopped answering to a matched key.</summary>
    public const string KeyNoLongerReported = "key_no_longer_reported";

    /// <summary>The wire prefix reported when a matched key is no longer the target's alone.</summary>
    public const string KeyNoLongerUnique = "key_no_longer_unique";

    /// <summary>
    /// Checks one target against the conditions that admitted it.
    /// </summary>
    /// <param name="target">A snapshot of the item as it is right now.</param>
    /// <param name="options">The same scope the analysis ran under.</param>
    /// <param name="requiredKeys">The detached keys that matched this target.</param>
    /// <param name="ownership">Who reports each key across the current catalog.</param>
    /// <returns>
    /// <see langword="null"/> when the target still qualifies, otherwise a short
    /// wire reason naming the first condition it now fails.
    /// </returns>
    public static string? Evaluate(
        CurrentItemSnapshot target,
        AnalysisOptions options,
        IReadOnlyList<string> requiredKeys,
        KeyOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requiredKeys);
        ArgumentNullException.ThrowIfNull(ownership);

        var exclusion = ItemEligibility.Evaluate(target, options);
        if (exclusion != ItemExclusion.None)
        {
            return ItemExclusions.ToWire(exclusion);
        }

        // A write admitted by no key at all would revalidate vacuously, which is
        // the one outcome this must never produce.
        if (requiredKeys.Count == 0)
        {
            return KeyNoLongerReported + ":none_recorded";
        }

        // Ordinal and case-sensitive, exactly as CurrentKeyIndex matched them.
        var reported = target.UserDataKeys.ToHashSet(StringComparer.Ordinal);
        var missing = requiredKeys
            .Where(key => !reported.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // All of them, not any: the evidence rule weighed the keys as a set, and a
        // target that has stopped answering to one of them is not the target that
        // set described.
        if (missing.Length > 0)
        {
            return KeyNoLongerReported + ":" + string.Join(' ', missing);
        }

        // Still answering to every key is not the same as still being the only
        // thing that does. A second item acquiring one of these keys — a refresh
        // identifying it as the same title, a duplicate arriving in another library
        // — is exactly the ambiguity the analysis refuses to guess through, and it
        // is invisible from the target alone.
        var shared = requiredKeys
            .Where(key => !ownership.IsOwnedOnlyBy(key, target.ItemId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return shared.Length == 0
            ? null
            : KeyNoLongerUnique + ":" + string.Join(' ', shared);
    }
}
