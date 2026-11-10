using System.Windows;

namespace BambuBar.Theme;

/// <summary>
/// Resource keys of the theme dictionaries (<see cref="ThemeResources"/>). Code sets them with
/// <c>SetResourceReference</c>; XAML pages can use <c>{DynamicResource Bb.Text.Primary}</c>. Values follow Windows 11's
/// Fluent tokens where Windows has one (text, card, control, focus), and the Mac's palette for everything Bambu Bar's own.
/// </summary>
public static class ThemeKeys
{
    // Text (Fluent TextFillColorPrimary/Secondary/Tertiary).
    public const string TextPrimary = "Bb.Text.Primary";
    public const string TextSecondary = "Bb.Text.Secondary";
    public const string TextTertiary = "Bb.Text.Tertiary";
    public const string TextOnAccent = "Bb.Text.OnAccent";
    /// <summary>Links ("Wiki", "Support this project"): the accent as text.</summary>
    public const string TextLink = "Bb.Text.Link";

    // Surfaces.
    /// <summary>The panel without a backdrop: Windows 10, high contrast, snapshots.</summary>
    public const string PanelBackground = "Bb.Panel.Background";
    public const string PanelStroke = "Bb.Panel.Stroke";
    /// <summary>The Mac's module fill (primary at 5 %) = Fluent CardBackgroundFillColorDefault.</summary>
    public const string CardFill = "Bb.Card.Fill";
    public const string CardStroke = "Bb.Card.Stroke";
    /// <summary>Transparent controls on hover and press (Fluent SubtleFillColorSecondary/Tertiary).</summary>
    public const string SubtleHover = "Bb.Subtle.Hover";
    public const string SubtlePressed = "Bb.Subtle.Pressed";
    /// <summary>Standard buttons (Fluent ControlFillColorDefault/Secondary/Tertiary).</summary>
    public const string ControlFill = "Bb.Control.Fill";
    public const string ControlHover = "Bb.Control.Hover";
    public const string ControlPressed = "Bb.Control.Pressed";
    public const string ControlStroke = "Bb.Control.Stroke";
    /// <summary>Selected toggles (the all-printers switch).</summary>
    public const string ControlSelected = "Bb.Control.Selected";
    /// <summary>Progress and fan tracks (the Mac's primary at 9 %).</summary>
    public const string Track = "Bb.Track";
    public const string Divider = "Bb.Divider";
    /// <summary>Menus and tooltips (Fluent AcrylicInAppFillColorDefault, opaque here).</summary>
    public const string FlyoutFill = "Bb.Flyout.Fill";
    public const string FlyoutStroke = "Bb.Flyout.Stroke";
    public const string FocusOuter = "Bb.Focus.Outer";
    public const string FocusInner = "Bb.Focus.Inner";

    // Prominent buttons (the Mac's accentDeep gradient).
    public const string AccentFill = "Bb.Accent.Fill";
    public const string AccentHover = "Bb.Accent.Hover";
    public const string AccentPressed = "Bb.Accent.Pressed";

    // Fonts.
    public const string FontText = "Bb.Font.Text";
    public const string FontDisplay = "Bb.Font.Display";
    public const string FontIcons = "Bb.Font.Icons";
    public const string FontMono = "Bb.Font.Mono";
}

/// <summary>
/// The type ramp, in DIPs. The Mac's sizes (points) are in the comments. Segoe UI sets smaller than SF Pro at the same
/// size and Windows' smallest comfortable text is 11–12 px, so the Mac's 10–11.5 pt captions grow by about a point.
/// </summary>
public static class Ramp
{
    /// <summary>Tertiary labels: "layers", spool amounts (Mac 10).</summary>
    public const double Micro = 11;
    /// <summary>Pills, section labels, spool materials, fleet details (Mac 10.5).</summary>
    public const double Caption = 11;
    /// <summary>Header subtitle, tile titles (Mac 11).</summary>
    public const double Footnote = 12;
    /// <summary>Job subtitle, timing, card text (Mac 11.5).</summary>
    public const double Body = 12;
    /// <summary>Buttons, footer (Mac 12).</summary>
    public const double Control = 13;
    /// <summary>Printer name, job name (Mac 14).</summary>
    public const double Title = 14;
    /// <summary>Card titles: "Connect your printer", "Ready to print" (Mac 15).</summary>
    public const double CardTitle = 15;
    /// <summary>"Getting ready" (Mac 18, rounded).</summary>
    public const double Lead = 18;
    /// <summary>Temperatures (Mac 22, rounded).</summary>
    public const double Reading = 22;
    /// <summary>The percentage (Mac 34, rounded); the % sign is half of it.</summary>
    public const double Hero = 34;
}

/// <summary>Sizes, gaps and corner radii. Windows 11 rounds overlays at 8 and controls at 4; the Mac's larger, softer
/// radii (20 for the job card, 14 for tiles) are scaled toward those so the panel sits with the rest of Windows.</summary>
public static class Metrics
{
    /// <summary>The panel's width (Mac 372).</summary>
    public const double PanelWidth = 372;
    /// <summary>Around the panel's content (Mac 12).</summary>
    public const double PanelPadding = 12;
    /// <summary>Between sections (Mac 10).</summary>
    public const double Gap = 8;
    /// <summary>From the work area's edge (Windows 11 flyouts sit 12 px off the taskbar).</summary>
    public const double ScreenMargin = 12;

    /// <summary>The job card (Mac 20).</summary>
    public static readonly CornerRadius Hero = new(12);
    /// <summary>Cards: connection, setup, filament (Mac 18–20).</summary>
    public static readonly CornerRadius Card = new(8);
    /// <summary>Tiles, issue, toast, update row, list rows (Mac 12–14).</summary>
    public static readonly CornerRadius Tile = new(8);
    /// <summary>Buttons (Mac 11, Fluent ControlCornerRadius 4).</summary>
    public static readonly CornerRadius Control = new(4);
    /// <summary>Menus and tooltips (Fluent OverlayCornerRadius).</summary>
    public static readonly CornerRadius Overlay = new(8);
    public static readonly CornerRadius Pill = new(999);

    /// <summary>Header icon buttons (Mac 28, Fluent 32).</summary>
    public const double HeaderButton = 32;
    /// <summary>Print controls (Mac min height 34).</summary>
    public const double ControlHeight = 34;
    /// <summary>The printer beside the job (Mac 92 × 110).</summary>
    public const double ArtWidth = 92, ArtHeight = 110;
    /// <summary>The printer in the connection and setup cards (Mac 70 × 84).</summary>
    public const double SmallArtWidth = 70, SmallArtHeight = 84;
    /// <summary>The printer in the all-printers list (Mac 30 × 36).</summary>
    public const double RowArtWidth = 30, RowArtHeight = 36;
    /// <summary>Spool swatches in the filament strip (Mac 34).</summary>
    public const double Swatch = 34;

    /// <summary>Fluent's control motion: fast in, decelerating (167 ms) and the panel's entrance (200 ms).</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan Entrance = TimeSpan.FromMilliseconds(200);
}
