using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Terebi.Api;

/// <summary>
/// Endpoints the web client script uses: readable by any signed-in user, plus the script itself.
/// </summary>
[ApiController]
[Route("Terebi")]
public sealed class PublicController : ControllerBase
{
    private readonly LineupStore _store;
    private readonly HealthChecker _health;

    public PublicController(LineupStore store, HealthChecker health)
    {
        _store = store;
        _health = health;
    }

    /// <summary>Categories with the channel numbers they contain, in block order.</summary>
    [HttpGet("Public/Categories")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> Categories()
    {
        var hideDead = Plugin.Instance?.Configuration.HideDeadChannels ?? true;
        var channels = _store.Lineup.Channels
            .Where(c => c.Enabled && c.Number > 0 && c.Streams.Any(s => s.Enabled))
            .Where(c => !hideDead || !c.Streams.Where(s => s.Enabled).All(s => _health.Get(s.Ref).IsDead));
        var groups = channels
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "Other" : c.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(c => c.Number))
            .Select(g => new { name = g.Key, numbers = g.Select(c => c.Number).OrderBy(n => n).ToList() })
            .ToList();
        return new { categories = groups, updatedUtc = _store.Lineup.UpdatedUtc };
    }

    [HttpGet("Client/{fileName}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600)]
    public IActionResult ClientFile(string fileName)
    {
        if (fileName.Contains("..", StringComparison.Ordinal) || fileName.Contains('/', StringComparison.Ordinal))
        {
            return BadRequest();
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Terebi.Web.Client." + fileName);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, fileName.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css" : "application/javascript");
    }
}
