using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace BambuBar.Theme;

/// <summary>Small factories for the panel and, later, the window's pages: themed text, cards, pills, tooltips.</summary>
public static class Ui
{
    /// <summary>One line of themed text, trimmed with an ellipsis when it runs out of room.</summary>
    public static TextBlock Text(string text = "", double size = Ramp.Body, FontWeight? weight = null,
        string brushKey = ThemeKeys.TextPrimary, bool display = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(TextElement.FontFamilyProperty, display ? ThemeKeys.FontDisplay : ThemeKeys.FontText);
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>Text that wraps instead of trimming.</summary>
    public static TextBlock Paragraph(string text, double size = Ramp.Body, string brushKey = ThemeKeys.TextSecondary, FontWeight? weight = null)
    {
        var block = Text(text, size, weight, brushKey);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    /// <summary>Digits of equal width, so changing numbers don't jiggle (the Mac's monospacedDigit).</summary>
    public static T Tabular<T>(T element) where T : DependencyObject
    {
        Typography.SetNumeralAlignment(element, FontNumeralAlignment.Tabular);
        return element;
    }

    /// <summary>A rounded module, the Mac's <c>.panel()</c>: a faint fill and a hairline, or a tint at 12 %.</summary>
    public static Border Card(UIElement? child, CornerRadius radius, Thickness padding, Color? tint = null)
    {
        var card = new Border { Child = child, CornerRadius = radius, Padding = padding, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
        Tint(card, tint);
        return card;
    }

    public static void Tint(Border card, Color? tint)
    {
        if (tint is { } t)
        {
            card.Background = Palette.Brush(Palette.WithAlpha(t, 0.12));
            // The Mac's hairline, in the tint: brighter at the top, fading down.
            var edge = new LinearGradientBrush(Palette.WithAlpha(t, 0.34), Palette.WithAlpha(t, 0.08), 90);
            edge.Freeze();
            card.BorderBrush = edge;
        }
        else
        {
            card.SetResourceReference(Border.BackgroundProperty, ThemeKeys.CardFill);
            card.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.CardStroke);
        }
    }

    /// <summary>A tooltip (the Mac's <c>.help</c>); also the control's UI Automation help text.</summary>
    public static void Tip(FrameworkElement element, string? text)
    {
        element.ToolTip = text is null ? null : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        ToolTipService.SetInitialShowDelay(element, 600);
        AutomationProperties.SetHelpText(element, text ?? "");
    }

    public static T Named<T>(T element, string name) where T : DependencyObject
    {
        AutomationProperties.SetName(element, name);
        return element;
    }

    /// <summary>Wraps <paramref name="child"/> in a group screen readers name, its controls still inside
    /// (<see cref="AccessibleGroup"/>).</summary>
    public static AccessibleGroup Group(UIElement child, string name)
    {
        var group = new AccessibleGroup { Child = child };
        AutomationProperties.SetName(group, name);
        return group;
    }

    /// <summary>Wraps <paramref name="child"/> so screen readers read it as one line, <paramref name="name"/>
    /// (<see cref="AccessibleCard"/>): a detail row, a numbered step.</summary>
    public static AccessibleCard ReadAsOne(UIElement child, string name)
    {
        var card = new AccessibleCard { Child = child };
        AutomationProperties.SetName(card, name);
        return card;
    }

    /// <summary>"● Printing": the state in its colour on a soft tint of it, corners rounded a little (the Mac's
    /// StatePill). The dot's glow is a drawn halo, not an effect: effects render through a bitmap and blur the pill.</summary>
    public static Border Pill(out Ellipse dot, out TextBlock label)
    {
        var halo = new Ellipse { Width = 10, Height = 10 };
        dot = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mark = new Grid { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Children = { halo, dot } };
        label = Text("", Ramp.Caption, FontWeights.SemiBold);
        label.Margin = new Thickness(4, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, label } };
        return Capsule(new Border
        {
            Child = row, CornerRadius = Metrics.Pill, Padding = new Thickness(5, 3, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    public static void SetPill(Border pill, Ellipse dot, TextBlock label, Color color, string text, bool dark)
    {
        TintCapsule(pill, color, dark);
        dot.Fill = Palette.Brush(color);
        if (dot.Parent is Grid { Children: [Ellipse halo, ..] }) halo.Fill = Palette.Brush(Palette.WithAlpha(color, dark ? 0.30 : 0.22));
        label.Foreground = Palette.Brush(Palette.Ink(color, dark));
        label.Text = text;
    }

    /// <summary>A pill tinted with a state colour: a soft fill of it, no outline (the Mac's StatePill).</summary>
    public static void TintCapsule(Border capsule, Color color, bool dark)
    {
        capsule.Background = Palette.Brush(Palette.WithAlpha(color, dark ? 0.16 : 0.13));
        capsule.BorderBrush = null;
        capsule.BorderThickness = new Thickness(0);
    }

    /// <summary>
    /// A heart that beats: two quick pulses (to 122 %, then 114 %) every 3.2 s, so "Support this project" catches the
    /// eye without moving the text. A short animation started by a timer, not a looping one: between beats nothing is
    /// redrawn (a forever animation would redraw at 60 fps). Only while it's on screen, and not when Windows' animation
    /// effects are off.
    /// </summary>
    public static void Heartbeat(FrameworkElement heart)
    {
        if (!AppTheme.AnimationsEnabled) return;
        var scale = new ScaleTransform(1, 1);
        heart.RenderTransform = scale;
        heart.RenderTransformOrigin = new Point(0.5, 0.55);
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3.2) };
        timer.Tick += (_, _) => Beat(scale);
        heart.Loaded += (_, _) => timer.Start();
        heart.Unloaded += (_, _) => timer.Stop();
        heart.IsVisibleChanged += (_, e) => { if (e.NewValue is true && heart.IsLoaded) timer.Start(); else timer.Stop(); };
    }

    private static readonly System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames BeatAnimation = MakeBeat();

    private static System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames MakeBeat()
    {
        static System.Windows.Media.Animation.EasingDoubleKeyFrame Key(double value, double ms) =>
            new(value, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new System.Windows.Media.Animation.SineEase());
        var beat = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(640),
            KeyFrames = { Key(1.22, 110), Key(1.0, 240), Key(1.14, 350), Key(1.0, 640) },
        };
        beat.Freeze();
        return beat;
    }

    private static void Beat(ScaleTransform scale)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, BeatAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, BeatAnimation);
    }

