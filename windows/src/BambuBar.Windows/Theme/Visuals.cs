using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Theme;

/// <summary>The Mac's FilamentProgressBar: a capsule filled with the filament colour, lighter at the start, with a soft
/// highlight along the top.</summary>
public sealed class ProgressCapsule : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double),
        typeof(ProgressCapsule), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush),
        typeof(ProgressCapsule), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Color _tint = Palette.Accent;

    public ProgressCapsule(double height)
    {
        Height = height;
        SetResourceReference(TrackProperty, ThemeKeys.Track);
    }

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public Color Tint
    {
        get => _tint;
        set { if (_tint != value) { _tint = value; InvalidateVisual(); } }
    }

    /// <summary>Moves to <paramref name="value"/> (0–1), gliding when animations are on.</summary>
    public void SetProgress(double value, bool animate)
    {
        value = Math.Clamp(value, 0, 1);
        if (!animate || Math.Abs(value - Progress) > 0.5)
        {
            BeginAnimation(ProgressProperty, null);
            Progress = value;
            return;
        }
        BeginAnimation(ProgressProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        if (Progress <= 0) return;
        var fill = Math.Max(h, w * Math.Min(1, Progress));
        var gradient = new LinearGradientBrush(Palette.WithAlpha(_tint, 0.75), _tint, 0);
        gradient.Freeze();
        dc.DrawRoundedRectangle(gradient, null, new Rect(0, 0, fill, h), r, r);
        var shine = h * 0.35;
        if (fill > h)
        {
            var highlight = Palette.Brush(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF));
            dc.DrawRoundedRectangle(highlight, null, new Rect(h * 0.4, 1, Math.Max(0, fill - h * 0.8), shine), shine / 2, shine / 2);
        }
    }
}

