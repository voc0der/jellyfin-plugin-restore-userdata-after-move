using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UserDataRestore.Configuration;

/// <summary>
/// Persistent plugin configuration (DESIGN §6.1).
/// </summary>
/// <remarks>
/// Every setting here has a default that is right for almost everybody, which is
/// why the configuration page is nearly empty. There is nothing to arm and
/// nothing to acknowledge: running the task is itself the deliberate act, and the
/// guard that matters is the check made immediately before each individual write,
/// not a checkbox.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the libraries whose items may receive recovered state. Empty
    /// means every movie and TV library on the server.
    /// </summary>
    /// <remarks>
    /// Stored as strings because that is what the configuration page posts back.
    /// An unparseable entry fails the run rather than being dropped: dropping it
    /// and then finding nothing left is indistinguishable from "not configured",
    /// which means <em>every</em> library.
    /// </remarks>
    public string[] EligibleLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the final path prefixes a recovery target must sit beneath.
    /// Empty means the in-scope libraries' own locations, which is what the server
    /// already knows and what nearly everyone wants.
    /// </summary>
    public string[] FinalPathPrefixes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether items whose media file is missing
    /// are skipped. On by default: a missing file usually means a leftover item
    /// from an unfinished scan, and recovering onto one re-strands the data.
    /// </summary>
    public bool RequirePathExists { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every classified row is logged at
    /// debug level. The plan file always holds the full detail.
    /// </summary>
    public bool VerboseLogging { get; set; }
}
