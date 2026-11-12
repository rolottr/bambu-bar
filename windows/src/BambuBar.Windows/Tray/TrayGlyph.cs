using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Tray;

/// <summary>
/// The tray icon: a small printer whose chamber fills with the print, a pause or alert mark when needed, a dashed
/// outline while offline. The Mac's MenuBarIcon geometry (18 × 18 units, y down), drawn in one colour for the
/// taskbar's theme. In "show %" mode the percentage is drawn instead of the printer, as Windows can't put text beside
/// a tray icon (ledger D2), inside a ring that fills with the print in Bambu green.
/// </summary>
public static class TrayGlyph
{
    public sealed record Look(JobState? State, double Progress, bool Alert, bool Offline, string? Percent = null);

    /// <summary>The look for a printer: what the Mac's MenuBarLabel passes to MenuBarIcon.image.</summary>
    public static Look For(Core.App.PrinterSession? session, bool configured, bool showPercent)
    {
        var s = session?.Status;
        var online = session?.IsOnline ?? false;
        var state = online ? (s?.Canceled == true ? JobState.Idle : s?.State) : null;
        var active = online && (s?.State.IsActive() ?? false);
        return new Look(state, (s?.Progress ?? 0) / 100.0,
            online && (s?.MostSevereIssue?.Severity ?? IssueSeverity.Info) <= IssueSeverity.Serious,
            configured && !online,
            showPercent && active && s!.State != JobState.Paused ? $"{s.Progress}" : null);
    }

    private static readonly Dictionary<(Look, int, bool), BitmapSource> Cache = [];

    /// <summary>The glyph at <paramref name="pixels"/> × <paramref name="pixels"/>, white for a dark taskbar or black for
    /// a light one, transparent around it. Progress is drawn in 5 % steps, as on the Mac.</summary>
    public static BitmapSource Render(Look look, int pixels, bool lightTaskbar)
    {
        var step = (int)Math.Round(look.Progress * 20);
        var key = (look with { Progress = step / 20.0 }, pixels, lightTaskbar);
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var ink = new SolidColorBrush(lightTaskbar ? Colors.Black : Colors.White);
        ink.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 18.0, pixels / 18.0));
            if (look.Percent is { } percent) DrawPercent(dc, percent, step / 20.0, ink, lightTaskbar);
            else Draw(dc, look.State, step / 20.0, look.Alert, look.Offline, ink);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Cache[key] = bitmap;
        return bitmap;
    }

    private static void Draw(DrawingContext dc, JobState? state, double fill, bool alert, bool offline, Brush ink)
    {
        var frame = new Rect(2.25, 1.75, 13.5, 14.5);
        var badge = new Rect(11.2, 0.2, 6.6, 6.6);
        var cutBadge = alert && state != JobState.Failed && !offline;
        // The badge is cut out of everything under it (the Mac clears a ring around it).
        if (cutBadge)
        {
            var around = new RectangleGeometry(new Rect(0, 0, 18, 18));
            var hole = new EllipseGeometry(Rect.Inflate(badge, 1.2, 1.2));
            dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, around, hole));
        }

        var pen = new Pen(ink, 1.5);
        if (offline) pen.DashStyle = new DashStyle([2.2 / 1.5, 1.6 / 1.5], 0);
        dc.DrawRoundedRectangle(null, pen, frame, 2.6, 2.6);
        if (!offline)
        {
            // Gantry and build plate.
            const double gantryY = 5.2, plateY = 13.4;
            dc.DrawRectangle(ink, null, new Rect(frame.Left + 1, gantryY - 0.4, frame.Width - 2, 0.8));
            dc.DrawRoundedRectangle(ink, null, new Rect(frame.Left + 2.2, plateY, frame.Width - 4.4, 1.3), 0.6, 0.6);
            switch (state)
            {
                case JobState.Paused:
                    foreach (var x in new[] { 6.6, 9.9 }) dc.DrawRoundedRectangle(ink, null, new Rect(x, 7.2, 1.6, 4.8), 0.8, 0.8);
                    break;
                case JobState.Failed:
                    Mark(dc, ink);
                    break;
                case JobState.Printing or JobState.Preparing or JobState.Finished:
                    var maxH = plateY - gantryY - 2.6;
                    var h = state == JobState.Finished ? maxH : Math.Max(0.9, maxH * fill);
                    dc.DrawRoundedRectangle(ink, null, new Rect(6.2, plateY - h, 5.6, h), 0.9, 0.9);
                    if (state != JobState.Finished)
                    {
                        // Toolhead on top of the part.
                        var headY = Math.Max(gantryY + 0.6, plateY - h - 2.3);
                        dc.DrawRoundedRectangle(ink, null, new Rect(7.3, headY, 3.4, 1.8), 0.5, 0.5);
                    }
                    break;
                default:
                    // Idle: the toolhead parked at the side.
                    dc.DrawRoundedRectangle(ink, null, new Rect(frame.Left + 1.6, gantryY + 0.6, 3.2, 1.8), 0.5, 0.5);
                    break;
            }
        }
        if (cutBadge)
        {
            dc.Pop();
            // The badge, with an exclamation mark cut out of it.
            var exclamation = new GeometryGroup();
            exclamation.Children.Add(new RectangleGeometry(new Rect(badge.X + badge.Width / 2 - 0.6, badge.Top + 1.3, 1.2, 2.6), 0.6, 0.6));
            exclamation.Children.Add(new EllipseGeometry(new Rect(badge.X + badge.Width / 2 - 0.65, badge.Top + 4.4, 1.3, 1.3)));
            dc.DrawGeometry(ink, null, new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(badge), exclamation));
        }
    }

    private static void Mark(DrawingContext dc, Brush ink)
    {
        dc.DrawRoundedRectangle(ink, null, new Rect(8.2, 6.6, 1.6, 4.4), 0.8, 0.8);
        dc.DrawEllipse(ink, null, new Point(9.0, 12.7), 0.9, 0.9);
    }

    /// <summary>The percentage inside a progress ring: a faint track all round, the done part in Bambu green from
    /// the top, clockwise; two digits as large as fit inside, "100" a little smaller.</summary>
    private static void DrawPercent(DrawingContext dc, string percent, double fraction, Brush ink, bool lightTaskbar)
    {
        const double stroke = 1.9;
        var c = new Point(9, 9);
        var r = 9 - stroke / 2 - 0.1;
        var track = new Pen(new SolidColorBrush(lightTaskbar ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), stroke);
        dc.DrawEllipse(null, track, c, r, r);
        var green = lightTaskbar ? Color.FromRgb(0x00, 0x99, 0x4A) : Color.FromRgb(0x21, 0xCC, 0x6B);
        var arc = new Pen(new SolidColorBrush(green), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (fraction >= 0.999) dc.DrawEllipse(null, arc, c, r, r);
        else if (fraction > 0)
        {
            var angle = Math.Max(fraction, 0.03) * 2 * Math.PI;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
                ctx.ArcTo(new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle)), new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, arc, g);
        }
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
        var size = percent.Length >= 3 ? 7.0 : 9.6;
        var text = new FormattedText(percent, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, ink, 1.0)
        {
            TextAlignment = TextAlignment.Center,
        };
        // Centred on the digits' ink (the line box has room for descenders below them): the ink ends OverhangAfter
        // from the line's bottom and is Extent tall.
        dc.DrawText(text, new Point(9, c.Y - text.Height - text.OverhangAfter + text.Extent / 2));
    }
}
