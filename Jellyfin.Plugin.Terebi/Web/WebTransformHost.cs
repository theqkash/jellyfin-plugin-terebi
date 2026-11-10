using System.Reflection;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Web;

/// <summary>
/// Hooks a script tag into jellyfin-web's index.html through the File Transformation plugin (when installed)
/// so the Live TV pages get a Terebi category filter. Without File Transformation nothing happens.
/// </summary>
public sealed class WebTransformHost : IHostedService
{
    private static readonly Guid TransformationId = Guid.Parse("2f6b9a1c-4d7e-4a3b-9c1d-8e5f6a7b0c21");
    /// <summary>Changes with every build so browsers do not keep an older copy of the script.</summary>
    private static readonly string ScriptVersion = (Plugin.Instance?.Version.ToString() ?? "0") + "." + File.GetLastWriteTimeUtc(typeof(WebTransformHost).Assembly.Location).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)[^8..];

    private const string Marker = "data-terebi";
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebTransformHost> _logger;
    private bool _registered;

    public WebTransformHost(IServiceProvider serviceProvider, ILogger<WebTransformHost> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public static bool IsActive { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableWebCategoryFilter != true)
        {
            return Task.CompletedTask;
        }

        var handler = typeof(WebTransformHost).GetMethod(nameof(TransformAsync), BindingFlags.Public | BindingFlags.Static)!;
        // Registered under the exact "index.html" key: File Transformation runs one pipeline per file and other
        // plugins share this key. The pattern also matches "*-index-html.*.chunk.js"; those have no body tag
        // and are left untouched.
        _registered = FileTransformationBridge.Register(TransformationId, "index.html", handler, _serviceProvider, _logger);
        IsActive = _registered;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            FileTransformationBridge.Unregister(TransformationId, _serviceProvider, _logger);
            IsActive = false;
        }

        return Task.CompletedTask;
    }

    public static string Apply(string html)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return html;
        }

        var tag = $"<script {Marker} src=\"../Terebi/Client/terebi-web.js?v={ScriptVersion}\" defer></script>"
                + $"<script data-terebi-tv src=\"../Terebi/Client/terebi-tv.js?v={ScriptVersion}\" defer></script>";
        return html.Insert(idx, tag);
    }

    public static async Task TransformAsync(string path, Stream contents)
    {
        string html;
        using (var reader = new StreamReader(contents, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var transformed = Apply(html);
        if (ReferenceEquals(transformed, html))
        {
            contents.Seek(0, SeekOrigin.Begin);
            return;
        }

        contents.Seek(0, SeekOrigin.Begin);
        contents.SetLength(0);
        var bytes = Encoding.UTF8.GetBytes(transformed);
        await contents.WriteAsync(bytes).ConfigureAwait(false);
        contents.Seek(0, SeekOrigin.Begin);
    }
}
