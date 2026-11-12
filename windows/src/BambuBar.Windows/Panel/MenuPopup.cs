using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>One entry of a <see cref="MenuPopup"/>; <see cref="Separator"/> draws a line instead.</summary>
public sealed record MenuEntry(string Text, Action? Action = null, bool Checked = false, Func<Brush, FrameworkElement>? Icon = null)
{
    public static readonly MenuEntry Separator = new("-");
}

/// <summary>
/// A Fluent menu flyout (the Mac's Menu): rounded, shadowed, a check beside the current choice. Built on a Popup, so
/// it doesn't take activation from the panel (which would light-dismiss it). Arrow keys move, Enter picks, Esc closes
/// and gives focus back to the button that opened it. Screen readers get a menu of menu items, the current choice
/// checked, and the button that opened it expanded while it's open.
/// </summary>
public static class MenuPopup
{
    /// <summary>The menu on screen, if any (the accessibility check walks it).</summary>
    internal static Popup? Open { get; private set; }

    /// <summary>Closes the menu <paramref name="target"/> opened, if it's open; false when it isn't.</summary>
    internal static bool Close(FrameworkElement target)
    {
        if (Open is not { IsOpen: true } popup || popup.PlacementTarget != target) return false;
        popup.IsOpen = false;
        return true;
    }

    public static void Show(FrameworkElement target, ResourceDictionary theme, IReadOnlyList<MenuEntry> entries, string? header = null)
    {
        var list = new MenuList { Margin = new Thickness(4) };
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Cycle);
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = AppTheme.AnimationsEnabled ? PopupAnimation.Fade : PopupAnimation.None, VerticalOffset = 4,
        };
        var secondary = (Brush?)theme[ThemeKeys.TextSecondary] ?? Brushes.Gray;
        if (header is not null)
        {
            var title = Ui.Text(header, Ramp.Caption, FontWeights.SemiBold, ThemeKeys.TextSecondary);
            title.Margin = new Thickness(12, 6, 12, 4);
            list.Children.Add(title);
        }
        ChromeButton? focus = null;
        foreach (var entry in entries)
        {
            if (entry == MenuEntry.Separator)
            {
                var line = new Border { Height = 1, Margin = new Thickness(-4, 4, -4, 4) };
                line.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Divider);
                list.Children.Add(line);
                continue;
            }
            var row = Ui.Columns("20", "Auto", "*");
            var check = Icons.Glyph(Icons.Check, 12, ThemeKeys.TextPrimary);
            check.Visibility = entry.Checked ? Visibility.Visible : Visibility.Hidden;
            row.Children.Add(Ui.At(check, 0));
            if (entry.Icon is { } icon)
            {
                var image = icon(secondary);
                image.Margin = new Thickness(4, 0, 8, 0);
                row.Children.Add(Ui.At(image, 1));
            }
            var text = Ui.Text(entry.Text, Ramp.Control);
            text.Margin = new Thickness(entry.Icon is null ? 4 : 0, 0, 12, 0);
            row.Children.Add(Ui.At(text, 2));
            var button = new ChromeButton
            {
                Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 7, 8, 7),
                MinWidth = 200, Margin = new Thickness(0, 1, 0, 1), Role = AutomationControlType.MenuItem, Selected = entry.Checked ? true : null,
            };
            AutomationProperties.SetName(button, entry.Text);
            var action = entry.Action;
            button.Click += (_, _) =>
            {
                popup.IsOpen = false;
                if (target.IsVisible) target.Focus();
                action?.Invoke();
            };
            if (entry.Checked || focus is null) focus = button;
            list.Children.Add(button);
        }

        var card = new Border
        {
            Child = new ScrollViewer
            {
                Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            },
            CornerRadius = Metrics.Overlay, BorderThickness = new Thickness(1), Margin = new Thickness(12, 4, 12, 16),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
        };
        card.SetResourceReference(Border.BackgroundProperty, ThemeKeys.FlyoutFill);
        card.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.FlyoutStroke);
        // A popup isn't in the panel's tree, so it brings its own theme.
        card.Resources.MergedDictionaries.Add(theme);
        if (header is not null) AutomationProperties.SetName(list, header);
        popup.Child = card;
        popup.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup.IsOpen = false;
            if (target.IsVisible) target.Focus();
            e.Handled = true;
        };
        popup.Opened += (_, _) => focus?.Focus();
        var opener = target as ChromeButton;
        popup.Closed += (_, _) =>
        {
            if (opener is not null) opener.Expanded = false;
            if (Open == popup) Open = null;
        };
        Open = popup;
        popup.IsOpen = true;
        if (opener is not null) opener.Expanded = true;
    }

    /// <summary>The entries' container: a menu to screen readers, named after its header.</summary>
    private sealed class MenuList : StackPanel
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this, AutomationControlType.Menu);
    }
}
