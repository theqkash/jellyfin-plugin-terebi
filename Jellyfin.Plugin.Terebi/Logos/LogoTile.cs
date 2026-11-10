using SkiaSharp;

namespace Jellyfin.Plugin.Terebi.Logos;

/// <summary>
/// Turns a raw logo into a card-friendly tile: Jellyfin fills its cards with the image and crops whatever
/// does not fit, so a wide logo loses its ends and a black-on-transparent logo vanishes on the dark theme.
/// The tile is 16:9 with the logo trimmed to its content, kept inside the central square (so a square
/// card crop keeps it whole as well) and placed on a light or dark background chosen from the logo's
/// own brightness. Uses the SkiaSharp that ships with Jellyfin.
/// </summary>
public static class LogoTile
{
    public const int Width = 640;
    public const int Height = 360;
    private const int BoxWidth = 330;
    private const int BoxHeight = 290;

    /// <summary>Renders the tile; returns false when the input cannot be decoded.</summary>
    public static bool TryRender(string inputPath, string outputPath)
    {
        using var source = SKBitmap.Decode(inputPath);
        if (source is null || source.Width < 2 || source.Height < 2)
        {
            return false;
        }

        var (bounds, brightness, opaqueShare) = Analyse(source);
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            return false;
        }

        // Dark marks need a light card, light or colourful marks a dark one. An image that is mostly opaque
        // brings its own background, so keep that impression by matching it.
        var lightBackground = brightness < 0.45;
        var background = lightBackground ? new SKColor(0xF3, 0xF3, 0xF1) : new SKColor(0x1E, 0x1F, 0x26);

        var scale = Math.Min((double)BoxWidth / bounds.Width, (double)BoxHeight / bounds.Height);
        if (opaqueShare > 0.9)
        {
            // Opaque picture (photo-like logo card): let it use more of the tile height, still inside the safe square.
            scale = Math.Min((double)BoxWidth / bounds.Width, 320.0 / bounds.Height);
        }

        var destWidth = (float)(bounds.Width * scale);
        var destHeight = (float)(bounds.Height * scale);
        var dest = new SKRect((Width - destWidth) / 2, (Height - destHeight) / 2, (Width + destWidth) / 2, (Height + destHeight) / 2);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(background);
        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, SKRect.Create(bounds.Left, bounds.Top, bounds.Width, bounds.Height), dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        var tmp = outputPath + ".tmp";
        using (var file = File.Create(tmp))
        {
            data.SaveTo(file);
        }

        File.Move(tmp, outputPath, overwrite: true);
        return true;
    }

    /// <summary>Content bounds (alpha above a threshold), mean brightness of opaque pixels, and the opaque share of the image.</summary>
    private static (SKRectI Bounds, double Brightness, double OpaqueShare) Analyse(SKBitmap bitmap)
    {
        var step = Math.Max(1, (int)Math.Sqrt((double)bitmap.Width * bitmap.Height / 40000));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        double lum = 0;
        long opaque = 0, samples = 0;
        var cornerIsWhite = IsNearWhite(bitmap.GetPixel(0, 0)) && IsNearWhite(bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1));

        for (var y = 0; y < bitmap.Height; y += step)
        {
            for (var x = 0; x < bitmap.Width; x += step)
            {
                samples++;
                var p = bitmap.GetPixel(x, y);
                if (p.Alpha < 24)
                {
                    continue;
                }

                // A logo on a plain white canvas: treat white as background for bounds and brightness.
                if (cornerIsWhite && IsNearWhite(p))
                {
                    continue;
                }

                opaque++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                lum += ((0.2126 * p.Red) + (0.7152 * p.Green) + (0.0722 * p.Blue)) / 255.0;
            }
        }

        if (opaque == 0)
        {
            return (SKRectI.Empty, 0, 0);
        }

        var bounds = new SKRectI(Math.Max(0, minX - step), Math.Max(0, minY - step), Math.Min(bitmap.Width, maxX + step + 1), Math.Min(bitmap.Height, maxY + step + 1));
        return (bounds, lum / opaque, (double)opaque / samples);
    }

    private static bool IsNearWhite(SKColor c) => c.Alpha > 200 && c.Red > 235 && c.Green > 235 && c.Blue > 235;
}