/// <summary>A filament spool seen from the side (the Mac's SpoolSwatch): a ring showing how much is left, the filament
/// in its colour with a hint of winding, and the hub in a deeper shade of that colour (with the spool's weight on it when
/// there is one); a dashed outline for an empty slot.</summary>
public sealed class SpoolSwatch : FrameworkElement
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush),
        typeof(SpoolSwatch), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RingProperty = DependencyProperty.Register(nameof(Ring), typeof(Brush),
        typeof(SpoolSwatch), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(nameof(Outline), typeof(Brush),
        typeof(SpoolSwatch), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Spool? _spool;
    private readonly bool _active;
    private readonly string? _hubText;

    /// <param name="hubText">Printed on a wider hub, such as the full spool's weight.</param>
    public SpoolSwatch(Spool? spool, bool active, double size, string? hubText = null)
    {
        _spool = spool;
        _active = active;
        _hubText = hubText;
        Width = Height = size;
        SetResourceReference(TrackProperty, ThemeKeys.Track);
        SetResourceReference(RingProperty, ThemeKeys.TextSecondary);
        SetResourceReference(OutlineProperty, ThemeKeys.TextTertiary);
    }

    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush? Ring { get => (Brush?)GetValue(RingProperty); set => SetValue(RingProperty, value); }
    public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => null!;

    protected override void OnRender(DrawingContext dc)
    {
        var size = Width;
        var ring = Math.Max(2, size * 0.075);
        var gap = Math.Max(1.5, size * 0.06);
        var c = new Point(size / 2, size / 2);
        var rr = (size - ring) / 2;
        dc.DrawEllipse(null, new Pen(Track, ring), c, rr, rr);
        var color = _spool is { IsLoaded: true } ? Palette.FromBambu(_spool.Color) : null;
        var accent = Palette.Brush(Palette.Accent);
        if (_spool?.Remain is { } remain && color is not null)
        {
            var pen = new Pen(_active ? accent : Ring, ring) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            DrawArc(dc, pen, c, rr, remain / 100.0);
        }
        else if (_active)
        {
            dc.DrawEllipse(null, new Pen(accent, ring), c, rr, rr);
        }

        var inner = size / 2 - ring - gap;
        if (color is not { } fill)
        {
            var dashed = new Pen(Outline, 1) { DashStyle = new DashStyle([2.5, 2.5], 0) };
            dashed.Freeze();
            dc.DrawEllipse(null, dashed, c, inner - 0.5, inner - 0.5);
            return;
        }
        var colors = _spool!.Colors.Select(Palette.FromBambu).OfType<Color>().ToList();
        if (colors.Count > 1)
        {
            // The Mac sweeps an angular gradient; WPF has none, so multicolour spools show as slices.
            for (var i = 0; i < colors.Count; i++)
                dc.DrawGeometry(Palette.Brush(colors[i]), null, Slice(c, inner, i / (double)colors.Count, (i + 1) / (double)colors.Count));
        }
        else dc.DrawEllipse(Palette.Brush(fill), null, c, inner, inner);
        SpoolArt.Finish(dc, c, inner, size, fill, Palette.Luminance(_spool.Color), _hubText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double fraction)
    {
        if (fraction >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        if (fraction <= 0) return;
        var start = new Point(c.X, c.Y - r);
        var angle = fraction * 2 * Math.PI;
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static Geometry Slice(Point c, double r, double from, double to)
    {
        Point At(double f) => new(c.X + r * Math.Sin(f * 2 * Math.PI), c.Y - r * Math.Cos(f * 2 * Math.PI));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(c, true, true);
            ctx.LineTo(At(from), false, false);
            ctx.ArcTo(At(to), new Size(r, r), 0, to - from > 0.5, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>The parts of a spool drawn over its filament colour, shared by the app's swatches and the widget previews:
/// light from the top left, a few turns of winding, and the hub.</summary>
public static class SpoolArt
{
    /// <summary>The hub's colour: the filament's own, deepened (more for light ones, so white text holds on it), or
    /// lifted for near-black filament, so the core reads as part of the spool rather than a hole.</summary>
    public static Color Hub(Color fill, double luminance) => luminance switch
    {
        < 0.16 => Mix(fill, Colors.White, 0.26),
        > 0.7 => Mix(fill, Colors.Black, 0.5),
        _ => Mix(fill, Colors.Black, 0.4),
    };

    public static void Finish(DrawingContext dc, Point c, double inner, double size, Color fill, double luminance, string? hubText, double pixelsPerDip)
    {
        var dark = luminance < 0.16;
        // Light from the top left, falling off towards the rim.
        var gloss = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF), 0), new(Colors.Transparent, 0.48), new(Color.FromArgb(0x30, 0, 0, 0), 1),
        }, new Point(0.15, 0.1), new Point(0.9, 1));
        gloss.Freeze();
        dc.DrawEllipse(gloss, null, c, inner, inner);
        // A few turns of filament: faint rings between the hub and the rim.
        var hub = hubText is null ? size * 0.14 : size * 0.29;
        if (inner - hub > 6)
        {
            var winding = new Pen(Palette.Brush(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0)), 0.75);
            winding.Freeze();
            for (var i = 1; i <= 3; i++)
            {
                var r = hub + (inner - hub) * i / 4.0;
                dc.DrawEllipse(null, winding, c, r, r);
            }
        }
        // The rim: a hairline so pale filament keeps its edge on a light card.
        var rim = new Pen(Palette.Brush(luminance > 0.85 ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x14, 0, 0, 0)), 0.75);
        rim.Freeze();
        dc.DrawEllipse(null, rim, c, inner - 0.375, inner - 0.375);

        var core = Hub(fill, luminance);
        var hubFill = new RadialGradientBrush(Mix(core, Colors.Black, 0.12), core) { GradientOrigin = new Point(0.4, 0.35), RadiusX = 0.6, RadiusY = 0.6 };
        hubFill.Freeze();
        var hubEdge = new Pen(Palette.Brush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 0.75);
        hubEdge.Freeze();
        dc.DrawEllipse(hubFill, hubEdge, c, hub, hub);
        if (hubText is null)
        {
            // The spool's bore.
            dc.DrawEllipse(Palette.Brush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)), null, c, hub * 0.38, hub * 0.38);
            return;
        }
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var em = size * 0.18;
        FormattedText Text(double em) => new(hubText, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, em, Brushes.White, pixelsPerDip);
        var text = Text(em);
        var room = hub * 2 * 0.86;
        if (text.Width > room) text = Text(Math.Max(7, em * room / text.Width));
        dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}

