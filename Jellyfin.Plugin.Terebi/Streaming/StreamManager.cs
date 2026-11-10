using System.Collections.Concurrent;
using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.Sources;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Streaming;

/// <summary>
/// Opens one <see cref="FailoverLiveStream"/> per channel and shares it between viewers. Knows which
/// sources are busy so the health checker and single-connection accounts are respected.
/// </summary>
public sealed class StreamManager
{
    private readonly LineupStore _store;
    private readonly CatalogStore _catalog;
    private readonly HealthChecker _health;
    private readonly IServerApplicationHost _appHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamManager> _logger;
    private readonly ConcurrentDictionary<Guid, FailoverLiveStream> _open = new();

    public StreamManager(LineupStore store, CatalogStore catalog, HealthChecker health, IServerApplicationHost appHost, IHttpClientFactory httpClientFactory, ILogger<StreamManager> logger)
    {
        _store = store;
        _catalog = catalog;
        _health = health;
        _appHost = appHost;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _health.IsSourceBusy = sourceId => _open.Values.Any(s => s.ActiveSourceId == sourceId);
    }

    public IReadOnlyCollection<FailoverLiveStream> Open => _open.Values.ToList();

    /// <summary>Streams of a channel in the order they should be tried: enabled, known-good first, unknown next, dead last.</summary>
    public List<(RawStream Stream, SourceDefinition Source)> Candidates(LogicalChannel channel)
    {
        var busySingle = new HashSet<string>(_open.Values.Select(s => s.ActiveSourceId).Where(id => id is not null)!, StringComparer.Ordinal);
        var list = new List<(RawStream Stream, SourceDefinition Source, int Rank)>();
        foreach (var sr in channel.Streams.Where(s => s.Enabled))
        {
            var raw = _catalog.Find(sr.Ref);
            var source = _store.GetSource(sr.SourceId);
            if (raw is null || source is null || !source.Enabled || raw.IsDash)
            {
                continue;
            }

            var h = _health.Get(sr.Ref);
            var rank = h.IsDead ? 2 : h.Unknown ? 1 : 0;
            if (source.MaxConnections == 1 && busySingle.Contains(source.Id))
            {
                rank += 3; // the account's only slot is taken by another channel
            }

            list.Add((raw, source, rank * 1000 + sr.Priority));
        }

        return list.OrderBy(x => x.Rank).Select(x => (x.Stream, x.Source)).ToList();
    }

    public async Task<ILiveStream> OpenAsync(LogicalChannel channel, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (_open.TryGetValue(channel.Id, out var existing) && (currentLiveStreams.Contains(existing) || existing.ConsumerCount <= 0))
        {
            // Either Jellyfin already shares it, or it is lingering after its last viewer left: reuse it.
            existing.ConsumerCount = Math.Max(0, existing.ConsumerCount) + 1;
            _logger.LogInformation("Terebi: sharing stream of {Channel} ({Consumers} viewers)", channel.Name, existing.ConsumerCount);
            return existing;
        }

        var candidates = Candidates(channel);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("Terebi: channel has no usable streams: " + channel.Name);
        }

        // One-slot accounts cannot afford an idle connection when another channel from them is wanted.
        var singleSlotSources = candidates.Where(c => c.Source.MaxConnections == 1).Select(c => c.Source.Id).ToHashSet(StringComparer.Ordinal);
        if (singleSlotSources.Count > 0)
        {
            await EvictIdleAsync(s => s.ActiveSourceId is { } id && singleSlotSources.Contains(id)).ConfigureAwait(false);
        }

        if (_open.TryRemove(channel.Id, out var stale))
        {
            await CloseNowAsync(stale).ConfigureAwait(false);
        }

