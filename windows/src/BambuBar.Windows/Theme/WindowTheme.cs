using System.Windows;
using System.Windows.Media;

namespace BambuBar.Theme;

/// <summary>Resource keys only the main window uses, on top of <see cref="ThemeKeys"/>: Windows 11's window surfaces
/// (Mica fallback, the content layer, the navigation pane) and its input controls (text box, toggle switch, radio).</summary>
public static class WindowKeys
{
    /// <summary>The window without Mica (Windows 10, high contrast, snapshots): SolidBackgroundFillColorBase.</summary>
    public const string WindowBackground = "Bb.Window.Background";
    /// <summary>The page area, a layer over the backdrop with a rounded top-left corner (LayerFillColorDefault).</summary>
    public const string LayerFill = "Bb.Layer.Fill";
    public const string LayerStroke = "Bb.Layer.Stroke";
    /// <summary>Navigation items: hover, selected, pressed (Fluent subtle fills).</summary>
    public const string NavHover = "Bb.Nav.Hover";
    public const string NavSelected = "Bb.Nav.Selected";
    public const string NavPressed = "Bb.Nav.Pressed";
    /// <summary>Text boxes: rest and focused fill, the bottom line at rest (ControlStrongStroke).</summary>
    public const string FieldFill = "Bb.Field.Fill";
    public const string FieldFocused = "Bb.Field.Focused";
    public const string StrongStroke = "Bb.Stroke.Strong";
    /// <summary>A switch or radio that is off (ControlAltFillColorSecondary).</summary>
    public const string OffFill = "Bb.Off.Fill";
}

/// <summary>The window's dictionary: the panel's (<see cref="ThemeResources.Create"/>) plus <see cref="WindowKeys"/>.</summary>
public static class WindowTheme
{
    public static ResourceDictionary Create(ThemeVariant variant)
    {
        var d = ThemeResources.Create(variant);
        void Put(string key, Color color) => d[key] = Palette.Brush(color);
        static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        switch (variant)
        {
            case ThemeVariant.Light:
                Put(WindowKeys.WindowBackground, C(0xFFF3F3F3));
                Put(WindowKeys.LayerFill, C(0x80FFFFFF));
                Put(WindowKeys.LayerStroke, C(0x0F000000));
                Put(WindowKeys.NavHover, C(0x09000000));
                Put(WindowKeys.NavSelected, C(0x0F000000));
                Put(WindowKeys.NavPressed, C(0x06000000));
                Put(WindowKeys.FieldFill, C(0xB3FFFFFF));
                Put(WindowKeys.FieldFocused, C(0xFFFFFFFF));
                Put(WindowKeys.StrongStroke, C(0x72000000));
                Put(WindowKeys.OffFill, C(0x06000000));
                break;
            case ThemeVariant.Dark:
                Put(WindowKeys.WindowBackground, C(0xFF202020));
                Put(WindowKeys.LayerFill, C(0x4C3A3A3A));
                Put(WindowKeys.LayerStroke, C(0x19000000));
                Put(WindowKeys.NavHover, C(0x0FFFFFFF));
                Put(WindowKeys.NavSelected, C(0x15FFFFFF));
                Put(WindowKeys.NavPressed, C(0x0AFFFFFF));
                Put(WindowKeys.FieldFill, C(0x0FFFFFFF));
                Put(WindowKeys.FieldFocused, C(0xB31E1E1E));
                Put(WindowKeys.StrongStroke, C(0x8BFFFFFF));
                Put(WindowKeys.OffFill, C(0x19000000));
                break;
            default:
                Put(WindowKeys.WindowBackground, SystemColors.WindowColor);
                Put(WindowKeys.LayerFill, SystemColors.WindowColor);
                Put(WindowKeys.LayerStroke, SystemColors.WindowTextColor);
                Put(WindowKeys.NavHover, SystemColors.WindowColor);
                // The accent indicator marks the selection; a highlight fill would need highlight-coloured text.
                Put(WindowKeys.NavSelected, SystemColors.WindowColor);
                Put(WindowKeys.NavPressed, SystemColors.WindowColor);
                Put(WindowKeys.FieldFill, SystemColors.WindowColor);
                Put(WindowKeys.FieldFocused, SystemColors.WindowColor);
                Put(WindowKeys.StrongStroke, SystemColors.WindowTextColor);
                Put(WindowKeys.OffFill, SystemColors.WindowColor);
                break;
        }
        return d;
    }
}
