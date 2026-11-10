using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BambuBar.Core.Model;

namespace BambuBar.Theme;

/// <summary>
/// The Mac's SF Symbols, mapped to Segoe Fluent Icons (Windows 11; the same code points exist in Segoe MDL2 Assets on
/// Windows 10, which WPF falls back to). Symbols the icon fonts lack (flame, heat waves, thermometer, fan, light bulb,
/// the speed dial) are drawn as small vectors in a 16 × 16 box.
/// </summary>
public static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>gearshape</summary>
    public const string Settings = "";
    /// <summary>arrow.clockwise</summary>
    public const string Refresh = "";
    /// <summary>chevron.down</summary>
    public const string ChevronDown = "";
    /// <summary>chevron.right</summary>
    public const string ChevronRight = "";
    /// <summary>square.grid.2x2 (ViewAll)</summary>
    public const string AllPrinters = "";
    /// <summary>macwindow (OpenInNewWindow)</summary>
    public const string OpenWindow = "";
    /// <summary>play.fill</summary>
    public const string Play = "";
    /// <summary>pause.fill</summary>
    public const string Pause = "";
    /// <summary>stop.fill</summary>
    public const string Stop = "";
    /// <summary>lock.fill</summary>
    public const string Lock = "";
    /// <summary>doc.on.doc</summary>
    public const string Copy = "";
    /// <summary>checkmark</summary>
    public const string Check = "";
    /// <summary>xmark</summary>
    public const string Close = "";
    /// <summary>clock (Recent)</summary>
    public const string Clock = "";
    /// <summary>exclamationmark.triangle.fill</summary>
    public const string Warning = "";
    /// <summary>info.circle.fill</summary>
    public const string Info = "";
    /// <summary>exclamationmark.circle.fill</summary>
    public const string Alert = "";
    /// <summary>checkmark.circle.fill (CompletedSolid)</summary>
    public const string Done = "";
    /// <summary>xmark.circle.fill (StatusErrorFull)</summary>
    public const string Failed = "";
    /// <summary>arrow.down.circle.fill</summary>
    public const string Download = "";
    /// <summary>printer.fill</summary>
    public const string Printer = "";
    /// <summary>video</summary>
    public const string Camera = "";

    public enum Vector { Flame, HeatWaves, Thermometer, Fan, Bulb, BulbLit }

    /// <summary>A font icon. Decorative: screen readers skip it (the control around it carries the name).</summary>
    public static TextBlock Glyph(string glyph, double size, string? brushKey = ThemeKeys.TextSecondary, Brush? brush = null)
    {
        var text = new SilentText
        {
            Text = glyph, FontFamily = Font, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        };
        if (brush is not null) text.Foreground = brush;
        else if (brushKey is not null) text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }

    /// <summary>A drawn icon, <paramref name="size"/> DIPs square.</summary>
    public static FrameworkElement Draw(Vector icon, double size, Brush brush)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        void Fill(Geometry g) => canvas.Children.Add(new Path { Data = g, Fill = brush });
        void Stroke(Geometry g, double width = 1.4) => canvas.Children.Add(new Path
        {
            Data = g, Stroke = brush, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        switch (icon)
        {
            case Vector.Flame:
                Fill(Geometry.Parse("M8,0.8 C8.6,3.2 12.5,5.3 12.5,9.6 C12.5,12.9 10.4,15.2 8,15.2 C5.6,15.2 3.5,12.9 3.5,9.9 C3.5,7.9 4.6,6.5 5.5,5.4 C5.7,6.9 6.3,7.9 7.2,8.3 C6.9,5.6 7.3,2.8 8,0.8 Z"));
                break;
            case Vector.HeatWaves:
                Stroke(Geometry.Parse("M4.5,2 C3,4 6,6 4.5,8 C3,10 6,12 4.5,14 M8,2 C6.5,4 9.5,6 8,8 C6.5,10 9.5,12 8,14 M11.5,2 C10,4 13,6 11.5,8 C10,10 13,12 11.5,14"));
                break;
            case Vector.Thermometer:
                Stroke(Geometry.Parse("M6.4,9.4 L6.4,3 A1.6,1.6 0 0 1 9.6,3 L9.6,9.4 A3.1,3.1 0 1 1 6.4,9.4 Z"), 1.2);
                Fill(new EllipseGeometry(new Point(8, 11.9), 1.6, 1.6));
                Stroke(Geometry.Parse("M8,6 L8,11"), 1.4);
                break;
            case Vector.Fan:
                var blade = Geometry.Parse("M8,8 C6.8,6.2 6.6,2.4 8.8,1.6 C11,0.8 11.6,3.6 9.6,6.4 C9.2,7 8.6,7.6 8,8 Z");
                var blades = new GeometryGroup();
                for (var i = 0; i < 4; i++)
                {
                    var copy = blade.Clone();
                    copy.Transform = new RotateTransform(i * 90, 8, 8);
                    blades.Children.Add(copy);
                }
                Fill(blades);
                Fill(new EllipseGeometry(new Point(8, 8), 1.5, 1.5));
                break;
            case Vector.Bulb or Vector.BulbLit:
                var bulb = Geometry.Parse("M5.8,10.6 C4.4,9.6 3.6,8.2 3.6,6.6 A4.4,4.4 0 0 1 12.4,6.6 C12.4,8.2 11.6,9.6 10.2,10.6 L10.2,11.8 L5.8,11.8 Z");
                if (icon == Vector.BulbLit) Fill(bulb);
                Stroke(bulb, 1.3);
                Stroke(Geometry.Parse("M6.2,13.6 L9.8,13.6 M6.9,15.2 L9.1,15.2"), 1.3);
                break;
        }
        return Boxed(canvas, size);
    }

    /// <summary>A small dial for a speed profile (the Mac's tortoise, walker, runner and hare): the needle swings
    /// from left (Silent) to right (Ludicrous).</summary>
    public static FrameworkElement Speed(SpeedLevel level, double size, Brush brush)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M2.6,12 A5.6,5.6 0 1 1 13.4,12"), Stroke = brush, StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var angle = level switch { SpeedLevel.Silent => -62, SpeedLevel.Standard => -22, SpeedLevel.Sport => 22, _ => 62 } * Math.PI / 180;
        var tip = new Point(8 + 4.4 * Math.Sin(angle), 10.2 - 4.4 * Math.Cos(angle));
        canvas.Children.Add(new Path
        {
            Data = new LineGeometry(new Point(8, 10.2), tip), Stroke = brush, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        canvas.Children.Add(new Path { Data = new EllipseGeometry(new Point(8, 10.2), 1.5, 1.5), Fill = brush });
        return Boxed(canvas, size);
    }

    private static FrameworkElement Boxed(Canvas canvas, double size) => new SilentViewbox
    {
        Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = false,
    };

    /// <summary>Text screen readers skip: icon glyphs, which would otherwise be read as private-use characters.</summary>
    private sealed class SilentText : TextBlock
    {
        protected override AutomationPeer OnCreateAutomationPeer() => null!;
    }

    private sealed class SilentViewbox : Viewbox
    {
        protected override AutomationPeer OnCreateAutomationPeer() => null!;
    }
}
