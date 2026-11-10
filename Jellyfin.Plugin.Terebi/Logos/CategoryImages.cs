using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Storage;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Terebi.Logos;

/// <summary>
/// Tile images for the category folders: a coloured card with the category name and a strip of the
/// category's first channel logos, rendered once per distinct content with the SkiaSharp Jellyfin ships.
/// </summary>
public sealed class CategoryImages
{
    private const int Width = 640;
    private const int Height = 360;
    private readonly LogoCache _logos;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<CategoryImages> _logger;
    private readonly string _dir;
    private readonly object _lock = new();
    private bool _unavailable;

    public CategoryImages(TerebiPaths paths, LogoCache logos, IServerApplicationHost appHost, ILogger<CategoryImages> logger)
    {
        _logos = logos;
        _appHost = appHost;
        _logger = logger;
        _dir = paths.Folder("logos");
    }

    /// <summary>Local file of the category tile (rendering it if needed), or null when tiles cannot be rendered.</summary>
    public string? PathFor(string category, IReadOnlyList<LogicalChannel> channels)
    {
        var url = UrlFor(category, channels);
        var name = url[(url.LastIndexOf('/') + 1)..];
        var path = Path.Combine(_dir, name);
        return name.StartsWith("cat-", StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    /// <summary>Local URL of the category tile, or the first channel logo (or placeholder) when rendering is not possible.</summary>
    public string UrlFor(string category, IReadOnlyList<LogicalChannel> channels)
    {
        var logos = channels.Select(c => _logos.LocalPath(c.Logo)).Where(p => p is not null && !p.EndsWith("placeholder.png", StringComparison.Ordinal)).Select(p => p!).Distinct(StringComparer.Ordinal).Take(4).ToList();
        var fallback = _logos.LocalUrlOrPlaceholder(channels.FirstOrDefault(c => _logos.LocalPath(c.Logo) is not null)?.Logo);
        if (_unavailable)
        {
            return fallback;
        }

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("v3|" + category + "|" + channels.Count + "|" + string.Join("|", logos)))).ToLowerInvariant();
        var file = "cat-" + hash + ".png";
        var path = Path.Combine(_dir, file);
        if (!File.Exists(path))
        {
            lock (_lock)
            {
                if (!File.Exists(path))
                {
                    try
                    {
                        Render(category, channels.Count, logos, path);
                    }
                    catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or DllNotFoundException or BadImageFormatException or TypeInitializationException)
                    {
                        _unavailable = true;
                        _logger.LogWarning(ex, "Terebi: SkiaSharp is not available, category tiles are disabled");
                        return fallback;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                    {
                        _logger.LogWarning(ex, "Terebi: could not render a tile for category {Category}", category);
                        return fallback;
                    }
                }
            }
        }

        return File.Exists(path) ? $"{_appHost.GetApiUrlForLocalAccess()}/Terebi/Logo/{file}" : fallback;
    }

