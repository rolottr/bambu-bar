using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BambuBar.Core.Model;

namespace BambuBar.Core.Imaging;

/// <summary>
/// The printer as the widgets and the app draw it, composed from the Blender renders in Assets\Printers (see
/// artwork/generate_printers.py): a contact shadow, the body with the chamber light on or off, the AMS spools tinted
/// with their filament, the print revealed from the plate up in the active filament's colour, and the door glass's
/// sheen on top. The Mac's PrinterRender, with the same blending (CoreGraphics' multiply then destination-in, in
/// premultiplied sRGB), done on the CPU so it runs anywhere and Rainmeter just shows a PNG.
/// </summary>
public static class PrinterArt
{
    /// <summary>What to draw. Colours are the printer's <c>RRGGBBAA</c>; null or fully transparent leaves a spool out.</summary>
    public sealed record Scene
    {
        public required PrinterShape Shape { get; init; }
        public required JobState State { get; init; }
        /// <summary>0–1.</summary>
        public double Progress { get; init; }
        public string? Filament { get; init; }
        public IReadOnlyList<string?> Spools { get; init; } = [];
        public bool LightOn { get; init; } = true;
        /// <summary>Offline: the light off, then washed out (saturation 0.1, opacity 0.55) as the Mac's widgets show it.</summary>
        public bool Dimmed { get; init; }
    }

    /// <summary>Bambu-ish green, the part's colour when the active filament is unknown (Palette.accent).</summary>
    private static readonly (double R, double G, double B) Accent = (0.13, 0.80, 0.42);

    private sealed record Box(double X, double Y, double W, double H);

    private sealed class RenderSet
    {
        public required Rgba Body, Dark, Part;
        public Rgba? Bare, BareDark, Sheen;
        public required List<Rgba> Spools;
        public required Box PartBox;
        public Box? ModelBox;
        public int Width => Body.Width;
        public int Height => Body.Height;
    }

    private static readonly ConcurrentDictionary<string, RenderSet?> Sets = new();

    private static RenderSet? Load(string name) => Sets.GetOrAdd(name, n =>
    {
        Rgba? Image(string suffix)
        {
            var path = Assets.PathOf("Printers", $"{n}-{suffix}.png");
            return File.Exists(path) ? Png.Read(path) : null;
        }
        var json = Assets.PathOf("Printers", $"{n}.json");
        if (Image("body") is not { } body || Image("dark") is not { } dark || Image("part") is not { } part || !File.Exists(json)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
        static Box BoxOf(JsonElement e) => new(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(),
            e.GetProperty("w").GetDouble(), e.GetProperty("h").GetDouble());
        return new RenderSet
        {
            Body = body, Dark = dark, Part = part, Bare = Image("bare"), BareDark = Image("baredark"), Sheen = Image("sheen"),
            // Four AMS spools, or the H3C's nine.
            Spools = Enumerable.Range(0, 9).Select(i => Image($"spool{i}")).OfType<Rgba>().ToList(),
            PartBox = BoxOf(doc.RootElement.GetProperty("part")),
            ModelBox = doc.RootElement.TryGetProperty("model", out var model) ? BoxOf(model) : null,
        };
    });

    /// <summary>How much of the print stands on the plate.</summary>
    public static double ShownFraction(JobState state, double progress) => state switch
    {
        JobState.Finished => 1,
        JobState.Idle or JobState.Failed => Math.Min(1, progress),
        JobState.Preparing => progress > 0 ? Math.Max(0.03, progress) : 0,
        // Something is always on the plate once printing starts.
        _ => Math.Max(0.03, Math.Min(1, progress)),
    };

    /// <summary>The scene at the renders' own size (480 × 576), or null when the shape's renders are missing.</summary>
    public static Rgba? Compose(Scene scene)
    {
        if (Load(scene.Shape.AssetName()) is not { } set) return null;
        var canvas = new Canvas(set.Width, set.Height);
        var spools = scene.Spools.Select(Color).ToList();

        if (set.ModelBox is { } box)
        {
            // Soft contact shadow where the printer meets the desk.
            canvas.Shadow(box.X - box.W * 0.025, box.Y + box.H - box.W * 0.11, box.W * 1.05, box.W * 0.16, box.W * 0.5);
        }
        var lit = scene.LightOn && !scene.Dimmed;
        var body = spools.Count == 0 && set.Bare is { } bare && set.BareDark is { } bareDark
            ? lit ? bare : bareDark
            : lit ? set.Body : set.Dark;
        canvas.Draw(body);
        for (var i = 0; i < set.Spools.Count && i < spools.Count; i++)
        {
            if (spools[i] is { } color) canvas.DrawTinted(set.Spools[i], color);
        }
        var shown = ShownFraction(scene.State, scene.Progress);
        if (shown > 0)
        {
            var top = set.PartBox.Y + set.PartBox.H * (1 - shown);
            canvas.DrawTinted(set.Part, Color(scene.Filament) ?? (Accent.R, Accent.G, Accent.B, 1), fromRow: top);
        }
        if (set.Sheen is { } sheen) canvas.Draw(sheen, opacity: 0.85);
        if (scene.Dimmed) canvas.Wash(saturation: 0.1, opacity: 0.55);
        return canvas.ToRgba();
    }

    /// <summary>The scene scaled to fit <paramref name="width"/> × <paramref name="height"/>, centred.</summary>
    public static Rgba? Render(Scene scene, int width, int height) =>
        Compose(scene) is { } full ? Resample.Fit(full, width, height) : null;

    /// <summary>
    /// Writes the scene as <c>art-&lt;hash&gt;.png</c> in <paramref name="folder"/> (Rainmeter may cache images by
    /// path, ledger §4.15) and returns the file name; an unchanged scene reuses its file.
    /// </summary>
    public static string? WritePng(Scene scene, int width, int height, string folder)
    {
        var key = string.Join('|', scene.Shape, scene.State, Math.Round(ShownFraction(scene.State, scene.Progress), 3),
            scene.Filament, string.Join(',', scene.Spools), scene.LightOn, scene.Dimmed, width, height, Version);
        var name = $"art-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]}.png";
        var path = Path.Combine(folder, name);
        if (File.Exists(path)) return name;
        if (Render(scene, width, height) is not { } image) return null;
        Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Png.Encode(image));
        File.Move(temp, path, overwrite: true);
        return name;
    }

