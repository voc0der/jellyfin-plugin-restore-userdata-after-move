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
    /// Deprecated and always empty, meaning the in-scope libraries' own locations.
    /// </summary>
    /// <remarks>
    /// Retained only so an upgraded install's persisted value can be recognized
    /// and cleared; see <see cref="HasLegacyScopeOverrides"/>. No version since
    /// 1.0.0.8 offers a control for it.
    /// </remarks>
    public string[] FinalPathPrefixes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether items whose media file is missing
    /// are skipped. Deprecated and always on: a missing file usually means a
    /// leftover item from an unfinished scan, and recovering onto one re-strands
    /// the data.
    /// </summary>
    /// <remarks>
    /// Retained only so an upgraded install's persisted value can be recognized
    /// and cleared; see <see cref="HasLegacyScopeOverrides"/>. No version since
    /// 1.0.0.8 offers a control for it.
    /// </remarks>
    public bool RequirePathExists { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every classified row is logged at
    /// debug level. The plan file always holds the full detail.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Gets a value indicating whether this configuration still carries a scope
    /// setting from a version that had a control for it.
    /// </summary>
    /// <remarks>
    /// 1.0.0.7 and earlier exposed both path settings; 1.0.0.8 removed the
    /// controls but kept reading the fields, so an upgrade preserved whatever was
    /// last saved and went on honouring it from a page that no longer showed it.
    /// Either value changes which items a run may write to, and a setting that
    /// changes write scope must not be simultaneously active and uneditable — so
    /// a run that finds one says what it found and clears it.
    /// </remarks>
    public bool HasLegacyScopeOverrides => FinalPathPrefixes.Length > 0 || !RequirePathExists;
}
