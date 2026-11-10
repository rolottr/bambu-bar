using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BambuBar.Theme;

/// <summary>How a <see cref="ChromeButton"/> fills at rest, on hover and pressed: theme keys, or tinted brushes.</summary>
public sealed record ButtonLook(string? Rest, string? Hover, string? Pressed, string? Stroke = null)
{
    /// <summary>Transparent until hovered (Fluent's subtle button): header icons, footer, list rows.</summary>
    public static readonly ButtonLook Subtle = new(null, ThemeKeys.SubtleHover, ThemeKeys.SubtlePressed);
    /// <summary>A standard button with a hairline.</summary>
    public static readonly ButtonLook Standard = new(ThemeKeys.ControlFill, ThemeKeys.ControlHover, ThemeKeys.ControlPressed, ThemeKeys.ControlStroke);
    /// <summary>The prominent one (the Mac's accentDeep gradient).</summary>
    public static readonly ButtonLook Accent = new(ThemeKeys.AccentFill, ThemeKeys.AccentHover, ThemeKeys.AccentPressed);
    /// <summary>A card-like row (the Mac's .panel on a button).</summary>
    public static readonly ButtonLook Card = new(ThemeKeys.CardFill, ThemeKeys.ControlHover, ThemeKeys.ControlPressed, ThemeKeys.CardStroke);

    /// <summary>Explicit brushes, for tinted buttons (Stop in the failure colour, a row that needs attention).</summary>
    public Brush? RestBrush { get; init; }
    public Brush? HoverBrush { get; init; }
    public Brush? PressedBrush { get; init; }

    public static ButtonLook Tinted(Color tint, double rest = 0.12, double hover = 0.18, double pressed = 0.24) => new(null, null, null, ThemeKeys.ControlStroke)
    {
        RestBrush = Palette.Brush(Palette.WithAlpha(tint, rest)),
        HoverBrush = Palette.Brush(Palette.WithAlpha(tint, hover)),
        PressedBrush = Palette.Brush(Palette.WithAlpha(tint, pressed)),
    };
}

/// <summary>
/// A button drawn the Fluent way: a rounded fill that changes on hover and press, no bevel, and the system's two-tone
/// keyboard focus rectangle. Content is whatever the caller puts in (text, icon, both).
/// </summary>
public class ChromeButton : Button
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(ChromeButton), new FrameworkPropertyMetadata(Metrics.Control));

    private static readonly ControlTemplate SharedTemplate = Chrome.Template(typeof(ChromeButton), CornerRadiusProperty);

    private ButtonLook _look = ButtonLook.Subtle;

    public ChromeButton()
    {
        Template = SharedTemplate;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(8, 4, 8, 4);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SetResourceReference(ForegroundProperty, ThemeKeys.TextPrimary);
        FocusVisualStyle = Chrome.FocusVisual(Metrics.Control);
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.4;
        Chrome.Apply(this, _look, false, false);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set
        {
            SetValue(CornerRadiusProperty, value);
            FocusVisualStyle = Chrome.FocusVisual(value);
        }
    }

    public ButtonLook Look
    {
        get => _look;
        set { _look = value; Refresh(); }
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsPressedChanged(e); Refresh(); }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Refresh(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Refresh(); }

    private void Refresh() => Chrome.Apply(this, _look, IsMouseOver, IsPressed);
}

/// <summary>A <see cref="ChromeButton"/> that stays on: the all-printers switch, the chamber light.</summary>
public class ChromeToggle : ToggleButton
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(ChromeToggle), new FrameworkPropertyMetadata(Metrics.Control));

    private static readonly ControlTemplate SharedTemplate = Chrome.Template(typeof(ChromeToggle), CornerRadiusProperty);

    public ChromeToggle()
    {
        Template = SharedTemplate;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(8, 4, 8, 4);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SetResourceReference(ForegroundProperty, ThemeKeys.TextPrimary);
        FocusVisualStyle = Chrome.FocusVisual(Metrics.Control);
        Checked += (_, _) => Refresh();
        Unchecked += (_, _) => Refresh();
        Refresh();
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set
        {
            SetValue(CornerRadiusProperty, value);
            FocusVisualStyle = Chrome.FocusVisual(value);
        }
    }

    /// <summary>Unchecked look.</summary>
    public ButtonLook Look { get; set => Set(ref field, value); } = ButtonLook.Subtle;

    /// <summary>Checked look.</summary>
    public ButtonLook CheckedLook { get; set => Set(ref field, value); } = new(ThemeKeys.ControlSelected, ThemeKeys.ControlSelected, ThemeKeys.SubtlePressed);

    private void Set(ref ButtonLook field, ButtonLook value)
    {
        field = value;
        Refresh();
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsPressedChanged(e); Refresh(); }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Refresh(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Refresh(); }

    private void Refresh() => Chrome.Apply(this, IsChecked == true ? CheckedLook : Look, IsMouseOver, IsPressed);
}

/// <summary>The template and states shared by the chrome buttons.</summary>
internal static class Chrome
{
    public static ControlTemplate Template(Type owner, DependencyProperty cornerRadius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new TemplateBindingExtension(cornerRadius));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(content);
        var template = new ControlTemplate(owner) { VisualTree = border };
        template.Seal();
        return template;
    }

    public static void Apply(Control control, ButtonLook look, bool hover, bool pressed)
    {
        var (key, brush) = pressed ? (look.Pressed, look.PressedBrush) : hover ? (look.Hover, look.HoverBrush) : (look.Rest, look.RestBrush);
        // A local value replaces an earlier resource reference, and the other way round.
        if (brush is not null) control.Background = brush;
        else if (key is not null) control.SetResourceReference(Control.BackgroundProperty, key);
        else control.Background = Brushes.Transparent;
        if (look.Stroke is not null)
        {
            control.SetResourceReference(Control.BorderBrushProperty, look.Stroke);
            control.BorderThickness = new Thickness(1);
        }
        else control.BorderThickness = new Thickness(0);
    }

    private static readonly Dictionary<double, Style> FocusVisuals = [];

    /// <summary>Windows' focus rectangle: 2 px outer, 1 px inner, following the control's corners, keyboard only.</summary>
    public static Style FocusVisual(CornerRadius radius)
    {
        if (FocusVisuals.TryGetValue(radius.TopLeft, out var cached)) return cached;
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Min(radius.TopLeft, 100) + 3));
        outer.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.FocusOuter);
        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Min(radius.TopLeft, 100) + 1));
        inner.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.FocusInner);
        outer.AppendChild(inner);
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = outer }));
        style.Seal();
        FocusVisuals[radius.TopLeft] = style;
        return style;
    }
}
