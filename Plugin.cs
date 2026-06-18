using Jellyfin.Plugin.AIRecommendations.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AIRecommendations;

/// <summary>
/// AI Recommendations plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override Guid Id => Guid.Parse("7c4a9e2b-3f1d-4a8c-b6e5-2d9f8a1c0b3e");

    public override string Name => "AI Recommendations";

    public override string Description =>
        "Per-user AI movie and TV recommendations synced to virtual libraries on all Jellyfin clients.";

    public override string ConfigurationFileName => "Jellyfin.Plugin.AIRecommendations.xml";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
