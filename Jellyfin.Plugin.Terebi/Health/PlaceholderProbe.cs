using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Terebi.Health;

/// <summary>
/// Recognises provider placeholder clips. Xtream panels do not fail an offline channel: they loop a short
/// clip such as "black.ts" or "offline.ts" and the player shows a black screen. Both the health check and
/// the failover use this to treat such a stream as dead and move on.
/// </summary>
public static partial class PlaceholderProbe
{
    /// <summary>Codes an Xtream panel or a CDN returns when the connection limit is hit; not a stream failure.</summary>
    public static bool IsBusyStatus(string? kind) => kind is "http-458" or "http-429" or "http-503" or "http-509";

    public static bool IsBusyStatus(int status) => status is 458 or 429 or 503 or 509;

    /// <summary>Segment or file names providers use for "nothing here" clips.</summary>
    public static bool LooksLikePlaceholderName(string url)
    {
        var name = url;
        var q = name.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            name = name[..q];
        }

        name = name[(name.LastIndexOf('/') + 1)..];
        return PlaceholderNameRegex().IsMatch(name);
    }

    /// <summary>For an Xtream ".ts" live URL returns the ".m3u8" variant of the same stream.</summary>
    public static bool TryHlsVariant(string url, out string hlsUrl)
    {
        hlsUrl = string.Empty;
        var m = XtreamTsRegex().Match(url);
        if (!m.Success)
        {
            return false;
        }

        hlsUrl = url[..m.Index] + ".m3u8" + m.Groups["q"].Value;
        return true;
    }

    /// <summary>
    /// Fetches a media playlist and returns a reason when it is a finished playlist made of a placeholder clip,
    /// otherwise null. Any error also yields null: this is a hint, the real probe still runs.
    /// </summary>
    public static async Task<string?> CheckAsync(HttpClient http, string userAgent, string playlistUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
            request.Headers.UserAgent.ParseAdd(userAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > 256 * 1024)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Inspect(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Playlist text check shared with the HLS puller.</summary>
    public static string? Inspect(string playlist)
    {
        if (!playlist.StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            return null;
        }

        var lines = playlist.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var segments = lines.Where(l => !l.StartsWith('#')).ToList();
        var ended = lines.Any(l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal));
        if (segments.Count > 0 && segments.All(LooksLikePlaceholderName))
        {
            return "provider placeholder clip (" + segments[0][(segments[0].LastIndexOf('/') + 1)..] + ")";
        }

        if (ended && segments.Count == 1)
        {
            return "provider serves a single finished clip instead of a live stream";
        }

        return null;
    }

    [GeneratedRegex(@"^(black|blank|offline|off_?line|no[_\-]?signal|nosignal|no[_\-]?stream|unavailable|not[_\-]?available|maintenance|placeholder|static|standby|error|down)[^/]*\.(ts|m3u8|mp4)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderNameRegex();

    [GeneratedRegex(@"\.ts(?<q>\?.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex XtreamTsRegex();
}
