using Jellyfin.Plugin.Terebi.Logos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Terebi.Api;

/// <summary>
/// Serves cached channel logos. Anonymous because Jellyfin fetches channel folder images itself without a
/// user session; file names are content hashes, nothing else under the data folder is reachable.
/// </summary>
[ApiController]
[Route("Terebi/Logo")]
public sealed class LogoController : ControllerBase
{
    private readonly LogoCache _logos;

    public LogoController(LogoCache logos)
    {
        _logos = logos;
    }

    [HttpGet("{fileName}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Any)]
    public IActionResult Get(string fileName)
    {
        if (!_logos.TryResolve(fileName, out var path, out var contentType))
        {
            return NotFound();
        }

        return PhysicalFile(path, contentType);
    }
}
