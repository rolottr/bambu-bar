using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuBar.Core.Model;
using BambuBar.Tray;

namespace BambuBar;

/// <summary>
/// <c>BambuBar.exe --snapshot &lt;dir&gt;</c> (P3.13): renders the app's pieces offscreen to PNGs, for CI artifacts
/// and review without a screen. For now the tray glyphs; pages and the panel join as they are written.
/// </summary>
public static class Snapshot
{
    public static string? Folder(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--snapshot");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        Save(TraySheet(), Path.Combine(folder, "tray-glyphs.png"));
        return 0;
    }

    /// <summary>Every look × 16/20/24/32 px on a dark and a light taskbar, magnified 4× without smoothing.</summary>
    private static BitmapSource TraySheet()
    {
        (string Name, TrayGlyph.Look Look)[] looks =
        [
            ("not set up", new(null, 0, false, false)),
            ("idle", new(JobState.Idle, 0, false, false)),
            ("preparing", new(JobState.Preparing, 0, false, false)),
            ("printing 5%", new(JobState.Printing, 0.05, false, false)),
            ("printing 50%", new(JobState.Printing, 0.5, false, false)),
            ("printing 95%", new(JobState.Printing, 0.95, false, false)),
            ("paused", new(JobState.Paused, 0.6, false, false)),
            ("paused + alert", new(JobState.Paused, 0.6, true, false)),
            ("printing + alert", new(JobState.Printing, 0.4, true, false)),
            ("finished", new(JobState.Finished, 1, false, false)),
            ("failed", new(JobState.Failed, 0.3, true, false)),
            ("offline", new(null, 0, false, true)),
            ("show % 64", new(JobState.Printing, 0.64, false, false, "64")),
            ("show % 100", new(JobState.Printing, 1, false, false, "100")),
        ];
        int[] sizes = [16, 20, 24, 32];
        const int zoom = 4, label = 150, cell = 32 * zoom + 12;
        var width = label + sizes.Length * 2 * cell;
        var height = 40 + looks.Length * cell;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var face = new Typeface("Segoe UI");
            void Text(string s, double x, double y) => dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 13, Brushes.Black, 1.0), new Point(x, y));
            for (var t = 0; t < 2; t++)
            {
                var light = t == 1;
                for (var z = 0; z < sizes.Length; z++)
                    Text($"{sizes[z]} px {(light ? "light" : "dark")}", label + (t * sizes.Length + z) * cell + 4, 12);
            }
            for (var r = 0; r < looks.Length; r++)
            {
                var y = 40 + r * cell;
                Text(looks[r].Name, 8, y + cell / 2.0 - 9);
                for (var t = 0; t < 2; t++)
                {
                    var light = t == 1;
                    for (var z = 0; z < sizes.Length; z++)
                    {
                        var x = label + (t * sizes.Length + z) * cell;
                        dc.DrawRectangle(new SolidColorBrush(light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20)), null,
                            new Rect(x, y, cell - 6, cell - 6));
                        var glyph = TrayGlyph.Render(looks[r].Look, sizes[z], light);
                        var image = new ImageDrawing(glyph, new Rect(x + 4, y + 4, sizes[z] * zoom, sizes[z] * zoom));
                        var group = new DrawingGroup();
                        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                        group.Children.Add(image);
                        dc.DrawDrawing(group);
                    }
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
