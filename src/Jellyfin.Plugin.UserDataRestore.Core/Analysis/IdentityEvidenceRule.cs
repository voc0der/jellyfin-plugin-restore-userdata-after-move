using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>One detached row that resolved to a candidate target, with its key's annotation.</summary>
/// <param name="Row">The detached row.</param>
/// <param name="Evidence">The evidence its key carries for this target.</param>
/// <param name="ProviderName">The provider that produced the key, when known.</param>
/// <param name="SeriesGuidDerived">Whether the key is series-GUID derived (recorded, not admitted).</param>
public readonly record struct ContributingKey(
    DetachedUserDataRow Row,
    KeyEvidence Evidence,
    string? ProviderName,
    bool SeriesGuidDerived)
{
    /// <summary>Gets the key value.</summary>
    public string Key => Row.CustomDataKey ?? string.Empty;
}

/// <summary>The verdict of the identity-evidence rule.</summary>
/// <param name="IsSufficient">Whether the candidate may be applied.</param>
/// <param name="Rule">Which case of DESIGN §7.3 was satisfied, or <c>none</c>.</param>
public readonly record struct IdentityEvidenceVerdict(bool IsSufficient, string Rule);

/// <summary>
/// The identity-evidence rule of DESIGN §7.3.
/// </summary>
/// <remarks>
/// A unique match is not enough. Jellyfin stores a TMDb ID as a bare number with
/// no provider namespace and no item type, and the detached row no longer knows
/// what kind of item it described, so a number that is unique among current items
/// can still have belonged to a now-absent item of a different type. One of three
/// things must hold before a candidate is applied.
/// </remarks>
public static class IdentityEvidenceRule
{
    /// <summary>Rule name: a contributing key is the exact current item GUID.</summary>
    public const string CurrentItemGuidRule = "current_item_guid";

    /// <summary>Rule name: a contributing key is the item's or series' IMDb key.</summary>
    public const string ImdbRule = "imdb";

    /// <summary>Rule name: two provider-derived keys corroborate one another.</summary>
    public const string CorroboratingProviderKeysRule = "two_corroborating_provider_keys";

    /// <summary>Rule name: nothing sufficient was found.</summary>
    public const string NoneRule = "none";

    /// <summary>
    /// Evaluates the rule for one <c>(user, target)</c> group.
    /// </summary>
    /// <param name="keys">The contributing keys of the group.</param>
    /// <returns>The verdict, naming the case that was satisfied.</returns>
    public static IdentityEvidenceVerdict Evaluate(IReadOnlyList<ContributingKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        // Case 1: the key is the current item's own GUID. The row was written for
        // this exact item.
        if (keys.Any(k => k.Evidence == KeyEvidence.CurrentItemGuid))
        {
            return new IdentityEvidenceVerdict(true, CurrentItemGuidRule);
        }

        // Case 2: an IMDb key. IMDb IDs identify one title across media types, so
        // the missing namespace problem does not apply.
        if (keys.Any(k => k.Evidence is KeyEvidence.Imdb or KeyEvidence.SeriesImdbEpisode))
        {
            return new IdentityEvidenceVerdict(true, ImdbRule);
        }

        // Case 3: two distinct provider-derived keys whose rows agree exactly,
        // including a retention stamp both of them actually carry — corroboration
        // rather than proof, but it removes the unsafe single-bare-number inference
        // while keeping TMDb-only recovery where Jellyfin wrote more than one
        // usable key.
        var providerKeys = keys
            .Where(k => k.Evidence is KeyEvidence.OtherProvider or KeyEvidence.Imdb or KeyEvidence.SeriesImdbEpisode)
            .GroupBy(k => k.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();

        for (var i = 0; i < providerKeys.Length; i++)
        {
            for (var j = i + 1; j < providerKeys.Length; j++)
            {
                if (Corroborates(providerKeys[i].Row, providerKeys[j].Row))
                {
                    return new IdentityEvidenceVerdict(true, CorroboratingProviderKeysRule);
                }
            }
        }

        return new IdentityEvidenceVerdict(false, NoneRule);
    }

    // The retention stamp is the whole of the corroboration: identical state
    // proves nothing on its own, because "watched once, never resumed, no rating"
    // is what most rows look like, and two such rows written years apart about
    // different titles match on every other field. Only the stamp says they were
    // detached by the same event.
    //
    // Two missing stamps are therefore not a match, they are two absences, and
    // `null == null` would read them as agreement — the exact inference this rule
    // exists to refuse. A row without a stamp is not rejected outright: it can
    // still be recovered under the GUID or IMDb rules, which do not need one. It
    // just cannot corroborate, or be corroborated by, anything.
    private static bool Corroborates(DetachedUserDataRow a, DetachedUserDataRow b) =>
        RecoveryStateComparer.Exact.Equals(a.State, b.State)
        && DateTimeNormalization.ToUtc(a.RetentionDate) is { } left
        && DateTimeNormalization.ToUtc(b.RetentionDate) is { } right
        && left == right;
}
