using System;
using System.Globalization;
using System.IO;
using SkiaSharp;

namespace Jellyfin.Plugin.SportsGuideArt.Imaging;

/// <summary>One side of a matchup image.</summary>
/// <param name="Name">The team's display name.</param>
/// <param name="Abbreviation">Short code shown under or instead of the logo.</param>
/// <param name="LogoPath">A cached local logo file, if any.</param>
/// <param name="Color">Hex color without '#', if known.</param>
public sealed record MatchupSide(string Name, string Abbreviation, string? LogoPath, string? Color);

/// <summary>Draws "Team A vs Team B" artwork with SkiaSharp.</summary>
public sealed class MatchupImageRenderer
{
    /// <summary>Bump when the drawing changes so cached images are redrawn.</summary>
    public const int Version = 3;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Renders a portrait poster (2:3).</summary>
    /// <param name="first">The first team.</param>
    /// <param name="second">The second team.</param>
    /// <param name="caption">A caption such as the league.</param>
    /// <returns>PNG bytes.</returns>
    public byte[] RenderPoster(MatchupSide first, MatchupSide second, string? caption)
        => Render(600, 900, portrait: true, first, second, caption);

    /// <summary>Renders a landscape thumbnail/backdrop (16:9).</summary>
    /// <param name="first">The first team.</param>
    /// <param name="second">The second team.</param>
    /// <param name="caption">A caption such as the league.</param>
    /// <returns>PNG bytes.</returns>
    public byte[] RenderLandscape(MatchupSide first, MatchupSide second, string? caption)
        => Render(1280, 720, portrait: false, first, second, caption);