    private static void Render(string category, int channelCount, IReadOnlyList<string> logoPaths, string outputPath)
    {
        var (top, bottom) = Palette(category);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(Width, Height), [top, bottom], null, SKShaderTileMode.Clamp) })
        {
            canvas.DrawRect(0, 0, Width, Height, bg);
        }

        // Soft highlight for depth.
        using (var glow = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(Width * 0.85f, Height * 0.1f), Width * 0.6f, [new SKColor(255, 255, 255, 70), new SKColor(255, 255, 255, 0)], null, SKShaderTileMode.Clamp) })
        {
            canvas.DrawRect(0, 0, Width, Height, glow);
        }

        // Category name: bold, shrunk to fit, CJK aware.
        var title = category.Trim();
        var (typeface, fallbackTypeface) = Typefaces(title);
        var size = 64f;
        using var font = new SKFont(typeface, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        while (size > 26 && MeasureMixed(title, font, fallbackTypeface, size) > Width - 64)
        {
            size -= 4;
            font.Size = size;
        }

        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) };
        var baseline = logoPaths.Count > 0 ? 150f : Height / 2f + size / 3f;
        DrawMixed(canvas, title, 32, baseline + 3, font, fallbackTypeface, size, shadow);
        DrawMixed(canvas, title, 32, baseline, font, fallbackTypeface, size, textPaint);

        // Channel count, small.
        using (var small = new SKFont(typeface, 22f) { Edging = SKFontEdging.SubpixelAntialias })
        using (var smallPaint = new SKPaint { Color = new SKColor(255, 255, 255, 190), IsAntialias = true })
        {
            canvas.DrawText(channelCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + (channelCount == 1 ? " channel" : " channels"), 34, baseline + 34, SKTextAlign.Left, small, smallPaint);
        }

        // Logo strip: up to four logos on light pills along the bottom.
        if (logoPaths.Count > 0)
        {
            const float pillW = 136f, pillH = 76f, gap = 12f, y = Height - pillH - 26f;
            var totalW = (logoPaths.Count * pillW) + ((logoPaths.Count - 1) * gap);
            var x = (Width - totalW) / 2f;
            using var pill = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true };
            using var sampling = new SKPaint { IsAntialias = true };
            foreach (var logoPath in logoPaths)
            {
                canvas.DrawRoundRect(new SKRect(x, y, x + pillW, y + pillH), 12, 12, pill);
                using var bitmap = SKBitmap.Decode(logoPath);
                if (bitmap is not null)
                {
                    // Tiles are 16:9 cards with a background of their own; draw only their central content area.
                    var src = bitmap.Width == LogoTile.Width && bitmap.Height == LogoTile.Height
                        ? SKRect.Create((LogoTile.Width - 340) / 2f, (LogoTile.Height - 300) / 2f, 340, 300)
                        : SKRect.Create(0, 0, bitmap.Width, bitmap.Height);
                    var scale = Math.Min((pillW - 16) / src.Width, (pillH - 12) / src.Height);
                    var w = src.Width * scale;
                    var h = src.Height * scale;
                    var dest = new SKRect(x + ((pillW - w) / 2), y + ((pillH - h) / 2), x + ((pillW + w) / 2), y + ((pillH + h) / 2));
                    using var image = SKImage.FromBitmap(bitmap);
                    canvas.DrawImage(image, src, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), sampling);
                }

                x += pillW + gap;
            }
        }

        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        var tmp = outputPath + ".tmp";
        using (var f = File.Create(tmp))
        {
            data.SaveTo(f);
        }

        File.Move(tmp, outputPath, overwrite: true);
    }

    private static (SKTypeface Main, SKTypeface? Fallback) Typefaces(string text)
    {
        var main = SKTypeface.FromFamilyName("Noto Sans", SKFontStyle.Bold) ?? SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        SKTypeface? fallback = null;
        foreach (var ch in text)
        {
            if (!main.ContainsGlyph(ch))
            {
                fallback = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, ch);
                if (fallback is not null)
                {
                    break;
                }
            }
        }

        return (main, fallback);
    }

    /// <summary>Text runs split between the main face and the fallback face (for scripts the main font lacks).</summary>
    private static IEnumerable<(string Run, bool UseFallback)> Runs(string text, SKTypeface main)
    {
        var sb = new StringBuilder();
        var fallbackRun = false;
        foreach (var ch in text)
        {
            var needsFallback = !main.ContainsGlyph(ch) && !char.IsWhiteSpace(ch);
            if (sb.Length > 0 && needsFallback != fallbackRun)
            {
                yield return (sb.ToString(), fallbackRun);
                sb.Clear();
            }

            fallbackRun = needsFallback;
            sb.Append(ch);
        }

        if (sb.Length > 0)
        {
            yield return (sb.ToString(), fallbackRun);
        }
    }

    private static float MeasureMixed(string text, SKFont font, SKTypeface? fallback, float size)
    {
        var total = 0f;
        foreach (var (run, useFallback) in Runs(text, font.Typeface))
        {
            if (useFallback && fallback is not null)
            {
                using var f = new SKFont(fallback, size);
                total += f.MeasureText(run);
            }
            else
            {
                total += font.MeasureText(run);
            }
        }

        return total;
    }

    private static void DrawMixed(SKCanvas canvas, string text, float x, float y, SKFont font, SKTypeface? fallback, float size, SKPaint paint)
    {
        foreach (var (run, useFallback) in Runs(text, font.Typeface))
        {
            if (useFallback && fallback is not null)
            {
                using var f = new SKFont(fallback, size);
                canvas.DrawText(run, x, y, SKTextAlign.Left, f, paint);
                x += f.MeasureText(run);
            }
            else
            {
                canvas.DrawText(run, x, y, SKTextAlign.Left, font, paint);
                x += font.MeasureText(run);
            }
        }
    }

    /// <summary>Two gradient colours: themed by keyword where the name tells the genre, otherwise a stable hue from the name.</summary>
    private static (SKColor Top, SKColor Bottom) Palette(string category)
    {
        var c = category.ToUpperInvariant();
        (float Hue, float Sat)? pick = null;
        foreach (var (words, hue) in Themes)
        {
            if (words.Any(w => c.Contains(w, StringComparison.Ordinal)))
            {
                pick = (hue, 0.62f);
                break;
            }
        }

        if (pick is null)
        {
            var h = 0;
            foreach (var ch in c)
            {
                h = (h * 31) + ch;
            }

            pick = (Math.Abs(h) % 360, 0.55f);
        }

        var (hue0, sat) = pick.Value;
        return (SKColor.FromHsl(hue0, sat * 100, 34), SKColor.FromHsl((hue0 + 28) % 360, sat * 100, 18));
    }

    private static readonly (string[] Words, float Hue)[] Themes =
    [
        (["XXX", "ADULT", "18+"], 345f),
        (["SPORT"], 140f),
        (["NEWS", "INFORMACYJNE", "INFO"], 4f),
        (["FILM", "MOVIE", "KINO", "CINEMA"], 268f),
        (["KIDS", "DZIECI", "CHILD"], 36f),
        (["MUSIC", "MUZYCZNE", "MUZYKA"], 318f),
        (["DOKUMENT", "DOCUMENTARY", "DOCS", "DOC"], 190f),
        (["REGION", "LOCAL", "LOKALNE"], 210f),
        (["ROZRYWKA", "ENTERTAINMENT", "LIFESTYLE"], 22f),
        (["ŚWIĄTECZNE", "SWIATECZNE", "CHRISTMAS", "SEASONAL"], 160f),
        (["PPV", "EVENT"], 52f),
        (["JP", "JAPAN"], 350f),
        (["KR", "KOREA"], 225f),
        (["CN", "CHINA"], 12f),
    ];
}
