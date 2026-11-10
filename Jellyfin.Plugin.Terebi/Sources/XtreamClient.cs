using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Terebi.Catalog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Sources;

/// <summary>
/// Minimal Xtream Codes client: account info, live categories, live streams, short EPG.
/// Providers return sloppy JSON (numbers as strings, empty arrays as objects), hence the lenient readers.
/// </summary>
public sealed class XtreamClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XtreamClient> _logger;

    public XtreamClient(IHttpClientFactory httpClientFactory, ILogger<XtreamClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public sealed record AccountInfo(string Status, int MaxConnections, int ActiveConnections, DateTimeOffset? Expires, string[] Formats, string? Timezone);

    public sealed record EpgEntry(DateTimeOffset Start, DateTimeOffset End, string Title, string? Description);

    public static string BaseUrl(SourceDefinition s) => s.Url.TrimEnd('/') + "/";

    /// <summary>The account's XMLTV feed.</summary>
    public static string XmltvUrl(SourceDefinition s) => $"{BaseUrl(s)}xmltv.php?username={Uri.EscapeDataString(s.Username ?? string.Empty)}&password={Uri.EscapeDataString(s.Password ?? string.Empty)}";

    public static string StreamUrl(SourceDefinition s, string streamId)
    {
        var ext = string.Equals(s.XtreamOutput, "m3u8", StringComparison.OrdinalIgnoreCase) ? "m3u8" : "ts";
        return $"{BaseUrl(s)}live/{Uri.EscapeDataString(s.Username ?? string.Empty)}/{Uri.EscapeDataString(s.Password ?? string.Empty)}/{streamId}.{ext}";
    }

    public async Task<AccountInfo> GetAccountAsync(SourceDefinition s, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(s, null, cancellationToken).ConfigureAwait(false);
        var ui = doc.RootElement.TryGetProperty("user_info", out var u) ? u : default;
        var si = doc.RootElement.TryGetProperty("server_info", out var sv) ? sv : default;
        var formats = ui.ValueKind == JsonValueKind.Object && ui.TryGetProperty("allowed_output_formats", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
            : [];
        DateTimeOffset? exp = null;
        var expRaw = ReadLong(ui, "exp_date");
        if (expRaw > 0)
        {
            exp = DateTimeOffset.FromUnixTimeSeconds(expRaw);
        }

        return new AccountInfo(
            ReadString(ui, "status") ?? "unknown",
            (int)ReadLong(ui, "max_connections"),
            (int)ReadLong(ui, "active_cons"),
            exp,
            formats,
            si.ValueKind == JsonValueKind.Object ? ReadString(si, "timezone") : null);
    }

    /// <summary>Downloads categories and live streams and converts them into raw catalog entries.</summary>
    public async Task<List<RawStream>> GetLiveStreamsAsync(SourceDefinition s, CancellationToken cancellationToken)
    {
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var doc = await GetJsonAsync(s, "get_live_categories", cancellationToken).ConfigureAwait(false))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    var id = ReadString(c, "category_id");
                    var name = ReadString(c, "category_name");
                    if (id is not null && name is not null)
                    {
                        categories[id] = name.Trim();
                    }
                }
            }
        }

        var list = new List<RawStream>();
        using (var doc = await GetJsonAsync(s, "get_live_streams", cancellationToken).ConfigureAwait(false))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("get_live_streams did not return a list");
            }

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var id = ReadString(e, "stream_id");
                var name = ReadString(e, "name");
                if (id is null || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var catId = ReadString(e, "category_id") ?? string.Empty;
                list.Add(new RawStream
                {
                    Key = id,
                    Name = name.Trim(),
                    Category = categories.TryGetValue(catId, out var cn) ? cn : catId,
                    Logo = ReadString(e, "stream_icon"),
                    EpgId = NullIfEmpty(ReadString(e, "epg_channel_id")),
                    Url = StreamUrl(s, id),
                    Number = (int?)ReadNullableLong(e, "num"),
                    CatchupDays = ReadLong(e, "tv_archive") == 1 ? (int)Math.Max(1, ReadLong(e, "tv_archive_duration")) : 0,
                });
            }
        }

        return list;
    }

    /// <summary>Short EPG for one stream (the provider decides how many entries; usually the next few hours).</summary>
    public async Task<List<EpgEntry>> GetShortEpgAsync(SourceDefinition s, string streamId, int limit, CancellationToken cancellationToken)
    {
        var result = new List<EpgEntry>();
        using var doc = await GetJsonAsync(s, "get_short_epg", cancellationToken, ("stream_id", streamId), ("limit", limit.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("epg_listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var e in listings.EnumerateArray())
        {
            var start = ReadLong(e, "start_timestamp");
            var end = ReadLong(e, "stop_timestamp");
            if (start <= 0 || end <= start)
            {
                continue;
            }

            result.Add(new EpgEntry(
                DateTimeOffset.FromUnixTimeSeconds(start),
                DateTimeOffset.FromUnixTimeSeconds(end),
                DecodeBase64(ReadString(e, "title")) ?? "?",
                DecodeBase64(ReadString(e, "description"))));
        }

        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(SourceDefinition s, string? action, CancellationToken cancellationToken, params (string Key, string Value)[] extra)
    {
        var query = new StringBuilder($"username={Uri.EscapeDataString(s.Username ?? string.Empty)}&password={Uri.EscapeDataString(s.Password ?? string.Empty)}");
        if (action is not null)
        {
            query.Append("&action=").Append(action);
        }

        foreach (var (k, v) in extra)
        {
            query.Append('&').Append(k).Append('=').Append(Uri.EscapeDataString(v));
        }

        var url = BaseUrl(s) + "player_api.php?" + query;
        var http = _httpClientFactory.CreateClient("Terebi");
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(s.UserAgent ?? Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    private static long ReadLong(JsonElement e, string name) => ReadNullableLong(e, name) ?? 0;

    private static long? ReadNullableLong(JsonElement e, string name)
    {
        var s = ReadString(e, name);
        return s is not null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? DecodeBase64(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s)).Trim();
        }
        catch (FormatException)
        {
            return s;
        }
    }
}
