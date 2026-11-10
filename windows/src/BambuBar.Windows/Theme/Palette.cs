using System.Globalization;
using System.Windows.Media;
using BambuBar.Core.Model;

namespace BambuBar.Theme;

/// <summary>
/// The Mac's colours (Palette in Sources/Shared/Formatting.swift; the same values as Core's WidgetPalette), plus
/// <see cref="Ink"/>: the Mac draws state colours as text in light mode too, where the bright greens and ambers fall
/// under 3:1 on white. On Windows, text in a state colour is darkened in the light theme; fills and dots keep the
/// Mac's values.
/// </summary>
public static class Palette
{
    /// <summary>Bambu-ish green, a touch brighter so it reads on dark glass (0.13, 0.80, 0.42).</summary>
    public static readonly Color Accent = Color.FromRgb(0x21, 0xCC, 0x6B);
    /// <summary>(0.00, 0.60, 0.29): prominent buttons.</summary>
    public static readonly Color AccentDeep = Color.FromRgb(0x00, 0x99, 0x4A);
    public static readonly Color Preparing = Color.FromRgb(0x5C, 0xA8, 0xFF);
    public static readonly Color Paused = Color.FromRgb(0xFF, 0xB8, 0x3D);
    public static readonly Color Failed = Color.FromRgb(0xFF, 0x5C, 0x5C);
    public static readonly Color Finished = Color.FromRgb(0x6B, 0xDB, 0x9E);
    /// <summary>(0.62, 0.66, 0.70): an idle printer, a canceled job.</summary>
    public static readonly Color Idle = Color.FromRgb(0x9E, 0xA8, 0xB3);
    public static readonly Color Nozzle = Color.FromRgb(0xFF, 0x7A, 0x4D);
    public static readonly Color Bed = Color.FromRgb(0xFF, 0xB8, 0x4D);
    public static readonly Color Chamber = Color.FromRgb(0x8C, 0xB3, 0xFF);
    /// <summary>SwiftUI's yellow, for the chamber light when it's on.</summary>
    public static readonly Color Lamp = Color.FromRgb(0xFF, 0xCC, 0x00);

    public static Color For(JobState state) => state switch
    {
        JobState.Idle => Idle,
        JobState.Preparing => Preparing,
        JobState.Printing => Accent,
        JobState.Paused => Paused,
        JobState.Finished => Finished,
        _ => Failed,
    };

    /// <summary>A canceled job reads as idle, not as a failure.</summary>
    public static Color For(PrinterStatus status) =>
        status.State == JobState.Failed && status.Canceled ? Idle : For(status.State);

    /// <summary><c>RRGGBB</c> or <c>RRGGBBAA</c>; fully transparent (an unset slot) is null, and faint colours get at
    /// least 35 % opacity, as on the Mac.</summary>
    public static Color? FromBambu(string? hex)
    {
        var clean = new string((hex ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (clean.Length is not (6 or 8) || !ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        byte r, g, b, a;
        if (clean.Length == 8) { r = (byte)(v >> 24); g = (byte)(v >> 16); b = (byte)(v >> 8); a = (byte)v; }
        else { r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v; a = 255; }
        if (a == 0) return null;
        return Color.FromArgb(Math.Max(a, (byte)89), r, g, b);
    }

    /// <summary>Perceived brightness of a filament colour, 0–1 (the Mac's bambuLuminance).</summary>
    public static double Luminance(string? hex)
    {
        var clean = new string((hex ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (clean.Length < 6 || !uint.TryParse(clean[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return 0.5;
        return 0.299 * ((v >> 16) & 0xFF) / 255 + 0.587 * ((v >> 8) & 0xFF) / 255 + 0.114 * (v & 0xFF) / 255;
    }

    /// <summary>A state colour as text: as is on dark backgrounds, darkened to about 4.5:1 on light ones.</summary>
    public static Color Ink(Color color, bool dark) =>
        dark ? color : Color.FromRgb((byte)(color.R * 0.6), (byte)(color.G * 0.6), (byte)(color.B * 0.6));

    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
