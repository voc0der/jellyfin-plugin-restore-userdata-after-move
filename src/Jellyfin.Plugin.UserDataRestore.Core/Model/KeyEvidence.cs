namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// How much a matched key says about identity (DESIGN §7.2).
/// </summary>
/// <remarks>
/// This annotates a key without changing its value. It exists because an exact
/// match on a bare number is not proof: Jellyfin stores TMDb IDs with no provider
/// namespace and no item type, so a key that is unique among <em>current</em>
/// items can still have belonged to a now-absent item of another type
/// (DESIGN §7.3).
/// </remarks>
public enum KeyEvidence
{
    /// <summary>The key is not recognizable from the target's metadata.</summary>
    Unknown = 0,

    /// <summary>The key is a provider ID of the target, or of its series, other than IMDb.</summary>
    OtherProvider = 1,

    /// <summary>The key is the target's — or its series' — IMDb ID.</summary>
    Imdb = 2,

    /// <summary>The key is the target series' IMDb ID plus this episode's padded season and episode numbers.</summary>
    SeriesImdbEpisode = 3,

    /// <summary>The key parses as a GUID and equals the current item's ID.</summary>
    CurrentItemGuid = 4,
}

/// <summary>
/// Wire names for <see cref="KeyEvidence"/>.
/// </summary>
public static class KeyEvidenceNames
{
    /// <summary>
    /// Maps evidence to its stable wire name.
    /// </summary>
    /// <param name="evidence">The evidence to map.</param>
    /// <returns>The snake_case name used in plans.</returns>
    public static string ToWire(KeyEvidence evidence) => evidence switch
    {
        KeyEvidence.CurrentItemGuid => "current_item_guid",
        KeyEvidence.Imdb => "imdb",
        KeyEvidence.SeriesImdbEpisode => "series_imdb_episode",
        KeyEvidence.OtherProvider => "other_provider",
        KeyEvidence.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
    };
}
