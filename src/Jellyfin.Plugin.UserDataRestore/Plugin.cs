using System.Globalization;
using Jellyfin.Plugin.UserDataRestore.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.UserDataRestore;

/// <summary>
/// Restore User Data After Move.
/// </summary>
/// <remarks>
/// The name states the trigger and an outcome, never a mechanism: this plugin
/// leaves Jellyfin's sentinel rows exactly where it found them (DESIGN §5).
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Host application paths.</param>
    /// <param name="xmlSerializer">Host configuration serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Restore User Data After Move";

    /// <inheritdoc />
    public override Guid Id => new("6b416775-6a90-436f-a034-796c52f5a317");

    /// <inheritdoc />
    public override string Description =>
        "Finds user data Jellyfin stranded when a media path changed, and puts it back on the item it belongs to now.";

    /// <summary>
    /// Gets the directory plans are written to.
    /// </summary>
    // Join rather than Combine: Combine discards everything before a rooted
    // segment, so a rooted right-hand side silently relocates the whole path.
    // Nothing here is rooted, and Join keeps it that way if anything ever is.
    public string PlanDirectory => Path.Join(DataFolderPath, "plans");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        },
    ];
}
