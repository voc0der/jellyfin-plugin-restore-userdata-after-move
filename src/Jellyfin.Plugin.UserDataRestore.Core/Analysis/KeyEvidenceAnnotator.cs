using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Core.Analysis;

/// <summary>
/// Annotates a matched key with identity evidence (DESIGN §7.2).
/// </summary>
/// <remarks>
/// <para>This never produces a key. It takes a key Jellyfin already returned from
/// <c>GetUserDataKeys()</c> and asks what kind of key it is, by comparing it
/// against the target's own provider metadata. Annotation can only make the
/// evidence rule refuse a candidate, never admit one it otherwise would not.</para>
/// <para>Comparisons are ordinal. A provider ID that differs only in case yields
/// weaker evidence, which fails closed.</para>
/// </remarks>
public static class KeyEvidenceAnnotator
{
    private const string ImdbProvider = "Imdb";

    /// <summary>
    /// Classifies one key against the item that reported it.
    /// </summary>
    /// <param name="item">The current item.</param>
    /// <param name="key">A key from that item's <c>GetUserDataKeys()</c>.</param>
    /// <returns>The evidence annotation.</returns>
    public static KeyEvidenceResult Annotate(CurrentItemSnapshot item, string key)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrEmpty(key))
        {
            return new KeyEvidenceResult(KeyEvidence.Unknown, null, false);
        }

        if (Guid.TryParse(key, out var parsed) && parsed.Equals(item.ItemId))
        {
            return new KeyEvidenceResult(KeyEvidence.CurrentItemGuid, null, false);
        }

        if (TryGetProvider(item.ProviderIds, ImdbProvider, out var imdb) && string.Equals(key, imdb, StringComparison.Ordinal))
        {
            return new KeyEvidenceResult(KeyEvidence.Imdb, ImdbProvider, false);
        }

        var episodeSuffix = GetEpisodeSuffix(item);
        if (episodeSuffix is not null)
        {
            // Episode.GetUserDataKeys() derives keys as <series key> + SSSEEE.
            foreach (var (provider, value) in item.SeriesProviderIds)
            {
                if (!string.Equals(key, value + episodeSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                return string.Equals(provider, ImdbProvider, StringComparison.OrdinalIgnoreCase)
                    ? new KeyEvidenceResult(KeyEvidence.SeriesImdbEpisode, provider, false)
                    : new KeyEvidenceResult(KeyEvidence.OtherProvider, provider, false);
            }

            // The series contributes its own GUID as a key too, so an episode can
            // hold <series GUID> + SSSEEE. That is strong evidence — the series
            // item itself was never replaced — but DESIGN §7.3 does not list it
            // among the sufficient cases, and widening the sufficient set is a
            // design change, not an implementation detail. Recorded, not admitted.
            if (item.SeriesId is { } seriesId
                && string.Equals(key, seriesId.ToString("D", CultureInfo.InvariantCulture) + episodeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return new KeyEvidenceResult(KeyEvidence.Unknown, null, true);
            }
        }

        foreach (var (provider, value) in item.ProviderIds)
        {
            if (string.Equals(key, value, StringComparison.Ordinal))
            {
                return new KeyEvidenceResult(KeyEvidence.OtherProvider, provider, false);
            }
        }

        return new KeyEvidenceResult(KeyEvidence.Unknown, null, false);
    }

    private static string? GetEpisodeSuffix(CurrentItemSnapshot item)
    {
        if (item.Kind != ItemKind.Episode || item.SeasonNumber is null || item.EpisodeNumber is null)
        {
            return null;
        }

        return item.SeasonNumber.Value.ToString("000", CultureInfo.InvariantCulture)
            + item.EpisodeNumber.Value.ToString("000", CultureInfo.InvariantCulture);
    }

    private static bool TryGetProvider(IReadOnlyDictionary<string, string> providers, string name, out string? value)
    {
        foreach (var (provider, candidate) in providers)
        {
            if (string.Equals(provider, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(candidate))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }
}
