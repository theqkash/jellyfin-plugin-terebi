using System.Globalization;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Epg;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.Logos;
using Jellyfin.Plugin.Terebi.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>
/// The Live TV provider Jellyfin talks to. Channels are Terebi's logical channels; programs come from the
/// EPG service; streams from the stream manager. Timers are not supported (no DVR).
/// </summary>
public sealed class TerebiLiveTvService : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly LineupStore _store;
    private readonly HealthChecker _health;
    private readonly EpgService _epg;
    private readonly StreamManager _streams;
    private readonly LogoCache _logos;
    private readonly ILogger<TerebiLiveTvService> _logger;

    public TerebiLiveTvService(LineupStore store, HealthChecker health, EpgService epg, StreamManager streams, LogoCache logos, ILogger<TerebiLiveTvService> logger)
    {
        _store = store;
        _health = health;
        _epg = epg;
        _streams = streams;
        _logos = logos;
        _logger = logger;
    }

    public string Name => "Terebi";

    public string HomePageUrl => string.Empty;

    public static string ChannelIdOf(LogicalChannel c) => c.Id.ToString("N");

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var hideDead = Plugin.Instance?.Configuration.HideDeadChannels ?? true;
        var list = new List<ChannelInfo>();
        foreach (var c in _store.Lineup.Channels.Where(c => c.Enabled && c.Streams.Any(s => s.Enabled)))
        {
            var usable = c.Streams.Where(s => s.Enabled).Select(s => _health.Get(s.Ref)).ToList();
            var allDead = usable.Count > 0 && usable.All(h => h.IsDead);
            if (hideDead && allDead)
            {
                continue;
            }

            var tags = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.Category))
            {
                tags.Add(c.Category);
            }

            tags.AddRange(c.Tags);
            if (c.Streams.Count > 1)
            {
                tags.Add(c.Streams.Count.ToString(CultureInfo.InvariantCulture) + " sources");
            }

            list.Add(new ChannelInfo
            {
                Id = ChannelIdOf(c),
                Name = c.Name,
                Number = c.Number > 0 ? c.Number.ToString(CultureInfo.InvariantCulture) : null,
                // Only cached logos are handed over: a local file Jellyfin reads directly, plus the local URL
                // it compares against to notice a changed logo. Provider hosts are never contacted by Jellyfin.
                // A channel without a usable logo gets the placeholder, so a stale remote path never survives in Jellyfin.
                ImagePath = _logos.LocalPathOrPlaceholder(c.Logo),
                ImageUrl = _logos.LocalUrlOrPlaceholder(c.Logo),
                HasImage = true,
                ChannelType = ChannelType.TV,
                ChannelGroup = c.Category,
                Tags = tags.ToArray(),
                TunerHostId = FailoverLiveStream.TunerHostId,
            });
        }

        _logger.LogInformation("Terebi: serving {Count} channels to Jellyfin", list.Count);
        return Task.FromResult<IEnumerable<ChannelInfo>>(list);
    }

    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(channelId, "N", out var id))
        {
            return [];
        }

        var channel = _store.GetChannel(id);
        if (channel is null)
        {
            return [];
        }

        var programs = await _epg.GetProgramsAsync(channel, new DateTimeOffset(startDateUtc, TimeSpan.Zero), new DateTimeOffset(endDateUtc, TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
        // Genre flags come from the channel's category: they drive Jellyfin's guide filters (News, Sports, Kids, Movies)
        // and the Live TV home sections, which otherwise stay empty for IPTV.
        var cat = (channel.Category ?? string.Empty).ToUpperInvariant();
        var isNews = cat.Contains("NEWS", StringComparison.Ordinal) || cat.Contains("INFORMACYJNE", StringComparison.Ordinal);
        var isSports = cat.Contains("SPORT", StringComparison.Ordinal);
        var isKids = cat.Contains("KIDS", StringComparison.Ordinal) || cat.Contains("DZIECI", StringComparison.Ordinal);
        var isMovieChannel = cat.Contains("FILM", StringComparison.Ordinal) || cat.Contains("MOVIE", StringComparison.Ordinal) || cat.Contains("KINO", StringComparison.Ordinal) || cat.Contains("CINEMA", StringComparison.Ordinal);
        return programs.Select(p => new ProgramInfo
        {
            Id = channelId + "_" + p.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ChannelId = channelId,
            Name = p.Title,
            EpisodeTitle = p.SubTitle,
            Overview = p.Description,
            StartDate = p.Start.UtcDateTime,
            EndDate = p.End.UtcDateTime,
            Genres = string.IsNullOrWhiteSpace(p.Category) ? [] : [p.Category],
            ImageUrl = p.Icon,
            SeasonNumber = p.Season,
            EpisodeNumber = p.Episode,
            IsSeries = p.Episode.HasValue,
            IsNews = isNews,
            IsSports = isSports,
            IsKids = isKids,
            IsMovie = isMovieChannel && !p.Episode.HasValue,
            IsLive = true,
        }).ToList();
    }

    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(string channelId, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(channelId, "N", out var id) || _store.GetChannel(id) is not { } channel)
        {
            throw new ArgumentException("Unknown Terebi channel " + channelId);
        }

        return await _streams.OpenAsync(channel, currentLiveStreams, cancellationToken).ConfigureAwait(false);
    }

    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Terebi streams are opened through the direct stream provider.");

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(channelId, "N", out var id) || _store.GetChannel(id) is not { } channel)
        {
            return Task.FromResult(new List<MediaSourceInfo>());
        }

        // One media source per channel; the actual URL is decided when the stream is opened.
        return Task.FromResult(new List<MediaSourceInfo>
        {
            new()
            {
                Id = channel.Id.ToString("N"),
                Name = channel.Name,
                Path = "terebi://" + channel.Id.ToString("N"),
                Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
                Container = "ts",
                IsInfiniteStream = true,
                IsRemote = true,
                RequiresOpening = true,
                RequiresClosing = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
                MediaStreams = [],
            },
        });
    }

    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => _streams.CloseAsync(id);

    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    // ---- DVR: not supported ------------------------------------------------------------------

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => throw new NotSupportedException("Terebi does not record.");

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw new NotSupportedException("Terebi does not record.");

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<TimerInfo>>([]);

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null) => Task.FromResult(new SeriesTimerInfo());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SeriesTimerInfo>>([]);

    public Task<string> CreateTimer(TimerInfo info, CancellationToken cancellationToken) => throw new NotSupportedException("Terebi does not record.");

    public Task<string> CreateSeriesTimer(SeriesTimerInfo info, CancellationToken cancellationToken) => throw new NotSupportedException("Terebi does not record.");
}
