#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.macOS@3.119.4
// Draws assets/logo.png: dotnet run assets/source/logo.cs -- assets/logo.png
using SkiaSharp;
const int W = 1280, H = 720;
using var surface = SKSurface.Create(new SKImageInfo(W, H));
var c = surface.Canvas;
var blue = new SKColor(22, 58, 122); var red = new SKColor(138, 30, 40);
using (var a = new SKPath()) using (var b = new SKPath())
{
    a.MoveTo(0, 0); a.LineTo(W * 0.56f, 0); a.LineTo(W * 0.44f, H); a.LineTo(0, H); a.Close();
    b.MoveTo(W * 0.56f, 0); b.LineTo(W, 0); b.LineTo(W, H); b.LineTo(W * 0.44f, H); b.Close();
    using var pa = new SKPaint { IsAntialias = true, Color = blue }; using var pb = new SKPaint { IsAntialias = true, Color = red };
    c.DrawPath(a, pa); c.DrawPath(b, pb);
}
using (var v = new SKPaint())
{
    v.Shader = SKShader.CreateRadialGradient(new SKPoint(W / 2f, H / 2f), W * 0.75f, [SKColors.Transparent, new SKColor(0, 0, 0, 160)], SKShaderTileMode.Clamp);
    c.DrawRect(0, 0, W, H, v);
}
using var bold = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold);
void Text(string t, float x, float y, float size, SKColor col)
{
    using var f = new SKFont(bold, size); using var p = new SKPaint { IsAntialias = true, Color = col };
    f.GetFontMetrics(out var m); c.DrawText(t, x, y - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, f, p);
}
void Shield(float cx, float cy, float s, SKColor team, string letter)
{
    using var path = new SKPath();
    path.MoveTo(cx - s * 0.5f, cy - s * 0.55f);
    path.LineTo(cx + s * 0.5f, cy - s * 0.55f);
    path.LineTo(cx + s * 0.5f, cy + s * 0.05f);
    path.CubicTo(cx + s * 0.5f, cy + s * 0.4f, cx + s * 0.2f, cy + s * 0.58f, cx, cy + s * 0.7f);
    path.CubicTo(cx - s * 0.2f, cy + s * 0.58f, cx - s * 0.5f, cy + s * 0.4f, cx - s * 0.5f, cy + s * 0.05f);
    path.Close();
    using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 110), ImageFilter = SKImageFilter.CreateBlur(14, 14) };
    c.Save(); c.Translate(0, 10); c.DrawPath(path, shadow); c.Restore();
    using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(245, 245, 248) };
    c.DrawPath(path, fill);
    using var inner = new SKPaint { IsAntialias = true, Color = team };
    c.Save(); c.Translate(cx, cy); c.Scale(0.8f); c.Translate(-cx, -cy); c.DrawPath(path, inner); c.Restore();
    // stripe
    using var stripe = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 60) };
    c.Save(); c.ClipPath(path, SKClipOperation.Intersect, true); c.DrawRect(cx - s, cy - s * 0.18f, s * 2, s * 0.12f, stripe); c.Restore();
    Text(letter, cx, cy + s * 0.04f, s * 0.5f, SKColors.White);
}
Shield(W * 0.25f, H * 0.4f, 250, new SKColor(40, 95, 190), "H");
Shield(W * 0.75f, H * 0.4f, 250, new SKColor(200, 45, 60), "A");
Text("HOME", W * 0.25f, H * 0.4f + 225, 34, new SKColor(255, 255, 255, 220));
Text("AWAY", W * 0.75f, H * 0.4f + 225, 34, new SKColor(255, 255, 255, 220));
var mid = new SKPoint(W / 2f, H * 0.4f);
using (var ring = new SKPaint { IsAntialias = true, Color = new SKColor(10, 10, 14, 235) })
using (var st = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 6 })
{ c.DrawCircle(mid, 72, ring); c.DrawCircle(mid, 72, st); }
Text("VS", mid.X, mid.Y, 60, SKColors.White);
Text("SPORTS GUIDE ART", W / 2f, H * 0.88f, 78, SKColors.White);
using var img = surface.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
File.WriteAllBytes(args[0], data.ToArray()); Console.WriteLine("ok");
