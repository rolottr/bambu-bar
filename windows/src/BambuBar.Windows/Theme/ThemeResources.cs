using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace BambuBar.Theme;

/// <summary>
/// Builds the theme dictionaries: brushes for <see cref="ThemeVariant"/> under <see cref="ThemeKeys"/>, the fonts, and
/// implicit styles for tooltips and scroll bars. Built in code rather than loose XAML so a typo fails the build, not a
/// launch (nobody runs this app on a Mac). Dictionaries are rebuilt when the theme changes; brushes are frozen.
/// </summary>
public static class ThemeResources
{
    /// <summary>Problems building optional parts (the scroll bar style); <c>--snapshot</c> reports them.</summary>
    public static List<string> Errors { get; } = [];

    public static bool IsDark(ThemeVariant variant) => variant == ThemeVariant.Dark;

    public static ResourceDictionary Create(ThemeVariant variant)
    {
        var d = new ResourceDictionary();
        void Put(string key, Color color) => d[key] = Palette.Brush(color);
        static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        switch (variant)
        {
            case ThemeVariant.Light:
                Put(ThemeKeys.TextPrimary, C(0xE4000000));
                Put(ThemeKeys.TextSecondary, C(0x9E000000));
                Put(ThemeKeys.TextTertiary, C(0x72000000));
                Put(ThemeKeys.TextOnAccent, C(0xFFFFFFFF));
                Put(ThemeKeys.TextLink, Palette.Ink(Palette.Accent, dark: false));
                Put(ThemeKeys.PanelBackground, C(0xFFF3F3F3));
                Put(ThemeKeys.PanelStroke, C(0x26000000));
                Put(ThemeKeys.CardFill, C(0xB3FFFFFF));
                Put(ThemeKeys.CardStroke, C(0x0F000000));
                Put(ThemeKeys.SubtleHover, C(0x09000000));
                Put(ThemeKeys.SubtlePressed, C(0x06000000));
                Put(ThemeKeys.ControlFill, C(0xB3FFFFFF));
                Put(ThemeKeys.ControlHover, C(0x80F9F9F9));
                Put(ThemeKeys.ControlPressed, C(0x4DF9F9F9));
                Put(ThemeKeys.ControlStroke, C(0x17000000));
                Put(ThemeKeys.ControlSelected, Palette.WithAlpha(Palette.Accent, 0.18));
                Put(ThemeKeys.Track, C(0x17000000));
                Put(ThemeKeys.Divider, C(0x14000000));
                Put(ThemeKeys.FlyoutFill, C(0xFFFCFCFC));
                Put(ThemeKeys.FlyoutStroke, C(0x1F000000));
                Put(ThemeKeys.FocusOuter, C(0xE4000000));
                Put(ThemeKeys.FocusInner, C(0xB3FFFFFF));
                Put(ThemeKeys.AccentFill, C(0xFF007A3D));
                Put(ThemeKeys.AccentHover, C(0xE6007A3D));
                Put(ThemeKeys.AccentPressed, C(0xCC007A3D));
                break;
            case ThemeVariant.Dark:
                Put(ThemeKeys.TextPrimary, C(0xFFFFFFFF));
                Put(ThemeKeys.TextSecondary, C(0xC5FFFFFF));
                Put(ThemeKeys.TextTertiary, C(0x87FFFFFF));
                Put(ThemeKeys.TextOnAccent, C(0xE4000000));
                Put(ThemeKeys.TextLink, Palette.Accent);
                Put(ThemeKeys.PanelBackground, C(0xFF2C2C2C));
                Put(ThemeKeys.PanelStroke, C(0x1FFFFFFF));
                Put(ThemeKeys.CardFill, C(0x0DFFFFFF));
                Put(ThemeKeys.CardStroke, C(0x14FFFFFF));
                Put(ThemeKeys.SubtleHover, C(0x0FFFFFFF));
                Put(ThemeKeys.SubtlePressed, C(0x0AFFFFFF));
                Put(ThemeKeys.ControlFill, C(0x0FFFFFFF));
                Put(ThemeKeys.ControlHover, C(0x15FFFFFF));
                Put(ThemeKeys.ControlPressed, C(0x08FFFFFF));
                Put(ThemeKeys.ControlStroke, C(0x12FFFFFF));
                Put(ThemeKeys.ControlSelected, Palette.WithAlpha(Palette.Accent, 0.22));
                Put(ThemeKeys.Track, C(0x17FFFFFF));
                Put(ThemeKeys.Divider, C(0x15FFFFFF));
                Put(ThemeKeys.FlyoutFill, C(0xFF2C2C2C));
                Put(ThemeKeys.FlyoutStroke, C(0x33000000));
                Put(ThemeKeys.FocusOuter, C(0xFFFFFFFF));
                Put(ThemeKeys.FocusInner, C(0xB3000000));
                Put(ThemeKeys.AccentFill, Palette.Accent);
                Put(ThemeKeys.AccentHover, Palette.WithAlpha(Palette.Accent, 0.9));
                Put(ThemeKeys.AccentPressed, Palette.WithAlpha(Palette.Accent, 0.8));
                break;
            default:
                // High contrast: the user's system colours, solid surfaces, outlines instead of tints.
                Put(ThemeKeys.TextPrimary, SystemColors.WindowTextColor);
                Put(ThemeKeys.TextSecondary, SystemColors.WindowTextColor);
                Put(ThemeKeys.TextTertiary, SystemColors.GrayTextColor);
                Put(ThemeKeys.TextOnAccent, SystemColors.HighlightTextColor);
                Put(ThemeKeys.TextLink, SystemColors.HotTrackColor);
                Put(ThemeKeys.PanelBackground, SystemColors.WindowColor);
                Put(ThemeKeys.PanelStroke, SystemColors.WindowTextColor);
                Put(ThemeKeys.CardFill, SystemColors.WindowColor);
                Put(ThemeKeys.CardStroke, SystemColors.WindowTextColor);
                Put(ThemeKeys.SubtleHover, SystemColors.WindowColor);
                Put(ThemeKeys.SubtlePressed, SystemColors.WindowColor);
                Put(ThemeKeys.ControlFill, SystemColors.WindowColor);
                Put(ThemeKeys.ControlHover, SystemColors.WindowColor);
                Put(ThemeKeys.ControlPressed, SystemColors.WindowColor);
                Put(ThemeKeys.ControlStroke, SystemColors.WindowTextColor);
                Put(ThemeKeys.ControlSelected, SystemColors.WindowColor);
                Put(ThemeKeys.Track, SystemColors.GrayTextColor);
                Put(ThemeKeys.Divider, SystemColors.WindowTextColor);
                Put(ThemeKeys.FlyoutFill, SystemColors.WindowColor);
                Put(ThemeKeys.FlyoutStroke, SystemColors.WindowTextColor);
                Put(ThemeKeys.FocusOuter, SystemColors.HighlightColor);
                Put(ThemeKeys.FocusInner, SystemColors.WindowColor);
                Put(ThemeKeys.AccentFill, SystemColors.HighlightColor);
                Put(ThemeKeys.AccentHover, SystemColors.HighlightColor);
                Put(ThemeKeys.AccentPressed, SystemColors.HighlightColor);
                break;
        }

        // Segoe UI Variable on Windows 11; WPF falls back along the list (Segoe UI on Windows 10).
        d[ThemeKeys.FontText] = new FontFamily("Segoe UI Variable Text, Segoe UI");
        d[ThemeKeys.FontDisplay] = new FontFamily("Segoe UI Variable Display, Segoe UI");
        d[ThemeKeys.FontIcons] = Icons.Font;
        d[ThemeKeys.FontMono] = new FontFamily("Cascadia Mono, Consolas");

        d[typeof(ToolTip)] = ToolTipStyle();
        if (ScrollBarStyle() is { } scrollBar) d[typeof(ScrollBar)] = scrollBar;
        return d;
    }

