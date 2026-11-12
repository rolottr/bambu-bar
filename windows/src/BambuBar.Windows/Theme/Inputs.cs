using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BambuBar.Theme;

/// <summary>
/// Windows 11's toggle switch (the Mac's switch-style Toggle): a 40 × 20 pill, outlined with a small knob when off,
/// filled with the accent and a white knob when on. A ToggleButton underneath, so screen readers get the toggle
/// pattern and Space flips it.
/// </summary>
public sealed class ToggleSwitch : ToggleButton
{
    public static readonly DependencyProperty OnFillProperty = Brushed(nameof(OnFill));
    public static readonly DependencyProperty OffFillProperty = Brushed(nameof(OffFill));
    public static readonly DependencyProperty StrokeProperty = Brushed(nameof(Stroke));
    public static readonly DependencyProperty KnobOnProperty = Brushed(nameof(KnobOn));

    private static DependencyProperty Brushed(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(ToggleSwitch),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly ControlTemplate Empty = MakeTemplate();

    public ToggleSwitch()
    {
        Template = Empty;
        Width = 40;
        Height = 20;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(OnFillProperty, ThemeKeys.AccentFill);
        SetResourceReference(OffFillProperty, WindowKeys.OffFill);
        SetResourceReference(StrokeProperty, WindowKeys.StrongStroke);
        SetResourceReference(KnobOnProperty, ThemeKeys.TextOnAccent);
        FocusVisualStyle = Chrome.FocusVisual(new CornerRadius(10));
        Checked += (_, _) => InvalidateVisual();
        Unchecked += (_, _) => InvalidateVisual();
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : 0.4; InvalidateVisual(); };
    }

    public Brush? OnFill { get => (Brush?)GetValue(OnFillProperty); set => SetValue(OnFillProperty, value); }
    public Brush? OffFill { get => (Brush?)GetValue(OffFillProperty); set => SetValue(OffFillProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? KnobOn { get => (Brush?)GetValue(KnobOnProperty); set => SetValue(KnobOnProperty, value); }

    /// <summary>A switch named for screen readers (the row's title), on or off.</summary>
    public static ToggleSwitch For(string name, bool on, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsChecked = on };
        AutomationProperties.SetName(toggle, name);
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    private static ControlTemplate MakeTemplate()
    {
        // Transparent but hit-testable over the whole pill; the drawing is OnRender's.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var template = new ControlTemplate(typeof(ToggleSwitch)) { VisualTree = border };
        template.Seal();
        return template;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }
    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsPressedChanged(e); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var on = IsChecked == true;
        var rect = new Rect(0.5, 0.5, Width - 1, Height - 1);
        var r = rect.Height / 2;
        if (on) dc.DrawRoundedRectangle(OnFill, null, new Rect(0, 0, Width, Height), Height / 2, Height / 2);
        else dc.DrawRoundedRectangle(OffFill, new Pen(Stroke, 1), rect, r, r);
        // The knob grows on hover and stretches while pressed, as Windows' does.
        var knob = IsPressed ? 7 : IsMouseOver ? 7 : 6;
        var stretch = IsPressed ? 3 : 0;
        var cy = Height / 2;
        var cx = on ? Width - 10 - stretch / 2.0 : 10 + stretch / 2.0;
        var brush = on ? KnobOn : Stroke;
        dc.DrawRoundedRectangle(brush, null, new Rect(cx - knob - stretch / 2.0, cy - knob, 2 * knob + stretch, 2 * knob), knob, knob);
    }
}

/// <summary>Windows 11's radio button: a 20 px ring that becomes a thick accent ring around a white dot.</summary>
public sealed class RadioOption : RadioButton
{
    private static readonly ControlTemplate SharedTemplate = MakeTemplate();
    private readonly RadioMark _mark;