    /// <summary>Rounds <paramref name="element"/>'s ends fully, whatever its height: the radius follows half the height
    /// (WPF draws a radius larger than the side as an ellipse, so a fixed 999 doesn't make a capsule).</summary>
    public static T Capsule<T>(T element) where T : FrameworkElement
    {
        element.SizeChanged += (_, e) =>
        {
            var radius = new CornerRadius(e.NewSize.Height / 2);
            switch (element)
            {
                case Border border: border.CornerRadius = radius; break;
                case ChromeButton button: button.CornerRadius = radius; break;
                case ChromeToggle toggle: toggle.CornerRadius = radius; break;
            }
        };
        return element;
    }

    /// <summary>Small uppercase heading above a group, with an optional note on the right (the Mac's SectionLabel).</summary>
    public static Grid SectionLabel(string title, string? trailing)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The Mac tracks the label out (0.6 pt); WPF has no letter spacing, so hair spaces stand in. Screen readers get the
        // plain words.
        var upper = title.ToUpperInvariant();
        var label = Text(string.Join('\u200A', upper.EnumerateRunes()), Ramp.Caption, FontWeights.SemiBold, ThemeKeys.TextSecondary);
        AutomationProperties.SetName(label, upper);
        grid.Children.Add(label);
        if (trailing is not null)
        {
            var note = Text(trailing, Ramp.Caption, null, ThemeKeys.TextTertiary);
            note.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(note, 1);
            grid.Children.Add(note);
        }
        return grid;
    }

    /// <summary>A grid with the given column widths ("*", "Auto", or a number).</summary>
    public static Grid Columns(params string[] widths)
    {
        var grid = new Grid();
        foreach (var w in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = w == "*" ? new GridLength(1, GridUnitType.Star) : w == "Auto" ? GridLength.Auto : new GridLength(double.Parse(w, System.Globalization.CultureInfo.InvariantCulture)),
            });
        }
        return grid;
    }

    public static T At<T>(T element, int column, int row = 0) where T : UIElement
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        return element;
    }
}

/// <summary>A stack with even gaps between its visible children (WPF's StackPanel has no spacing).</summary>
public sealed class Stack : Panel
{
    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public double Spacing { get; set; }

    /// <summary>What screen readers make of the stack: nothing of its own (null, the default: its children are read
    /// in its place), or a named container of that type (a settings group).</summary>
    public System.Windows.Automation.Peers.AutomationControlType? Role { get; set; }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        Role is { } role ? new GroupPeer(this, role) : base.OnCreateAutomationPeer();

    protected override Size MeasureOverride(Size available)
    {
        double along = 0, across = 0;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(Orientation == Orientation.Vertical ? new Size(available.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, available.Height));
            var size = child.DesiredSize;
            along += Orientation == Orientation.Vertical ? size.Height : size.Width;
            across = Math.Max(across, Orientation == Orientation.Vertical ? size.Width : size.Height);
            count++;
        }
        along += Math.Max(0, count - 1) * Spacing;
        return Orientation == Orientation.Vertical ? new Size(across, along) : new Size(along, across);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double offset = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (Orientation == Orientation.Vertical)
            {
                child.Arrange(new Rect(0, offset, final.Width, size.Height));
                offset += size.Height + Spacing;
            }
            else
            {
                child.Arrange(new Rect(offset, 0, size.Width, final.Height));
                offset += size.Width + Spacing;
            }
        }
        return final;
    }
}