    /// <summary>Raise when the drawing changes, so files written by an older app aren't reused.</summary>
    private const int Version = 1;

    /// <summary>Removes art files in <paramref name="folder"/> that none of <paramref name="keep"/> names.</summary>
    public static void Prune(string folder, IEnumerable<string> keep)
    {
        if (!Directory.Exists(folder)) return;
        var wanted = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(folder, "art-*.png"))
        {
            if (wanted.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch (IOException) { /* Rainmeter has it open; next time. */ }
        }
    }

    /// <summary><c>RRGGBB</c> or <c>RRGGBBAA</c> as the Mac reads it: fully transparent is no colour, and a faint one
    /// still shows at 35 %.</summary>
    public static (double R, double G, double B, double A)? Color(string? hex)
    {
        var clean = new string((hex ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (clean.Length is not (6 or 8) || !ulong.TryParse(clean, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var v)) return null;
        double r, g, b, a;
        if (clean.Length == 8) { r = (v >> 24 & 0xFF) / 255.0; g = (v >> 16 & 0xFF) / 255.0; b = (v >> 8 & 0xFF) / 255.0; a = (v & 0xFF) / 255.0; }
        else { r = (v >> 16 & 0xFF) / 255.0; g = (v >> 8 & 0xFF) / 255.0; b = (v & 0xFF) / 255.0; a = 1; }
        if (a == 0) return null;
        return (r, g, b, Math.Max(a, 0.35));
    }

    /// <summary>Premultiplied RGBA in 0–1, composited source-over.</summary>
    private sealed class Canvas(int width, int height)
    {
        private readonly float[] _p = new float[width * height * 4];

        private void Over(int i, float r, float g, float b, float a)
        {
            var k = 1 - a;
            _p[i] = r + _p[i] * k;
            _p[i + 1] = g + _p[i + 1] * k;
            _p[i + 2] = b + _p[i + 2] * k;
            _p[i + 3] = a + _p[i + 3] * k;
        }

        public void Draw(Rgba image, double opacity = 1)
        {
            var px = image.Pixels;
            for (int i = 0, n = width * height * 4; i < n; i += 4)
            {
                var a = px[i + 3] / 255f * (float)opacity;
                if (a <= 0) continue;
                Over(i, px[i] / 255f * a, px[i + 1] / 255f * a, px[i + 2] / 255f * a, a);
            }
        }

        /// <summary>The Mac's <c>tinted</c>: the image, a multiply fill of the colour over it, then cut to the image's own
        /// alpha (shading kept, filament colour applied). Rows above <paramref name="fromRow"/> are masked out, with a
        /// partial row at the edge, as the part grows from the plate.</summary>
        public void DrawTinted(Rgba image, (double R, double G, double B, double A) color, double fromRow = 0)
        {
            var px = image.Pixels;
            float cr = (float)color.R, cg = (float)color.G, cb = (float)color.B, ca = (float)color.A;
            var first = Math.Max(0, (int)Math.Floor(fromRow));
            for (var y = first; y < height; y++)
            {
                var coverage = y + 1 <= fromRow ? 0f : y < fromRow ? (float)(y + 1 - fromRow) : 1f;
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var sa = px[i + 3] / 255f;
                    if (sa <= 0) continue;
                    // Backdrop: the image (premultiplied). Source: the fill colour with alpha ca, blended by multiply.
                    float br = px[i] / 255f * sa, bg = px[i + 1] / 255f * sa, bb = px[i + 2] / 255f * sa;
                    // W3C separable blending: Cs' = (1 - ab) Cs + ab B(Cb, Cs); out = as Cs' + (1 - as) ab Cb.
                    float Mix(float cbStraight, float cs) => (1 - sa) * cs + sa * (cbStraight * cs);
                    var or = ca * Mix(px[i] / 255f, cr) + (1 - ca) * br;
                    var og = ca * Mix(px[i + 1] / 255f, cg) + (1 - ca) * bg;
                    var ob = ca * Mix(px[i + 2] / 255f, cb) + (1 - ca) * bb;
                    var oa = ca + sa * (1 - ca);
                    // Destination-in with the image's alpha, then the progress mask.
                    var cut = sa * coverage;
                    Over(i, or * cut, og * cut, ob * cut, oa * cut);
                }
            }
        }

        /// <summary>An ellipse filled with a circular radial gradient (black 55 % → 20 % → clear), as SwiftUI draws it.</summary>
        public void Shadow(double x, double y, double w, double h, double radius)
        {
            double cx = x + w / 2, cy = y + h / 2;
            for (var py = Math.Max(0, (int)y); py < Math.Min(height, (int)Math.Ceiling(y + h)); py++)
            {
                for (var px = Math.Max(0, (int)x); px < Math.Min(width, (int)Math.Ceiling(x + w)); px++)
                {
                    double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                    if (dx * dx / (w * w / 4) + dy * dy / (h * h / 4) > 1) continue;
                    var t = Math.Sqrt(dx * dx + dy * dy) / radius;
                    var a = t < 0.5 ? 0.55 + (0.2 - 0.55) * (t / 0.5) : t < 1 ? 0.2 * (1 - (t - 0.5) / 0.5) : 0;
                    if (a > 0) Over((py * width + px) * 4, 0, 0, 0, (float)a);
                }
            }
        }

        /// <summary>SwiftUI's <c>.saturation(s).opacity(o)</c> on the whole picture.</summary>
        public void Wash(double saturation, double opacity)
        {
            for (var i = 0; i < _p.Length; i += 4)
            {
                var a = _p[i + 3];
                if (a <= 0) continue;
                var lum = 0.2126f * _p[i] + 0.7152f * _p[i + 1] + 0.0722f * _p[i + 2];
                var s = (float)saturation;
                _p[i] = (lum + (_p[i] - lum) * s) * (float)opacity;
                _p[i + 1] = (lum + (_p[i + 1] - lum) * s) * (float)opacity;
                _p[i + 2] = (lum + (_p[i + 2] - lum) * s) * (float)opacity;
                _p[i + 3] = a * (float)opacity;
            }
        }

        public Rgba ToRgba()
        {
            var image = new Rgba(width, height);
            for (var i = 0; i < _p.Length; i += 4)
            {
                var a = _p[i + 3];
                if (a <= 0) continue;
                image.Pixels[i] = ToByte(_p[i] / a);
                image.Pixels[i + 1] = ToByte(_p[i + 1] / a);
                image.Pixels[i + 2] = ToByte(_p[i + 2] / a);
                image.Pixels[i + 3] = ToByte(a);
            }
            return image;
        }

        private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
    }
}

/// <summary>Area-averaged downscaling in premultiplied space (no dark fringes at transparent edges).</summary>
public static class Resample
{
    /// <summary><paramref name="source"/> scaled to fit <paramref name="width"/> × <paramref name="height"/>, centred,
    /// aspect kept.</summary>
    public static Rgba Fit(Rgba source, int width, int height)
    {
        var scale = Math.Min((double)width / source.Width, (double)height / source.Height);
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var scaled = Scale(source, w, h);
        if (w == width && h == height) return scaled;
        var output = new Rgba(width, height);
        int ox = (width - w) / 2, oy = (height - h) / 2;
        for (var y = 0; y < h; y++) Array.Copy(scaled.Pixels, y * w * 4, output.Pixels, ((y + oy) * width + ox) * 4, w * 4);
        return output;
    }