internal static class PenExtensions
{
    public static Pen CloneWithOpacity(this Pen pen, double opacity)
    {
        var copy = pen.Clone();
        if (copy.Brush is { } b)
        {
            var brush = b.Clone();
            brush.Opacity = opacity;
            copy.Brush = brush;
        }
        copy.Freeze();
        return copy;
    }
}

/// <summary>Fluent's indeterminate progress ring (the Mac's small ProgressView). Spins only while on screen and only
/// when Windows' animations are on.</summary>
public sealed class Spinner : FrameworkElement
{
    private readonly RotateTransform _rotation = new();
    private readonly bool _animate;

    public Spinner(double size, bool animate)
    {
        Width = Height = size;
        _animate = animate;
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Loaded += (_, _) => Spin(true);
        Unloaded += (_, _) => Spin(false);
        IsVisibleChanged += (_, e) => Spin((bool)e.NewValue);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => null!;

    private void Spin(bool on)
    {
        if (!_animate || (on && !AppTheme.AnimationsEnabled)) return;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, on && IsVisible
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Width;
        var stroke = Math.Max(2, size / 8);
        var r = (size - stroke) / 2;
        var c = new Point(size / 2, size / 2);
        var pen = new Pen(Palette.Brush(Palette.Accent), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>The printer as Core's compositor draws it (the same art as the widgets), as a WPF image. Stands in for the
/// Mac's 3D model until P3.10. Rendered at 3× for sharp edges at 200–300 % scaling and cached by scene.</summary>
public static class ArtImage
{
    private const double Scale = 3;
    private static readonly Dictionary<string, BitmapSource?> Cache = [];

    public static BitmapSource? For(PrinterArt.Scene scene, double width, double height)
    {
        var key = string.Join('|', scene.Shape, scene.State, Math.Round(PrinterArt.ShownFraction(scene.State, scene.Progress), 2),
            scene.Filament, string.Join(',', scene.Spools), scene.LightOn, scene.Dimmed, width, height);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        if (Cache.Count > 48) Cache.Clear();
        BitmapSource? bitmap = null;
        try
        {
            if (PrinterArt.Render(scene with { Progress = Math.Round(scene.Progress, 2) }, (int)(width * Scale), (int)(height * Scale)) is { } rgba)
                bitmap = ToBitmap(rgba, 96 * Scale);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Missing or unreadable renders: the card shows without its printer.
        }
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Straight-alpha RGBA → a frozen BGRA bitmap.</summary>
    public static BitmapSource ToBitmap(Rgba image, double dpi)
    {
        var pixels = new byte[image.Pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = image.Pixels[i + 2];
            pixels[i + 1] = image.Pixels[i + 1];
            pixels[i + 2] = image.Pixels[i];
            pixels[i + 3] = image.Pixels[i + 3];
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, dpi, dpi, PixelFormats.Bgra32, null, pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>An image element for the scene; empty when the renders are missing. Decorative for screen readers.</summary>
    public static Image Element(double width, double height) => new SilentImage
    {
        Width = width, Height = height, Stretch = Stretch.Uniform, SnapsToDevicePixels = true,
    };

    public static void Show(Image image, PrinterArt.Scene scene)
    {
        image.Source = For(scene, image.Width, image.Height);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
    }

    /// <summary>The scene for a printer: its shape, the print revealed by progress, the filaments, the light.</summary>
    public static PrinterArt.Scene Scene(PrinterStatus? status, string? model, bool lightOn, bool dimmed, string? lastFilament = null)
    {
        var active = status?.ActiveSpool;
        var unit = status?.Ams.FirstOrDefault();
        var filament = active?.Color;
        // After a print the part stays on the plate in the colour it was printed in.
        if (filament is null && status is not null && !status.State.IsActive()) filament = lastFilament;
        return new PrinterArt.Scene
        {
            Shape = PrinterShapes.FromModel(model),
            State = status?.State ?? JobState.Idle,
            Progress = (status?.Progress ?? 0) / 100.0,
            Filament = filament,
            Spools = (unit?.Spools ?? []).Select(s => s.IsLoaded ? s.Color : null).ToList(),
            LightOn = lightOn,
            Dimmed = dimmed,
        };
    }

    private sealed class SilentImage : Image
    {
        protected override AutomationPeer OnCreateAutomationPeer() => null!;
    }
}
