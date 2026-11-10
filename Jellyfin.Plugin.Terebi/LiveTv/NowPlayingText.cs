using System.Globalization;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Epg;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>
/// Text shown on folder-tree items. Jellyfin has no guide view for channel items, so the current programme
/// goes into the item name (visible on the card) and now/next into the overview (visible on the detail page).
/// </summary>
public static class NowPlayingText
{
    public static string Name(LogicalChannel c, EpgProgram? now)
    {
        var name = c.Number > 0 ? c.Number.ToString("0000", CultureInfo.InvariantCulture) + "  " + c.Name : c.Name;
        if (now is not null && (Plugin.Instance?.Configuration.ShowNowPlayingInFolderNames ?? true) && !string.IsNullOrWhiteSpace(now.Title))
        {
            name += "  \u00b7  " + now.Title.Trim();
        }

        return name;
    }

    public static string? Overview(EpgProgram? now, EpgProgram? next)
    {
        if (now is null && next is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (now is not null)
        {
            parts.Add("Now: " + Line(now));
        }

        if (next is not null)
        {
            parts.Add("Next: " + Line(next));
        }

        return string.Join('\n', parts);
    }

    private static string Line(EpgProgram p)
    {
        var start = p.Start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var end = p.End.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var title = p.Title.Trim() + (string.IsNullOrWhiteSpace(p.SubTitle) ? string.Empty : ": " + p.SubTitle.Trim());
        return start + " to " + end + "  " + title;
    }
}
