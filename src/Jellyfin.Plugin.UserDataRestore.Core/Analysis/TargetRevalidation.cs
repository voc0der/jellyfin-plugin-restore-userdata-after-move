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
/// <para>So every per-item condition is evaluated again against a freshly read
/// snapshot: kind, virtual/extra status, path, path existence, library
/// membership, final-path prefix, and — the one that actually carries the
/// identity — whether the item still reports the keys the stranded rows matched
/// on. Anything short of all of them fails closed and the write is skipped; the
/// stranded row is untouched, so the next run reconsiders it from scratch.</para>
/// <para>What this cannot re-establish is <em>uniqueness</em>, which is a
/// property of the whole catalogue rather than of one item: another item
/// acquiring the same key would make the match ambiguous, and noticing that means
/// rebuilding the reverse index over every movie and episode on the server. The
/// caller covers the bulk case instead by refusing to run while a library scan is
/// in progress and by abandoning the remaining writes if one starts mid-run.</para>
/// </remarks>
public static class TargetRevalidation
{
    /// <summary>The wire prefix reported when the target stopped answering to a matched key.</summary>
    public const string KeyNoLongerReported = "key_no_longer_reported";

    /// <summary>
    /// Checks one target against the conditions that admitted it.
    /// </summary>
    /// <param name="target">A snapshot of the item as it is right now.</param>
    /// <param name="options">The same scope the analysis ran under.</param>
    /// <param name="requiredKeys">The detached keys that matched this target.</param>
    /// <returns>
    /// <see langword="null"/> when the target still qualifies, otherwise a short
    /// wire reason naming the first condition it now fails.
    /// </returns>
    public static string? Evaluate(
        CurrentItemSnapshot target,
        AnalysisOptions options,
        IReadOnlyList<string> requiredKeys)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requiredKeys);

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
        return missing.Length == 0
            ? null
            : KeyNoLongerReported + ":" + string.Join(' ', missing);
    }
}