    /// <summary>Fluent's tooltip: a small rounded flyout in the theme's colours.</summary>
    private static Style ToolTipStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 6));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);

        var style = new Style(typeof(ToolTip));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToolTip)) { VisualTree = border }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ThemeKeys.FlyoutFill)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ThemeKeys.FlyoutStroke)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeKeys.TextPrimary)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new DynamicResourceExtension(ThemeKeys.FontText)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(ToolTip.HasDropShadowProperty, true));
        return style;
    }

    /// <summary>A thin, rounded scroll bar like Windows 11's (the classic one looks out of place on glass). The
    /// track's parts are awkward to build in code, so this one piece is XAML.</summary>
    private static Style? ScrollBarStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
              <Setter Property="Width" Value="10" />
              <Setter Property="MinWidth" Value="10" />
              <Setter Property="Background" Value="Transparent" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent">
                      <Track x:Name="PART_Track" IsDirectionReversed="True">
                        <Track.Thumb>
                          <Thumb>
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Border x:Name="Bar" Width="3" Margin="0,4" CornerRadius="1.5" HorizontalAlignment="Center"
                                        Background="{DynamicResource Bb.Text.Tertiary}" />
                                <ControlTemplate.Triggers>
                                  <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="Bar" Property="Width" Value="6" />
                                    <Setter TargetName="Bar" Property="CornerRadius" Value="3" />
                                  </Trigger>
                                </ControlTemplate.Triggers>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                      </Track>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        try
        {
            return (Style)XamlReader.Parse(xaml);
        }
        catch (Exception e) when (e is XamlParseException or InvalidCastException or InvalidOperationException)
        {
            Errors.Add("ScrollBar style: " + e.Message);
            return null;
        }
    }
}
