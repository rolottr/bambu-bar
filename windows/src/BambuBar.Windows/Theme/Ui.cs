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
            card.BorderBrush = Palette.Brush(Palette.WithAlpha(t, 0.28));
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

    /// <summary>"● Printing": the state in its colour on a 14 % capsule.</summary>
    public static Border Pill(out Ellipse dot, out TextBlock label)
    {
        dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        label = Text("", Ramp.Caption, FontWeights.SemiBold);
        label.Margin = new Thickness(5, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } };
        return new Border
        {
            Child = row, CornerRadius = Metrics.Pill, Padding = new Thickness(7, 2, 8, 3), HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    public static void SetPill(Border pill, Ellipse dot, TextBlock label, Color color, string text, bool dark)
    {
        pill.Background = Palette.Brush(Palette.WithAlpha(color, 0.14));
        dot.Fill = Palette.Brush(color);
        dot.Effect = new DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 };
        label.Foreground = Palette.Brush(Palette.Ink(color, dark));
        label.Text = text;
    }

    /// <summary>Small uppercase heading above a group, with an optional note on the right (the Mac's SectionLabel).</summary>
    public static Grid SectionLabel(string title, string? trailing)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(title.ToUpperInvariant(), Ramp.Caption, FontWeights.SemiBold, ThemeKeys.TextSecondary);
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