    public static Rgba Scale(Rgba source, int width, int height)
    {
        var output = new Rgba(width, height);
        double sx = (double)source.Width / width, sy = (double)source.Height / height;
        for (var y = 0; y < height; y++)
        {
            double y0 = y * sy, y1 = y0 + sy;
            for (var x = 0; x < width; x++)
            {
                double x0 = x * sx, x1 = x0 + sx;
                double r = 0, g = 0, b = 0, a = 0, total = 0;
                for (var yy = (int)y0; yy < Math.Min(source.Height, (int)Math.Ceiling(y1)); yy++)
                {
                    var wy = Math.Min(yy + 1, y1) - Math.Max(yy, y0);
                    for (var xx = (int)x0; xx < Math.Min(source.Width, (int)Math.Ceiling(x1)); xx++)
                    {
                        var weight = wy * (Math.Min(xx + 1, x1) - Math.Max(xx, x0));
                        var i = source.Offset(xx, yy);
                        var pa = source.Pixels[i + 3] / 255.0 * weight;
                        r += source.Pixels[i] * pa;
                        g += source.Pixels[i + 1] * pa;
                        b += source.Pixels[i + 2] * pa;
                        a += pa;
                        total += weight;
                    }
                }
                var o = output.Offset(x, y);
                if (a <= 0 || total <= 0) continue;
                output.Pixels[o] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
                output.Pixels[o + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
                output.Pixels[o + 2] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
                output.Pixels[o + 3] = (byte)Math.Clamp(Math.Round(a / total * 255), 0, 255);
            }
        }
        return output;
    }
}
