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
    /// Gets or sets the libraries whose items may receive recovered state.
    /// </summary>
    /// <remarks>
    /// Stored as strings because that is what the configuration page posts back.
    /// Unparseable entries are dropped, not guessed at.
    /// </remarks>
    public string[] EligibleLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the final path prefixes a recovery target must sit beneath.
    /// </summary>
    public string[] FinalPathPrefixes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a target's media path must exist
    /// on disk. Leave this on unless the library lives on storage that is
    /// deliberately offline during analysis.
    /// </summary>
    public bool RequirePathExists { get; set; } = true;

    /// <summary>
    /// Gets or sets how many plan files to keep (DESIGN §8).
    /// </summary>
    public int PlanRetentionCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether every classified row is logged at
    /// debug level. The plan file always holds the full detail.
    /// </summary>
    public bool VerboseLogging { get; set; }
}
