using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Streaming;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Api;

/// <summary>
/// Raw transport-stream output of a logical channel, with failover, for the channel folder tree and for
/// external players. Protected by the per-install stream key instead of a user session because Jellyfin's
/// own ffmpeg fetches it without credentials.
/// </summary>
[ApiController]
[Route("Terebi/Stream")]
public sealed class StreamController : ControllerBase
{
    private readonly LineupStore _store;
    private readonly StreamManager _streams;
    private readonly ILogger<StreamController> _logger;

    public StreamController(LineupStore store, StreamManager streams, ILogger<StreamController> logger)
    {
        _store = store;
        _streams = streams;
        _logger = logger;
    }

    [HttpGet("{channelId}.ts")]
    [AllowAnonymous]
    public async Task Get(string channelId, [FromQuery] string? key, CancellationToken cancellationToken)
    {
        var expected = Plugin.Instance?.Configuration.StreamKey;
        if (string.IsNullOrEmpty(expected) || !string.Equals(key, expected, StringComparison.Ordinal))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!Guid.TryParseExact(channelId, "N", out var id) || _store.GetChannel(id) is not { } channel)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Jellyfin's own ffmpeg comes from the loopback address; anything else is an external player without a session.
        var external = HttpContext.Connection.RemoteIpAddress is { } ip && !System.Net.IPAddress.IsLoopback(ip);
        ILiveStream live;
        try
        {
            live = await _streams.OpenSharedAsync(channel, cancellationToken, external).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Terebi: no stream for {Channel}", channel.Name);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Terebi-Channel"] = channel.Name;
        try
        {
            await using var input = live.GetStream();
            var buffer = new byte[188 * 350];
            int n;
            while (!cancellationToken.IsCancellationRequested && (n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // client went away
        }
        catch (IOException)
        {
            // client went away
        }
        finally
        {
            await _streams.CloseAsync(live.UniqueId, external).ConfigureAwait(false);
        }
    }
}
