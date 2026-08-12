using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Gate0;

public class Gate0Config : BasePluginConfiguration
{
}

public class Gate0Plugin : BasePlugin<Gate0Config>
{
    public Gate0Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Gate0Plugin? Instance { get; private set; }

    public override string Name => "Gate 0 Probe";

    public override Guid Id => new("b7e4c1a2-3f5d-4e88-9a10-2c6f7d8e9b01");

    public override string Description => "Disposable probe: can a third-party plugin resolve the Jellyfin database context?";
}