        var stream = new FailoverLiveStream(channel.Id, channel.Name, candidates, _appHost, _httpClientFactory, _health, _logger);
        _open[channel.Id] = stream;
        try
        {
            await stream.Open(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _open.TryRemove(channel.Id, out _);
            stream.Dispose();
            throw;
        }

        stream.ConsumerCount = 1;
        _logger.LogInformation("Terebi: opened {Channel} from source '{Source}' ({Ref})", channel.Name, stream.ActiveSourceId, stream.ActiveStreamRef);
        return stream;
    }

    /// <summary>Opens (or joins) the channel's stream without Jellyfin's live-stream bookkeeping (folder tree, raw endpoint).</summary>
    public async Task<ILiveStream> OpenSharedAsync(LogicalChannel channel, CancellationToken cancellationToken, bool external = false)
    {
        FailoverLiveStream stream;
        if (_open.TryGetValue(channel.Id, out var existing))
        {
            existing.ConsumerCount = Math.Max(0, existing.ConsumerCount) + 1;
            stream = existing;
        }
        else
        {
            stream = (FailoverLiveStream)await OpenAsync(channel, [], cancellationToken).ConfigureAwait(false);
        }

        if (external)
        {
            stream.ExternalConsumers++;
        }

        return stream;
    }

    /// <summary>Closes a stream regardless of consumers (watchdog). Jellyfin's later close for it is a no-op.</summary>
    public async Task ForceCloseAsync(FailoverLiveStream stream, string reason)
    {
        var entry = _open.FirstOrDefault(kv => ReferenceEquals(kv.Value, stream));
        if (entry.Value is null || !_open.TryRemove(entry.Key, out _))
        {
            return;
        }

        _logger.LogWarning("Terebi: watchdog closing {Channel} ({Consumers} consumers): {Reason}", stream.ChannelName, stream.ConsumerCount, reason);
        stream.ConsumerCount = 0;
        await CloseNowAsync(stream).ConfigureAwait(false);
    }

    /// <summary>
    /// A viewer left. The upstream connection is kept for a short linger so a probe followed by playback, or a
    /// player restart, does not reconnect to the provider; it is closed when nobody came back.
    /// </summary>
    public async Task CloseAsync(string uniqueId, bool external = false)
    {
        var entry = _open.FirstOrDefault(kv => kv.Value.UniqueId == uniqueId);
        if (entry.Value is null)
        {
            return;
        }

        entry.Value.ConsumerCount--;
        if (external && entry.Value.ExternalConsumers > 0)
        {
            entry.Value.ExternalConsumers--;
        }

        if (entry.Value.ConsumerCount > 0)
        {
            return;
        }

        var linger = TimeSpan.FromSeconds(Math.Clamp(Plugin.Instance?.Configuration.StreamLingerSeconds ?? 10, 0, 120));
        if (linger == TimeSpan.Zero)
        {
            if (_open.TryRemove(entry.Key, out var now))
            {
                await CloseNowAsync(now).ConfigureAwait(false);
            }

            return;
        }

        var stream = entry.Value;
        var channelId = entry.Key;
        _ = Task.Run(async () =>
        {
            await Task.Delay(linger).ConfigureAwait(false);
            if (stream.ConsumerCount <= 0 && _open.TryGetValue(channelId, out var current) && ReferenceEquals(current, stream) && _open.TryRemove(channelId, out _))
            {
                await CloseNowAsync(stream).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Closes lingering (viewer-less) streams matching the predicate right away.</summary>
    public async Task EvictIdleAsync(Func<FailoverLiveStream, bool> predicate)
    {
        foreach (var kv in _open.ToArray())
        {
            if (kv.Value.ConsumerCount <= 0 && predicate(kv.Value) && _open.TryRemove(kv.Key, out var s))
            {
                _logger.LogInformation("Terebi: closing idle stream of {Channel} to free its source", s.ChannelName);
                await CloseNowAsync(s).ConfigureAwait(false);
            }
        }
    }

    private static async Task CloseNowAsync(FailoverLiveStream stream)
    {
        await stream.Close().ConfigureAwait(false);
        stream.Dispose();
    }
}
