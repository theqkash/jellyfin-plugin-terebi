using System.Text.RegularExpressions;
using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Epg;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.LiveTv;
using Jellyfin.Plugin.Terebi.Logos;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Sources;

/// <summary>
/// Downloads every enabled source into the catalog and then lets the lineup builder re-match channels.
/// </summary>
public sealed class SourceRefresher
{
    private readonly LineupStore _store;
    private readonly CatalogStore _catalog;
    private readonly XtreamClient _xtream;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LineupBuilder _builder;
    private readonly ChannelNameSync _nameSync;
    private readonly HealthChecker _health;
    private readonly EpgService _epg;
    private readonly GuideRefreshQueue _guide;
    private readonly LogoIndex _logoIndex;
    private readonly LogoCache _logos;
    private readonly ILogger<SourceRefresher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SourceRefresher(LineupStore store, CatalogStore catalog, XtreamClient xtream, IHttpClientFactory httpClientFactory, LineupBuilder builder, ChannelNameSync nameSync, HealthChecker health, EpgService epg, GuideRefreshQueue guide, LogoIndex logoIndex, LogoCache logos, ILogger<SourceRefresher> logger)
    {
        _nameSync = nameSync;
        _health = health;
        _epg = epg;
        _guide = guide;
        _logoIndex = logoIndex;
        _logos = logos;
        _store = store;
        _catalog = catalog;
        _xtream = xtream;
        _httpClientFactory = httpClientFactory;
        _builder = builder;
        _logger = logger;
    }

    public event EventHandler? LineupChanged;

    /// <summary>Remote logo URLs of every enabled channel.</summary>
    public List<string?> LogoUrls() => _store.Lineup.Channels.Where(c => c.Enabled).Select(c => c.Logo).ToList();

    /// <summary>Caches missing channel logos (and prunes unreferenced ones); returns how many were newly cached.</summary>
    public async Task<int> WarmLogosAsync(TimeSpan budget, CancellationToken cancellationToken, bool retryFailed = false)
    {
        try
        {
            var urls = LogoUrls();
            var added = await _logos.WarmAsync(urls, budget, retryFailed, cancellationToken).ConfigureAwait(false);
            await _logos.PruneAsync(urls, cancellationToken).ConfigureAwait(false);
            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Terebi: logo caching failed");
            return 0;
        }
    }

    public async Task RefreshAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Terebi: source refresh already running");
            return;
        }

        try
        {
            var before = _store.Lineup.Channels.Select(c => c.Id).ToHashSet();
            var knownEpgIds = _store.Lineup.Channels.SelectMany(c => c.EpgIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources = _store.Sources.Where(s => s.Enabled).ToList();
            for (var i = 0; i < sources.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshSourceAsync(sources[i], cancellationToken).ConfigureAwait(false);
                progress?.Report(80.0 * (i + 1) / Math.Max(1, sources.Count));
            }

            await _store.SaveSourcesAsync(cancellationToken).ConfigureAwait(false);
            await _logoIndex.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            string changes;
            try
            {
                changes = await _builder.RebuildAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Terebi: lineup rebuild failed; the previous lineup stays in place");
                throw;
            }

            _logger.LogInformation("Terebi: lineup rebuilt: {Summary}", changes);
            progress?.Report(85);

            // Logos first, with a time budget: Jellyfin only ever sees cached files, never provider hosts.
            var newLogos = await WarmLogosAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            progress?.Report(95);
            try
            {
                await _nameSync.SyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Terebi: pushing channel names to Jellyfin failed");
            }

            LineupChanged?.Invoke(this, EventArgs.Empty);

            // Follow-ups, all best effort: probe streams we have never seen, fetch EPG for new ids, and let
            // Jellyfin pick up added or removed channels without waiting for its daily guide refresh.
            var after = _store.Lineup.Channels.Select(c => c.Id).ToHashSet();
            var newEpgIds = _store.Lineup.Channels.SelectMany(c => c.EpgIds).Any(id => !knownEpgIds.Contains(id) && !_epg.HasPrograms(id));
            _ = Task.Run(async () =>
            {
                try
                {
                    await _health.RunAsync(null, CancellationToken.None, onlyUnknownOrDead: true).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Terebi: post-refresh health probe failed");
                }
            });
            if (newEpgIds)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _epg.RefreshAsync(null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Terebi: post-refresh EPG download failed");
                    }
                });
            }

            var autoGuide = Plugin.Instance?.Configuration.AutoRefreshGuide ?? true;
            if (autoGuide && !before.SetEquals(after))
            {
                _guide.Request($"{after.Except(before).Count()} channels added, {before.Except(after).Count()} removed");
            }
            else if (autoGuide && newLogos > 0)
            {
                _guide.Request($"{newLogos} new logos");
            }

            if (_logos.Stats(LogoUrls()).Pending > 0)
            {
                // Slow hosts get the rest of the time in the background; one more guide pass when they arrive.
                _ = Task.Run(async () =>
                {
                    var more = await WarmLogosAsync(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
                    if (more > 0 && autoGuide)
                    {
                        _guide.Request($"{more} late logos");
                    }
                });
            }

            progress?.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RefreshSourceAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        try
        {
            List<RawStream> streams = source.Type switch
            {
                SourceType.Xtream => await _xtream.GetLiveStreamsAsync(source, cancellationToken).ConfigureAwait(false),
                SourceType.M3u => ParseM3u(source, await DownloadTextAsync(source, cancellationToken).ConfigureAwait(false)),
                _ => [],
            };

            streams = ApplyCategoryFilters(source, streams);
            await _catalog.ReplaceSourceAsync(source.Id, streams, cancellationToken).ConfigureAwait(false);
            source.LastRefreshUtc = DateTimeOffset.UtcNow;
            source.LastError = null;
            source.StreamCount = streams.Count;
            _logger.LogInformation("Terebi: source '{Name}' refreshed, {Count} streams", source.Name, streams.Count);
            return streams.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
        {
            source.LastError = ex.Message;
            _logger.LogWarning(ex, "Terebi: source '{Name}' failed to refresh; keeping the previous catalog", source.Name);
            return -1;
        }
    }

    private static List<RawStream> ApplyCategoryFilters(SourceDefinition source, List<RawStream> streams)
    {
        Regex? include = Compile(source.IncludeCategories);
        Regex? exclude = Compile(source.ExcludeCategories);
        if (include is null && exclude is null)
        {
            return streams;
        }

        return streams.Where(s => (include is null || include.IsMatch(s.Category)) && (exclude is null || !exclude.IsMatch(s.Category))).ToList();
    }

    internal static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static List<RawStream> ParseM3u(SourceDefinition source, string content)
    {
        source.DiscoveredEpgUrls = M3uReader.TvgUrls(content);
        return M3uReader.Parse(content, source.Id);
    }

    private async Task<string> DownloadTextAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        if (!source.Url.Contains("://", StringComparison.Ordinal))
        {
            return await File.ReadAllTextAsync(source.Url, cancellationToken).ConfigureAwait(false);
        }

        var http = _httpClientFactory.CreateClient("Terebi");
        http.Timeout = TimeSpan.FromSeconds(120);
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        request.Headers.UserAgent.ParseAdd(source.UserAgent ?? Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