    private static byte[] Render(int width, int height, bool portrait, MatchupSide first, MatchupSide second, string? caption)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(12, 14, 22));

        var colorA = Darken(ParseColor(first.Color, new SKColor(40, 70, 140)));
        var colorB = Darken(ParseColor(second.Color, new SKColor(140, 40, 50)));

        // Two team-colored halves split on a diagonal.
        using (var pathA = new SKPath())
        using (var pathB = new SKPath())
        {
            if (portrait)
            {
                pathA.MoveTo(0, 0);
                pathA.LineTo(width, 0);
                pathA.LineTo(width, height * 0.44f);
                pathA.LineTo(0, height * 0.56f);
                pathA.Close();
                pathB.MoveTo(0, height * 0.56f);
                pathB.LineTo(width, height * 0.44f);
                pathB.LineTo(width, height);
                pathB.LineTo(0, height);
                pathB.Close();
            }
            else
            {
                pathA.MoveTo(0, 0);
                pathA.LineTo(width * 0.56f, 0);
                pathA.LineTo(width * 0.44f, height);
                pathA.LineTo(0, height);
                pathA.Close();
                pathB.MoveTo(width * 0.56f, 0);
                pathB.LineTo(width, 0);
                pathB.LineTo(width, height);
                pathB.LineTo(width * 0.44f, height);
                pathB.Close();
            }

            using var fillA = new SKPaint { IsAntialias = true, Color = colorA };
            using var fillB = new SKPaint { IsAntialias = true, Color = colorB };
            canvas.DrawPath(pathA, fillA);
            canvas.DrawPath(pathB, fillB);
        }

        // Soft vignette for depth.
        using (var vignette = new SKPaint())
        {
            vignette.Shader = SKShader.CreateRadialGradient(
                new SKPoint(width / 2f, height / 2f),
                Math.Max(width, height) * 0.75f,
                [SKColors.Transparent, new SKColor(0, 0, 0, 150)],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, vignette);
        }

        using var bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        var logoSize = portrait ? width * 0.4f : height * 0.48f;
        SKPoint centerA;
        SKPoint centerB;
        if (portrait)
        {
            centerA = new SKPoint(width / 2f, height * 0.2f);
            centerB = new SKPoint(width / 2f, height * 0.72f);
        }
        else
        {
            centerA = new SKPoint(width * 0.25f, height * 0.44f);
            centerB = new SKPoint(width * 0.75f, height * 0.44f);
        }

        DrawSide(canvas, first, centerA, logoSize, bold, portrait);
        DrawSide(canvas, second, centerB, logoSize, bold, portrait);

        // "VS" badge in the middle.
        var mid = new SKPoint(width / 2f, height / 2f);
        var badge = portrait ? width * 0.09f : height * 0.1f;
        using (var ring = new SKPaint { IsAntialias = true, Color = new SKColor(10, 10, 14, 235) })
        using (var stroke = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 200), Style = SKPaintStyle.Stroke, StrokeWidth = badge * 0.08f })
        {
            canvas.DrawCircle(mid, badge, ring);
            canvas.DrawCircle(mid, badge, stroke);
        }

        DrawCentered(canvas, "VS", mid.X, mid.Y, badge * 0.85f, bold, SKColors.White);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            var captionSize = portrait ? width * 0.05f : height * 0.05f;
            DrawCentered(canvas, caption.ToUpperInvariant(), width / 2f, height - (captionSize * 1.4f), captionSize, bold, new SKColor(255, 255, 255, 190));
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void DrawSide(SKCanvas canvas, MatchupSide side, SKPoint center, float size, SKTypeface typeface, bool portrait)
    {
        using var logo = LoadLogo(side.LogoPath);
        var labelSize = size * (portrait ? 0.13f : 0.16f);
        if (logo is not null)
        {
            var scale = Math.Min(size / logo.Width, size / logo.Height);
            var w = logo.Width * scale;
            var h = logo.Height * scale;
            var rect = SKRect.Create(center.X - (w / 2), center.Y - (h / 2) - (labelSize * 0.6f), w, h);

            using (var shadow = new SKPaint())
            {
                shadow.ImageFilter = SKImageFilter.CreateDropShadow(0, size * 0.02f, size * 0.03f, size * 0.03f, new SKColor(0, 0, 0, 140));
                canvas.DrawImage(logo, rect, Sampling, shadow);
            }

            DrawCentered(canvas, Shorten(side), center.X, rect.Bottom + (labelSize * 1.1f), labelSize, typeface, SKColors.White);
        }
        else
        {
            DrawCentered(canvas, side.Abbreviation.ToUpperInvariant(), center.X, center.Y - (size * 0.08f), size * 0.42f, typeface, SKColors.White);
            DrawCentered(canvas, Shorten(side), center.X, center.Y + (size * 0.32f), labelSize, typeface, new SKColor(255, 255, 255, 210));
        }
    }

    private static string Shorten(MatchupSide side)
        => side.Name.Length <= 22 ? side.Name.ToUpperInvariant() : side.Abbreviation.ToUpperInvariant();

    private static SKImage? LoadLogo(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var data = SKData.Create(path);
            return data is null ? null : SKImage.FromEncodedData(data);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void DrawCentered(SKCanvas canvas, string text, float x, float y, float size, SKTypeface typeface, SKColor color)
    {
        using var font = new SKFont(typeface, size) { Embolden = false, Subpixel = true };
        using var paint = new SKPaint { IsAntialias = true, Color = color };

        // Shrink long text to fit within 90% of the canvas width.
        var maxWidth = canvas.DeviceClipBounds.Width * 0.9f;
        var measured = font.MeasureText(text);
        if (measured > maxWidth)
        {
            font.Size = size * maxWidth / measured;
        }

        font.GetFontMetrics(out var metrics);
        var baseline = y - ((metrics.Ascent + metrics.Descent) / 2);
        canvas.DrawText(text, x, baseline, SKTextAlign.Center, font, paint);
    }

    /// <summary>
    /// Picks the team color for the background: the primary color, unless it is near black or white
    /// and the alternate color has more character.
    /// </summary>
    /// <param name="color">Primary hex color.</param>
    /// <param name="altColor">Alternate hex color.</param>
    /// <returns>The chosen hex color, or null.</returns>
    public static string? PickColor(string? color, string? altColor)
    {
        static bool Flat(string? hex)
        {
            var c = ParseColor(hex, SKColors.Transparent);
            if (c == SKColors.Transparent)
            {
                return true;
            }

            var luminance = ((0.2126 * c.Red) + (0.7152 * c.Green) + (0.0722 * c.Blue)) / 255;
            return luminance < 0.08 || luminance > 0.92;
        }

        return Flat(color) && !Flat(altColor) ? altColor : color;
    }

    private static SKColor ParseColor(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var value = hex.Trim().TrimStart('#');
        return value.Length == 6 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
    }

    private static SKColor Darken(SKColor color)
    {
        // Keep team colors recognizable but dark enough for white text and logos.
        const float factor = 0.62f;
        return new SKColor((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor));
    }
}
