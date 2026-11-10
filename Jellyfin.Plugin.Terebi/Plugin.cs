using System.Globalization;
using Jellyfin.Plugin.Terebi.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Terebi;

/// <summary>
/// Terebi: many IPTV sources (Xtream, M3U), one clean channel list for Jellyfin Live TV.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("7d3e1b52-6f4a-4c8e-9b0d-2a5c7e9f1b34");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataPath = Path.Combine(applicationPaths.DataPath, "terebi");
        Directory.CreateDirectory(DataPath);
        if (string.IsNullOrEmpty(Configuration.StreamKey))
        {
            Configuration.StreamKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Terebi";

    public override Guid Id => PluginId;

    public override string Description => "Aggregates Xtream and M3U sources into merged live TV channels with failover, health checks and EPG.";

    /// <summary>Gets the folder for catalog, lineup, EPG cache and health data.</summary>
    public string DataPath { get; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Web.Pages.terebi.html", ns),
            EnableInMainMenu = false,
        };
        yield return new PluginPageInfo
        {
            Name = "terebi.js",
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Web.Pages.terebi.js", ns),
        };
    }
}
