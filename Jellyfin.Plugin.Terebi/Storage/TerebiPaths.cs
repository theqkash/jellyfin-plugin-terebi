using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Terebi.Storage;

/// <summary>
/// Resolves the plugin data folder from Jellyfin's application paths, independent of plugin instance timing.
/// </summary>
public sealed class TerebiPaths
{
    public TerebiPaths(IApplicationPaths applicationPaths)
    {
        DataPath = Path.Combine(applicationPaths.DataPath, "terebi");
        Directory.CreateDirectory(DataPath);
    }

    public string DataPath { get; }

    public string File(string name) => Path.Combine(DataPath, name);

    public string Folder(string name)
    {
        var p = Path.Combine(DataPath, name);
        Directory.CreateDirectory(p);
        return p;
    }
}
