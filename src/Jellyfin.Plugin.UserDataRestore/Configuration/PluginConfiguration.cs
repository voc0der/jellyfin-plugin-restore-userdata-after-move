using Jellyfin.Plugin.UserDataRestore.Core.Applying;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UserDataRestore.Configuration;

/// <summary>
/// Persistent plugin configuration (DESIGN §6.1).
/// </summary>
/// <remarks>
/// The apply-side settings (arming, backup acknowledgement, write caps) are
/// absent because this build has no apply task. They arrive with Milestone 3,
/// together with the code that enforces them.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the libraries whose items may receive recovered state. Empty
    /// means every movie and TV library on the server.
    /// </summary>
    /// <remarks>
    /// Stored as strings because that is what the configuration page posts back.
    /// Unparseable entries are dropped, not guessed at.
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

    /// <summary>
    /// Gets or sets the one-time authorization for an apply run (DESIGN §6.3).
    /// </summary>
    /// <remarks>
    /// Cleared and persisted by the apply task before its first write, so a crash
    /// mid-run cannot be retried without an administrator looking at the result
    /// first.
    /// </remarks>
    public ArmState Arm { get; set; } = new();
}