    public RadioOption(string text, string group)
    {
        Template = SharedTemplate;
        GroupName = group;
        _mark = new RadioMark(this);
        var label = Ui.Text(text, 14);
        label.Margin = new Thickness(8, 0, 0, 1);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _mark, label } };
        AutomationProperties.SetName(this, text);
        Cursor = Cursors.Hand;
        Padding = new Thickness(0, 4, 0, 4);
        FocusVisualStyle = Chrome.FocusVisual(Metrics.Control);
        Checked += (_, _) => _mark.InvalidateVisual();
        Unchecked += (_, _) => _mark.InvalidateVisual();
        MouseEnter += (_, _) => _mark.InvalidateVisual();
        MouseLeave += (_, _) => _mark.InvalidateVisual();
    }

    private static ControlTemplate MakeTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(RadioOption)) { VisualTree = border };
        template.Seal();
        return template;
    }

    private sealed class RadioMark : FrameworkElement
    {
        public static readonly DependencyProperty OnProperty = DependencyProperty.Register("On", typeof(Brush), typeof(RadioMark),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty OffProperty = DependencyProperty.Register("Off", typeof(Brush), typeof(RadioMark),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register("Stroke", typeof(Brush), typeof(RadioMark),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty DotProperty = DependencyProperty.Register("Dot", typeof(Brush), typeof(RadioMark),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private readonly RadioOption _owner;

        public RadioMark(RadioOption owner)
        {
            _owner = owner;
            Width = Height = 20;
            VerticalAlignment = VerticalAlignment.Center;
            SetResourceReference(OnProperty, ThemeKeys.AccentFill);
            SetResourceReference(OffProperty, WindowKeys.OffFill);
            SetResourceReference(StrokeProperty, WindowKeys.StrongStroke);
            SetResourceReference(DotProperty, ThemeKeys.TextOnAccent);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = new Point(10, 10);
            if (_owner.IsChecked == true)
            {
                dc.DrawEllipse((Brush)GetValue(OnProperty), null, c, 10, 10);
                var dot = _owner.IsMouseOver ? 5 : 4;
                dc.DrawEllipse((Brush)GetValue(DotProperty), null, c, dot, dot);
            }
            else
            {
                dc.DrawEllipse((Brush)GetValue(OffProperty), new Pen((Brush)GetValue(StrokeProperty), 1), c, 9.5, 9.5);
            }
        }
    }
}

/// <summary>Windows 11's text box: a rounded field that brightens and gets an accent underline while typing. Wraps a
/// plain TextBox (or PasswordBox for access codes and license keys).</summary>
public sealed class TextField : Border
{
    private readonly Border _underline = new() { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 4, 4) };

    public TextBox? Box { get; }
    public PasswordBox? Secret { get; }

    public TextField(string name, bool secret = false, double width = double.NaN)
    {
        CornerRadius = Metrics.Control;
        BorderThickness = new Thickness(1);
        Width = width;
        MinHeight = 32;
        SnapsToDevicePixels = true;
        SetResourceReference(BorderBrushProperty, ThemeKeys.ControlStroke);
        Control input;
        if (secret)
        {
            Secret = new PasswordBox();
            input = Secret;
        }
        else
        {
            Box = new TextBox();
            input = Box;
        }
        input.BorderThickness = new Thickness(0);
        input.Background = Brushes.Transparent;
        input.Padding = new Thickness(8, 5, 8, 6);
        input.FontSize = 14;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        input.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextPrimary);
        input.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontText);
        if (input is TextBox box) box.SetResourceReference(TextBoxBase.CaretBrushProperty, ThemeKeys.TextPrimary);
        if (input is PasswordBox pass) pass.SetResourceReference(PasswordBox.CaretBrushProperty, ThemeKeys.TextPrimary);
        input.FocusVisualStyle = null;
        AutomationProperties.SetName(input, name);
        Child = new Grid { Children = { input, _underline } };
        IsKeyboardFocusWithinChanged += (_, _) => Paint();
        Paint();
    }

    public string Text
    {
        get => Box?.Text ?? Secret?.Password ?? "";
        set
        {
            if (Box is not null) Box.Text = value;
            if (Secret is not null) Secret.Password = value;
        }
    }

    /// <summary>Calls <paramref name="action"/> after every edit.</summary>
    public TextField OnChange(Action action)
    {
        if (Box is not null) Box.TextChanged += (_, _) => action();
        if (Secret is not null) Secret.PasswordChanged += (_, _) => action();
        return this;
    }

    /// <summary>Calls <paramref name="submit"/> on Enter, and <paramref name="cancel"/> on Esc.</summary>
    public TextField OnKeys(Action? submit, Action? cancel = null)
    {
        ((UIElement?)Box ?? Secret)!.KeyDown += (_, e) =>
        {
            var action = e.Key == Key.Enter ? submit : e.Key == Key.Escape ? cancel : null;
            if (action is null) return;
            e.Handled = true;
            action();
        };
        return this;
    }

    public void FocusInput()
    {
        var input = (UIElement?)Box ?? Secret;
        input?.Focus();
        Box?.SelectAll();
    }

    private void Paint()
    {
        var focused = IsKeyboardFocusWithin;
        SetResourceReference(BackgroundProperty, focused ? WindowKeys.FieldFocused : WindowKeys.FieldFill);
        _underline.Height = focused ? 2 : 1;
        _underline.SetResourceReference(BackgroundProperty, focused ? ThemeKeys.AccentFill : WindowKeys.StrongStroke);
    }
}
