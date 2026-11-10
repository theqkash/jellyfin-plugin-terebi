using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Epg;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.LiveTv;
using Jellyfin.Plugin.Terebi.Logos;
using Jellyfin.Plugin.Terebi.Sources;
using Jellyfin.Plugin.Terebi.Streaming;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Api;

/// <summary>
/// Admin API behind the Terebi settings pages. Everything requires an elevated (admin) session.
/// </summary>
[ApiController]
[Route("Terebi")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public sealed class TerebiController : ControllerBase
{
    private readonly LineupStore _store;
    private readonly CatalogStore _catalog;
    private readonly SourceRefresher _refresher;
    private readonly LineupBuilder _builder;
    private readonly HealthChecker _health;
    private readonly EpgService _epg;
    private readonly StreamManager _streams;
    private readonly XtreamClient _xtream;
    private readonly IApplicationPaths _paths;
    private readonly Storage.TerebiPaths _terebiPaths;
    private readonly GuideRefreshQueue _guide;
    private readonly LogoCache _logos;
    private readonly LogoIndex _logoIndex;
    private readonly StreamWatchdog _watchdog;
    private readonly ILogger<TerebiController> _logger;

    public TerebiController(LineupStore store, CatalogStore catalog, SourceRefresher refresher, LineupBuilder builder, HealthChecker health, EpgService epg, StreamManager streams, XtreamClient xtream, IApplicationPaths paths, Storage.TerebiPaths terebiPaths, GuideRefreshQueue guide, LogoCache logos, LogoIndex logoIndex, StreamWatchdog watchdog, ILogger<TerebiController> logger)
    {
        _terebiPaths = terebiPaths;
        _watchdog = watchdog;
        _logos = logos;
        _logoIndex = logoIndex;
        _store = store;
        _catalog = catalog;
        _refresher = refresher;
        _builder = builder;
        _health = health;
        _epg = epg;
        _streams = streams;
        _xtream = xtream;
        _paths = paths;
        _guide = guide;
        _logger = logger;
    }

    // ---- Overview ----------------------------------------------------------------------------

    [HttpGet("Status")]
    public ActionResult<object> GetStatus()
    {
        var lineup = _store.Lineup;
        var refs = lineup.Channels.Where(c => c.Enabled).SelectMany(c => c.Streams.Where(s => s.Enabled).Select(s => s.Ref)).Distinct(StringComparer.Ordinal).ToList();
        var dead = refs.Count(r => _health.Get(r).IsDead);
        var unknown = refs.Count(r => _health.Get(r).Unknown);
        return new
        {
            version = Plugin.Instance?.Version.ToString(),
            sources = _store.Sources.Select(s => new { id = s.Id, name = s.Name, type = s.Type.ToString(), enabled = s.Enabled, streamCount = s.StreamCount, lastRefreshUtc = s.LastRefreshUtc, lastError = s.LastError, maxConnections = s.MaxConnections }),
            channels = lineup.Channels.Count,
            enabledChannels = lineup.Channels.Count(c => c.Enabled),
            streams = refs.Count,
            deadStreams = dead,
            unprobedStreams = unknown,
            openStreams = _streams.Open.Select(s => new { channelName = s.ChannelName, activeSourceId = s.ActiveSourceId, activeStreamRef = s.ActiveStreamRef, consumerCount = s.ConsumerCount, externalConsumers = s.ExternalConsumers, switches = s.Switches, openedUtc = s.OpenedUtc, bytesWritten = s.BytesWritten, secondsSinceRead = (int)(DateTimeOffset.UtcNow - s.LastReadUtc).TotalSeconds, watchingSessions = _watchdog.WatchingSessions(s.ChannelId) }),
            epg = _epg.Sources.Select(e => new { url = e.Url, fetchedUtc = e.FetchedUtc, error = e.Error, channels = e.Channels.Count, programmes = e.Programmes }),
            logos = LogoStats(),
            guideRefreshRunning = _guide.IsRunning,
            lineupUpdatedUtc = lineup.UpdatedUtc,
        };
    }

    /// <summary>Runs a background job and logs its failure instead of losing it in an unobserved task.</summary>
    private void Fire(string what, Func<Task> job)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await job().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Terebi: {What} failed", what);
            }
        });
    }

    private object LogoStats()
    {
        var (cached, failed, pending) = _logos.Stats(_refresher.LogoUrls());
        return new { cached, failed, pending, indexKeys = _logoIndex.Count, indexFetchedUtc = _logoIndex.FetchedUtc };
    }

    // ---- Logos -------------------------------------------------------------------------------

    /// <summary>Re-reads the iptv-org index if due, rebuilds the lineup so logo choices are applied, downloads missing logos, refreshes the guide.</summary>
    [HttpPost("Logos/Refresh")]
    public IActionResult RefreshLogos([FromQuery] bool retryFailed = true)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _logoIndex.EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(false);
                await _builder.RebuildAsync(CancellationToken.None).ConfigureAwait(false);
                var added = await _refresher.WarmLogosAsync(Timeout.InfiniteTimeSpan, CancellationToken.None, retryFailed).ConfigureAwait(false);
                _guide.Request($"logo refresh, {added} new logos");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Terebi: logo refresh failed");
            }
        });
        return Accepted();
    }

    // ---- Sources -----------------------------------------------------------------------------

    [HttpGet("Sources")]
    public ActionResult<List<SourceDefinition>> GetSources() => _store.Sources.Select(Redact).ToList();

    [HttpPost("Sources")]
    public async Task<ActionResult<SourceDefinition>> SaveSource([FromBody] SourceDefinition source, CancellationToken cancellationToken)
    {
        var existing = _store.GetSource(source.Id);
        if (existing is null)
        {
            _store.Sources.Add(source);
            existing = source;
        }
        else
        {
            // Keep the stored password when the UI sends the redacted placeholder.
            if (source.Password == "********")
            {
                source.Password = existing.Password;
            }

            var idx = _store.Sources.IndexOf(existing);
            _store.Sources[idx] = source;
            existing = source;
        }

        await _store.SaveSourcesAsync(cancellationToken).ConfigureAwait(false);
        return Redact(existing);
    }

    [HttpDelete("Sources/{id}")]
    public async Task<IActionResult> DeleteSource(string id, CancellationToken cancellationToken)
    {
        _store.Sources.RemoveAll(s => s.Id == id);
        _catalog.RemoveSource(id);
        foreach (var c in _store.Lineup.Channels)
        {
            c.Streams.RemoveAll(s => s.SourceId == id);
        }

        await _store.SaveSourcesAsync(cancellationToken).ConfigureAwait(false);
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("Sources/{id}/Test")]
    public async Task<ActionResult<object>> TestSource(string id, CancellationToken cancellationToken)
    {
        var source = _store.GetSource(id);
        if (source is null)
        {
            return NotFound();
        }

        try
        {
            if (source.Type == SourceType.Xtream)
            {
                var info = await _xtream.GetAccountAsync(source, cancellationToken).ConfigureAwait(false);
                if (info.MaxConnections > 0 && source.MaxConnections == 0)
                {
                    source.MaxConnections = info.MaxConnections;
                    await _store.SaveSourcesAsync(cancellationToken).ConfigureAwait(false);
                }

                return new { ok = info.Status.Equals("Active", StringComparison.OrdinalIgnoreCase), message = $"{info.Status}, max connections {info.MaxConnections}, active {info.ActiveConnections}, expires {info.Expires:yyyy-MM-dd}, formats {string.Join('/', info.Formats)}" };
            }

            var count = await _refresher.RefreshSourceAsync(source, cancellationToken).ConfigureAwait(false);
            return new { ok = count >= 0, message = count >= 0 ? $"{count} streams" : source.LastError };
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    [HttpPost("Sources/Refresh")]
    public IActionResult RefreshSources()
    {
        Fire("source refresh", () => _refresher.RefreshAllAsync(null, CancellationToken.None));
        return Accepted();
    }

    // ---- Catalog (raw streams) ----------------------------------------------------------------

    [HttpGet("Catalog")]
    public ActionResult<object> GetCatalog([FromQuery] string? sourceId, [FromQuery] string? q, [FromQuery] string? category, [FromQuery] int skip = 0, [FromQuery] int take = 200)
    {
        IEnumerable<RawStream> items = sourceId is null ? _catalog.All() : _catalog.ForSource(sourceId);
        if (!string.IsNullOrWhiteSpace(category))
        {
            items = items.Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            items = items.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (s.EpgId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var assigned = _store.Lineup.Channels.SelectMany(c => c.Streams.Select(s => (s.Ref, c.Id, c.Name))).ToDictionary(x => x.Ref, x => (x.Id, x.Name), StringComparer.Ordinal);
        var list = items.ToList();
        return new
        {
            total = list.Count,
            items = list.Skip(skip).Take(take).Select(s => new
            {
                @ref = s.Ref,
                sourceId = s.SourceId,
                name = s.Name,
                category = s.Category,
                logo = s.Logo,
                epgId = s.EpgId,
                number = s.Number,
                isHls = s.IsHls,
                isDash = s.IsDash,
                health = Health(_health.Get(s.Ref)),
                channelId = assigned.TryGetValue(s.Ref, out var a) ? a.Id : (Guid?)null,
                channelName = assigned.TryGetValue(s.Ref, out var b) ? b.Name : null,
            }),
        };
    }

    [HttpGet("Catalog/Categories")]
    public ActionResult<object> GetCategories([FromQuery] string? sourceId)
    {
        IEnumerable<RawStream> items = sourceId is null ? _catalog.All() : _catalog.ForSource(sourceId);
        return items.GroupBy(s => (s.SourceId, s.Category)).Select(g => new { sourceId = g.Key.SourceId, category = g.Key.Category, count = g.Count() }).OrderBy(x => x.sourceId).ThenBy(x => x.category).ToList();
    }

    // ---- Lineup ------------------------------------------------------------------------------

    [HttpGet("Channels")]
    public ActionResult<object> GetChannels([FromQuery] string? q, [FromQuery] string? category, [FromQuery] bool includeDisabled = true)
    {
        IEnumerable<LogicalChannel> items = _store.Lineup.Channels;
        if (!includeDisabled)
        {
            items = items.Where(c => c.Enabled);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            items = items.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            items = items.Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Number.ToString(CultureInfo.InvariantCulture) == q);
        }

        return items.OrderBy(c => c.Number).Select(ToDto).ToList();
    }

    [HttpGet("Channels/{id}")]
    public ActionResult<object> GetChannel(Guid id)
    {
        var c = _store.GetChannel(id);
        return c is null ? NotFound() : ToDto(c);
    }

    /// <summary>Creates or updates a channel. Any edit pins the channel so automatic matching leaves it alone.</summary>
    [HttpPost("Channels")]
    public async Task<ActionResult<object>> SaveChannel([FromBody] LogicalChannel channel, CancellationToken cancellationToken)
    {
        var existing = _store.GetChannel(channel.Id);
        channel.Pinned = true;
        channel.Auto = existing?.Auto ?? false;
        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(channel.MatchKey))
            {
                channel.MatchKey = NameNormalizer.Parse(channel.Name).Key;
            }

            _store.Lineup.Channels.Add(channel);
        }
        else
        {
            var idx = _store.Lineup.Channels.IndexOf(existing);
            _store.Lineup.Channels[idx] = channel;
        }

        for (var i = 0; i < channel.Streams.Count; i++)
        {
            channel.Streams[i].Priority = i;
        }

        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(channel);
    }

    [HttpDelete("Channels/{id}")]
    public async Task<IActionResult> DeleteChannel(Guid id, CancellationToken cancellationToken)
    {
        _store.Lineup.Channels.RemoveAll(c => c.Id == id);
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Merges channels: all streams move into the first id; the others are deleted.</summary>
    [HttpPost("Channels/Merge")]
    public async Task<ActionResult<object>> Merge([FromBody] MergeRequest body, CancellationToken cancellationToken)
    {
        if (body.Ids.Count < 2)
        {
            return BadRequest("need at least two ids");
        }

        var target = _store.GetChannel(body.Ids[0]);
        if (target is null)
        {
            return NotFound();
        }

        foreach (var id in body.Ids.Skip(1))
        {
            var other = _store.GetChannel(id);
            if (other is null)
            {
                continue;
            }

            foreach (var s in other.Streams.Where(s => target.Streams.All(t => t.Ref != s.Ref)))
            {
                target.Streams.Add(s);
            }

            foreach (var e in other.EpgIds.Where(e => !target.EpgIds.Contains(e, StringComparer.OrdinalIgnoreCase)))
            {
                target.EpgIds.Add(e);
            }

            target.Logo ??= other.Logo;
            _store.Lineup.Channels.Remove(other);
        }

        target.Pinned = true;
        for (var i = 0; i < target.Streams.Count; i++)
        {
            target.Streams[i].Priority = i;
        }

        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(target);
    }

    /// <summary>Moves one stream out of a channel into a brand-new channel.</summary>
    [HttpPost("Channels/{id}/Split")]
    public async Task<ActionResult<object>> Split(Guid id, [FromBody] SplitRequest body, CancellationToken cancellationToken)
    {
        var source = _store.GetChannel(id);
        var sr = source?.Streams.FirstOrDefault(s => s.Ref == body.Ref);
        if (source is null || sr is null)
        {
            return NotFound();
        }

        source.Streams.Remove(sr);
        var raw = _catalog.Find(sr.Ref);
        var parsed = NameNormalizer.Parse(raw?.Name ?? sr.MatchedName ?? "channel");
        var created = new LogicalChannel { Name = parsed.DisplayName, MatchKey = parsed.Key, Category = source.Category, Logo = raw?.Logo, Auto = false, Pinned = true, Streams = [sr] };
        if (raw?.EpgId is not null)
        {
            created.EpgIds.Add(raw.EpgId);
        }

        _store.Lineup.Channels.Add(created);
        source.Pinned = true;
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(created);
    }

    [HttpPost("Channels/Bulk")]
    public async Task<IActionResult> Bulk([FromBody] BulkRequest body, CancellationToken cancellationToken)
    {
        foreach (var id in body.Ids)
        {
            var c = _store.GetChannel(id);
            if (c is null)
            {
                continue;
            }

            if (body.Enabled.HasValue)
            {
                c.Enabled = body.Enabled.Value;
            }

            if (body.Category is not null)
            {
                c.Category = body.Category;
                c.Pinned = true;
            }

            if (body.NameMode.HasValue)
            {
                c.NameMode = body.NameMode.Value;
            }

            if (body.Delete)
            {
                _store.Lineup.Channels.Remove(c);
            }
        }

        if (body.RenumberFrom is > 0)
        {
            var n = body.RenumberFrom.Value;
            var used = new HashSet<int>(_store.Lineup.Channels.Where(c => !body.Ids.Contains(c.Id)).Select(c => c.Number));
            foreach (var id in body.Ids)
            {
                var c = _store.GetChannel(id);
                if (c is null)
                {
                    continue;
                }

                while (used.Contains(n))
                {
                    n++;
                }

                c.Number = n;
                c.Pinned = true;
                used.Add(n++);
            }
        }

        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("Channels/Rebuild")]
    public async Task<ActionResult<object>> Rebuild(CancellationToken cancellationToken)
    {
        var summary = await _builder.RebuildAsync(cancellationToken).ConfigureAwait(false);
        return new { summary };
    }

    /// <summary>Suggests merges: enabled channels whose match keys are equal after a looser normalisation (e.g. "TBS" vs "TBS TELEVISION").</summary>
    [HttpGet("Channels/Suggestions")]
    public ActionResult<object> Suggestions()
    {
        static string Loose(string key) => string.Join(' ', key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("TV" or "TELEVISION" or "CHANNEL" or "KANAL" or "KANAŁ" or "POLSKA" or "POLAND" or "JAPAN" or "PL" or "JP")));
        var groups = _store.Lineup.Channels
            .GroupBy(c => Loose(c.MatchKey), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 1 && g.Count() > 1)
            .Select(g => new { key = g.Key, channels = g.Select(ToDto).ToList() })
            .OrderBy(g => g.key)
            .ToList();
        return groups;
    }

    // ---- Rules -------------------------------------------------------------------------------

    [HttpGet("Rules")]
    public ActionResult<List<CategoryRule>> GetRules() => _store.Lineup.Rules;

    [HttpPost("Rules")]
    public async Task<ActionResult<List<CategoryRule>>> SaveRules([FromBody] List<CategoryRule> rules, CancellationToken cancellationToken)
    {
        _store.Lineup.Rules = rules;
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return rules;
    }

    /// <summary>Lists the bundled rule presets.</summary>
    [HttpGet("Rules/Presets")]
    public ActionResult<List<string>> GetPresets() =>
        typeof(TerebiController).Assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Channels.Presets.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[(n.IndexOf(".Presets.", StringComparison.Ordinal) + 9)..^5])
            .ToList();

    /// <summary>Replaces the rules with a bundled preset (optionally keeping the current ones after it).</summary>
    [HttpPost("Rules/Presets/{name}")]
    public async Task<ActionResult<List<CategoryRule>>> LoadPreset(string name, [FromQuery] bool append = false, CancellationToken cancellationToken = default)
    {
        var resource = typeof(TerebiController).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Channels.Presets." + name + ".json", StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            return NotFound();
        }

        await using var stream = typeof(TerebiController).Assembly.GetManifestResourceStream(resource)!;
        var rules = await System.Text.Json.JsonSerializer.DeserializeAsync<List<CategoryRule>>(stream, Storage.JsonFile.Options, cancellationToken).ConfigureAwait(false) ?? [];
        _store.Lineup.Rules = append ? _store.Lineup.Rules.Concat(rules.Where(r => _store.Lineup.Rules.All(x => x.Id != r.Id))).ToList() : rules;
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return _store.Lineup.Rules;
    }

    /// <summary>Clears every non-pinned channel's category and number so the next rebuild applies the rules from scratch.</summary>
    [HttpPost("Channels/Reflow")]
    public async Task<ActionResult<object>> Reflow([FromQuery] bool includePinned = false, CancellationToken cancellationToken = default)
    {
        var n = 0;
        foreach (var c in _store.Lineup.Channels.Where(c => includePinned || !c.Pinned))
        {
            c.Number = 0;
            if (c.Auto)
            {
                c.Category = string.Empty;
            }

            n++;
        }

        _store.Lineup.CategoryBlocks.Clear();
        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        var summary = await _builder.RebuildAsync(cancellationToken).ConfigureAwait(false);
        return new { reflowed = n, summary };
    }

    // ---- Health ------------------------------------------------------------------------------

    [HttpPost("Health/Run")]
    public IActionResult RunHealth([FromQuery] bool onlyUnknownOrDead = false, [FromQuery] string? sourceId = null)
    {
        Fire("health check", () => _health.RunAsync(null, CancellationToken.None, onlyUnknownOrDead, sourceId));
        return Accepted();
    }

    [HttpPost("Health/Probe")]
    public async Task<ActionResult<StreamHealth>> Probe([FromBody] SplitRequest body, CancellationToken cancellationToken)
    {
        var raw = _catalog.Find(body.Ref);
        var source = raw is null ? null : _store.GetSource(raw.SourceId);
        if (raw is null || source is null)
        {
            return NotFound();
        }

        var h = await _health.ProbeAsync(raw, source, cancellationToken).ConfigureAwait(false);
        await _health.SaveAsync(cancellationToken).ConfigureAwait(false);
        return h;
    }

    // ---- EPG ---------------------------------------------------------------------------------

    [HttpGet("Epg/Urls")]
    public ActionResult<List<string>> GetEpgUrls() => _epg.GlobalUrls;

    [HttpPost("Epg/Urls")]
    public ActionResult<List<string>> SaveEpgUrls([FromBody] List<string> urls)
    {
        _epg.GlobalUrls = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList();
        return _epg.GlobalUrls;
    }

    [HttpPost("Epg/Refresh")]
    public IActionResult RefreshEpg()
    {
        Fire("EPG refresh", () => _epg.RefreshAsync(null, CancellationToken.None));
        return Accepted();
    }

    [HttpGet("Epg/Channels")]
    public ActionResult<object> EpgChannels([FromQuery] string? q)
    {
        var known = _epg.KnownChannels();
        IEnumerable<KeyValuePair<string, string>> items = known;
        if (!string.IsNullOrWhiteSpace(q))
        {
            items = items.Where(kv => kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return items.Take(300).Select(kv => new { id = kv.Key, name = kv.Value, hasPrograms = _epg.HasPrograms(kv.Key) }).ToList();
    }

    /// <summary>Suggests XMLTV ids for channels without EPG data by comparing normalised names.</summary>
    [HttpGet("Epg/Suggestions")]
    public ActionResult<object> EpgSuggestions()
    {
        var known = _epg.KnownChannels().Select(kv => (Id: kv.Key, Name: kv.Value, Key: NameNormalizer.Parse(kv.Value).Key, IdKey: NameNormalizer.Parse(kv.Key.Split('@')[0].Replace('.', ' ')).Key)).ToList();
        var result = new List<object>();
        foreach (var c in _store.Lineup.Channels.Where(c => c.Enabled && !c.EpgIds.Any(_epg.HasPrograms)))
        {
            var key = c.MatchKey;
            var hits = known.Where(k => k.Key == key || k.IdKey == key).Select(k => new { id = k.Id, name = k.Name, hasPrograms = _epg.HasPrograms(k.Id) }).Take(5).ToList();
            if (hits.Count > 0)
            {
                result.Add(new { channelId = c.Id, channel = c.Name, hits });
            }
        }

        return result;
    }

    /// <summary>Asks Jellyfin to refresh its guide (channels + programs) now.</summary>
    [HttpPost("Guide/Refresh")]
    public IActionResult RefreshGuide()
    {
        _guide.Request("requested from the settings page");
        return Accepted();
    }

    // ---- Export / import ---------------------------------------------------------------------

    /// <summary>Everything needed to recreate this setup elsewhere. Passwords are included only when requested.</summary>
    [HttpGet("Export")]
    public ActionResult<object> Export([FromQuery] bool includeSecrets = false)
    {
        var sources = _store.Sources.Select(s => includeSecrets ? s : Redact(s)).ToList();
        return new
        {
            format = "terebi/1",
            exportedUtc = DateTimeOffset.UtcNow,
            sources,
            lineup = _store.Lineup,
            epgUrls = _epg.GlobalUrls,
        };
    }

    /// <summary>Restores an export. Existing data is replaced; redacted passwords keep the current value when the source id matches.</summary>
    [HttpPost("Import")]
    public async Task<ActionResult<object>> Import([FromBody] ImportDto body, CancellationToken cancellationToken)
    {
        if (body.Sources is not null)
        {
            foreach (var s in body.Sources)
            {
                if (s.Password == "********")
                {
                    s.Password = _store.GetSource(s.Id)?.Password;
                }
            }

            _store.Sources.Clear();
            _store.Sources.AddRange(body.Sources);
            await _store.SaveSourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (body.Lineup is not null)
        {
            _store.Lineup.Channels = body.Lineup.Channels;
            _store.Lineup.Rules = body.Lineup.Rules;
            _store.Lineup.CategoryBlocks = body.Lineup.CategoryBlocks;
            await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        }

        if (body.EpgUrls is not null)
        {
            _epg.GlobalUrls = body.EpgUrls;
        }

        return new { sources = _store.Sources.Count, channels = _store.Lineup.Channels.Count, rules = _store.Lineup.Rules.Count };
    }

    // ---- Import from the Jellyfin.Xtream plugin ------------------------------------------------

    /// <summary>
    /// Reads the Xtream plugin's configuration on this server: creates a source with its credentials and
    /// remembers the channel numbers it had assigned, so the first lineup keeps the user's numbering.
    /// </summary>
    [HttpPost("Import/XtreamPlugin")]
    public async Task<ActionResult<object>> ImportXtreamPlugin(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Xtream.xml");
        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { message = "Jellyfin.Xtream.xml not found in " + _paths.PluginConfigurationsPath });
        }

        var doc = XDocument.Load(path);
        var root = doc.Root!;
        var baseUrl = root.Element("BaseUrl")?.Value?.Trim();
        var user = root.Element("Username")?.Value?.Trim();
        var pass = root.Element("Password")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(user))
        {
            return BadRequest(new { message = "Xtream plugin has no credentials configured" });
        }

        var source = _store.Sources.FirstOrDefault(s => s.Type == SourceType.Xtream && string.Equals(s.Url.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && s.Username == user);
        if (source is null)
        {
            source = new SourceDefinition { Name = new Uri(baseUrl).Host, Type = SourceType.Xtream, Url = baseUrl, Username = user, Password = pass, Priority = 10 };
            var ua = root.Element("UserAgent")?.Value;
            if (!string.IsNullOrWhiteSpace(ua))
            {
                source.UserAgent = ua;
            }

            _store.Sources.Add(source);
            await _store.SaveSourcesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Selected categories → include filter (only when the user had not picked "everything").
        var selectedCategoryIds = root.Element("LiveTv")?.Elements("Item").Select(i => i.Element("Key")?.Element("int")?.Value).Where(v => v is not null).ToList() ?? [];
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in root.Element("LiveTvOverrides")?.Elements("Item") ?? [])
        {
            var sid = item.Element("Key")?.Element("int")?.Value;
            var num = item.Element("Value")?.Element("ChannelOverrides")?.Element("Number")?.Value;
            if (sid is not null && int.TryParse(num, out var n))
            {
                numbers[sid] = n;
            }
        }

        var imported = ImportedNumbers;
        imported[source.Id] = numbers;
        await Storage.JsonFile.SaveAsync(ImportedNumbersPath, imported, cancellationToken).ConfigureAwait(false);
        return new { sourceId = source.Id, categoriesSelected = selectedCategoryIds.Count, numbersRemembered = numbers.Count, message = "Source created. Run 'Refresh sources', then 'Apply imported numbers'." };
    }

    /// <summary>Applies remembered Xtream plugin numbers to channels whose streams carry those stream ids.</summary>
    [HttpPost("Import/ApplyNumbers")]
    public async Task<ActionResult<object>> ApplyImportedNumbers(CancellationToken cancellationToken)
    {
        var imported = ImportedNumbers;
        var applied = 0;
        var used = new HashSet<int>();
        foreach (var c in _store.Lineup.Channels)
        {
            foreach (var sr in c.Streams)
            {
                if (imported.TryGetValue(sr.SourceId, out var map) && map.TryGetValue(sr.Ref.Split(':', 2)[1], out var n) && n > 0 && !used.Contains(n))
                {
                    c.Number = n;
                    c.Pinned = true;
                    used.Add(n);
                    applied++;
                    break;
                }
            }
        }

        await _store.SaveLineupAsync(cancellationToken).ConfigureAwait(false);
        return new { applied };
    }

    private string ImportedNumbersPath => _terebiPaths.File("imported-numbers.json");

    private Dictionary<string, Dictionary<string, int>> ImportedNumbers => Storage.JsonFile.Load(ImportedNumbersPath, () => new Dictionary<string, Dictionary<string, int>>(), _logger);

    // ---- helpers -----------------------------------------------------------------------------

    private object ToDto(LogicalChannel c) => new
    {
        id = c.Id,
        name = c.Name,
        nameMode = c.NameMode.ToString(),
        matchKey = c.MatchKey,
        number = c.Number,
        category = c.Category,
        tags = c.Tags,
        logo = c.Logo,
        logoOrigin = c.LogoOrigin,
        epgIds = c.EpgIds,
        enabled = c.Enabled,
        auto = c.Auto,
        pinned = c.Pinned,
        region = c.Region,
        createdUtc = c.CreatedUtc,
        hasEpg = c.EpgIds.Any(_epg.HasPrograms),
        streams = c.Streams.Select(s =>
        {
            var raw = _catalog.Find(s.Ref);
            var h = _health.Get(s.Ref);
            return new
            {
                @ref = s.Ref,
                sourceId = s.SourceId,
                sourceName = _store.GetSource(s.SourceId)?.Name,
                priority = s.Priority,
                enabled = s.Enabled,
                variant = s.Variant,
                name = raw?.Name ?? s.MatchedName,
                category = raw?.Category ?? s.MatchedCategory,
                missing = raw is null,
                isHls = raw?.IsHls ?? false,
                health = new { dead = h.IsDead, unknown = h.Unknown, consecutiveFailures = h.ConsecutiveFailures, lastOkUtc = h.LastOkUtc, lastError = h.LastError, latencyMs = h.LatencyMs, kind = h.Kind },
            };
        }),
    };

    private static object Health(StreamHealth h) => new { dead = h.IsDead, unknown = h.Unknown, consecutiveFailures = h.ConsecutiveFailures, lastOkUtc = h.LastOkUtc, lastError = h.LastError, latencyMs = h.LatencyMs, kind = h.Kind };

    private static SourceDefinition Redact(SourceDefinition s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Type = s.Type,
        Enabled = s.Enabled,
        Priority = s.Priority,
        Url = s.Url,
        Username = s.Username,
        Password = string.IsNullOrEmpty(s.Password) ? null : "********",
        MaxConnections = s.MaxConnections,
        XtreamOutput = s.XtreamOutput,
        IncludeCategories = s.IncludeCategories,
        ExcludeCategories = s.ExcludeCategories,
        CategoryLabel = s.CategoryLabel,
        EpgUrls = s.EpgUrls,
        AutoAddChannels = s.AutoAddChannels,
        UserAgent = s.UserAgent,
        LastRefreshUtc = s.LastRefreshUtc,
        LastError = s.LastError,
        StreamCount = s.StreamCount,
    };

    public sealed class ImportDto
    {
        [JsonPropertyName("sources")]
        public List<SourceDefinition>? Sources { get; set; }

        [JsonPropertyName("lineup")]
        public Lineup? Lineup { get; set; }

        [JsonPropertyName("epgUrls")]
        public List<string>? EpgUrls { get; set; }
    }

    public sealed class MergeRequest
    {
        [JsonPropertyName("ids")]
        public List<Guid> Ids { get; set; } = [];
    }

    public sealed class SplitRequest
    {
        [JsonPropertyName("ref")]
        public string Ref { get; set; } = string.Empty;
    }

    public sealed class BulkRequest
    {
        [JsonPropertyName("ids")]
        public List<Guid> Ids { get; set; } = [];

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("nameMode")]
        public NameMode? NameMode { get; set; }

        [JsonPropertyName("renumberFrom")]
        public int? RenumberFrom { get; set; }

        [JsonPropertyName("delete")]
        public bool Delete { get; set; }
    }
}
