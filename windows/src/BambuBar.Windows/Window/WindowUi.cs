using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>The window's type ramp: Windows 11's (Title 28, Body 14, Caption 12), where the Mac's pages use 26, 12.5
/// and 11.5 pt.</summary>
internal static class WRamp
{
    public const double PageTitle = 28;
    public const double Subtitle = 14;
    public const double GroupTitle = 14;
    public const double Body = 14;
    public const double Detail = 13;
    public const double Note = 12;
}

/// <summary>Building blocks of the window's pages, after the Mac's PageHeader, SettingsGroup, SettingsRow,
/// SettingsToggle and DetailRow, drawn the Windows 11 way.</summary>
internal static class W
{
    /// <summary>The page's title, a line under it, and something on the right (a button, the refresh control).</summary>
    public static Grid PageHeader(string title, string? subtitle, UIElement? trailing = null) =>
        PageHeader(Ui.Text(title, WRamp.PageTitle, FontWeights.SemiBold, display: true), subtitle is null ? null : Subtitle(subtitle), trailing);

    public static Grid PageHeader(UIElement title, UIElement? subtitle, UIElement? trailing)
    {
        var grid = Ui.Columns("*", "Auto");
        grid.Margin = new Thickness(0, 0, 0, 4);
        var titles = new Stack { Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, Children = { title } };
        if (subtitle is not null) titles.Children.Add(subtitle);
        grid.Children.Add(Ui.At(titles, 0));
        if (trailing is FrameworkElement t)
        {
            t.VerticalAlignment = VerticalAlignment.Bottom;
            t.Margin = new Thickness(16, 0, 0, 0);
            grid.Children.Add(Ui.At(t, 1));
        }
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        return grid;
    }

    public static TextBlock Subtitle(string text) => Ui.Paragraph(text, WRamp.Subtitle);

    /// <summary>A titled card of settings (the Mac's SettingsGroup).</summary>
    public static Stack Group(string title, UIElement icon, out Stack body)
    {
        body = new Stack { Spacing = 14 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 0) };
        if (icon is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 0, 8, 0);
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        header.Children.Add(icon);
        var label = Ui.Text(title, WRamp.GroupTitle, FontWeights.SemiBold);
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        header.Children.Add(label);
        // A group named by its heading (which may change later: the Printers page's), like a GroupBox.
        var group = new Stack { Spacing = 8, Role = AutomationControlType.Group, Children = { header, Ui.Card(body, Metrics.Card, new Thickness(16)) } };
        AutomationProperties.SetLabeledBy(group, label);
        return group;
    }

    public static Stack Group(string title, string glyph, out Stack body) =>
        Group(title, Icons.Glyph(glyph, 16, ThemeKeys.TextPrimary), out body);

    /// <summary>A title on the left, a control on the right (the Mac's SettingsRow).</summary>
    public static Grid Row(string title, UIElement content)
    {
        var grid = Ui.Columns("*", "Auto");
        var label = Ui.Text(title, WRamp.Body);
        label.TextTrimming = TextTrimming.None;
        label.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(Ui.At(label, 0));
        if (content is FrameworkElement fe)
        {
            fe.Margin = new Thickness(12, 0, 0, 0);
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        grid.Children.Add(Ui.At(content, 1));
        return grid;
    }

    /// <summary>A setting with a switch (the Mac's SettingsToggle).</summary>
    public static Grid Switch(string title, bool on, Action<bool> changed, out ToggleSwitch toggle)
    {
        toggle = ToggleSwitch.For(title, on, changed);
        return Row(title, toggle);
    }

    public static Grid Switch(string title, bool on, Action<bool> changed) => Switch(title, on, changed, out _);

    /// <summary>A secondary note under a setting. <c>**bold**</c> as in the Mac's catalog keys.</summary>
    public static TextBlock Note(string text, string brushKey = ThemeKeys.TextSecondary, double size = WRamp.Note)
    {
        var block = Ui.Paragraph("", size, brushKey);
        Rich(block, text);
        return block;
    }

    /// <summary>Fills a text block from Markdown-ish text: <c>**…**</c> is bold, the rest plain.</summary>
    public static void Rich(TextBlock block, string text)
    {
        block.Inlines.Clear();
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new Run(parts[i]);
            if (i % 2 == 1) run.FontWeight = FontWeights.SemiBold;
            block.Inlines.Add(run);
        }
    }

    public static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 2) };
        line.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Divider);
        return line;
    }

    /// <summary>A Windows button: standard, or the accent one for the main action.</summary>
    public static ChromeButton Button(string text, Action? click, bool accent = false, bool small = false, string? glyph = null)
    {
        var label = Ui.Text(text, small ? WRamp.Detail : WRamp.Body, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? ThemeKeys.TextOnAccent : ThemeKeys.TextPrimary);
        UIElement content = label;
        if (glyph is not null)
        {
            label.Margin = new Thickness(8, 0, 0, 0);
            content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icons.Glyph(glyph, small ? 12 : 14, accent ? ThemeKeys.TextOnAccent : ThemeKeys.TextPrimary), label } };
        }
        var button = new ChromeButton
        {
            Look = accent ? ButtonLook.Accent : ButtonLook.Standard, Content = content,
            Height = small ? 28 : 32, Padding = new Thickness(small ? 10 : 12, 0, small ? 10 : 12, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, text);
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    private static readonly Dictionary<int, System.Windows.Media.Imaging.BitmapImage?> AppIcons = [];

    /// <summary>Bambu Bar's icon (AppIcon.png beside the exe, as toasts show it) decoded at <paramref name="pixels"/>
    /// wide; null in a build without the file.</summary>
    public static System.Windows.Media.Imaging.BitmapImage? AppIcon(int pixels)
    {
        if (AppIcons.TryGetValue(pixels, out var cached)) return cached;
        System.Windows.Media.Imaging.BitmapImage? image = null;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.png");
        if (System.IO.File.Exists(path))
        {
            try
            {
                image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = pixels;
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
            }
            catch (Exception e) when (e is System.IO.IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
            {
                image = null;
            }
        }
        return AppIcons[pixels] = image;
    }

    /// <summary>A text button in the link colour (the Mac's .link buttons and Links).</summary>
    public static ChromeButton Link(string text, Action click, string? glyph = null, double size = WRamp.Detail)
    {
        var label = Ui.Text(text, size, null, ThemeKeys.TextLink);
        UIElement content = label;
        if (glyph is not null)
        {
            label.Margin = new Thickness(8, 0, 0, 0);
            var icon = Icons.Glyph(glyph, size, ThemeKeys.TextLink);
            icon.Width = 18;
            content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
        }
        var button = new ChromeButton { Content = content, Padding = new Thickness(6, 3, 6, 4), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>"Speed ……… Standard (100%)" (the Mac's DetailRow), read as "Speed: Standard (100%)".</summary>
    public static FrameworkElement DetailRow(string title, string value, FrameworkElement? icon = null)
    {
        var grid = Ui.Columns("Auto", "Auto", "*");
        var slot = new Border { Width = 16, Margin = new Thickness(0, 0, 8, 0), Child = icon, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(Ui.At(slot, 0));
        grid.Children.Add(Ui.At(Ui.Text(title, WRamp.Detail, null, ThemeKeys.TextSecondary), 1));
        var text = Ui.Text(value, WRamp.Detail, FontWeights.SemiBold);
        text.TextAlignment = TextAlignment.Right;
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(Ui.At(text, 2));
        return Ui.ReadAsOne(grid, $"{title}: {value}");
    }

    public static FrameworkElement Glyph(string glyph) => Icons.Glyph(glyph, 13, ThemeKeys.TextSecondary);

    public static FrameworkElement Vector(Icons.Vector icon, WindowContext ctx) => Icons.Draw(icon, 13, ctx.Brush(ThemeKeys.TextSecondary));

    /// <summary>A card of rows (the Mac's .panel(radius: 16, padding: 13)).</summary>
    public static Border Card(UIElement child, double padding = 14, Color? tint = null) =>
        Ui.Card(child, Metrics.Card, new Thickness(padding), tint);

    /// <summary>A message across the page (Windows' InfoBar): an icon in the tint, the text, an optional button.</summary>
    public static Border InfoBar(Color tint, string glyph, string text, WindowContext ctx, UIElement? action = null)
    {
        var grid = Ui.Columns("Auto", "*", "Auto");
        var icon = Icons.Glyph(glyph, 16, null, ctx.Ink(tint));
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(Ui.At(icon, 0));
        var message = Note(text, ThemeKeys.TextPrimary, WRamp.Detail);
        message.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(Ui.At(message, 1));
        if (action is FrameworkElement a)
        {
            a.Margin = new Thickness(12, 0, 0, 0);
            a.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(Ui.At(a, 2));
        }
        // Named by its message, its button inside, and read out when it appears.
        var group = Announce.WhenShown(Ui.Group(grid, text.Replace("**", "", StringComparison.Ordinal)));
        return Ui.Card(group, Metrics.Card, new Thickness(14, 12, 14, 12), tint);
    }

    /// <summary>Nothing to show yet: a large faint icon, a title and why (the Mac's empty states).</summary>
    public static Border Empty(string glyph, string title, string detail)
    {
        var icon = Icons.Glyph(glyph, 36, ThemeKeys.TextTertiary);
        var heading = Ui.Text(title, 16, FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        var note = Ui.Paragraph(detail, WRamp.Detail);
        note.TextAlignment = TextAlignment.Center;
        note.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new Stack { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, heading, note } };
        return Ui.Card(stack, Metrics.Card, new Thickness(24, 50, 24, 50));
    }

    /// <summary>Opens a link or an <c>ms-settings:</c> page; nothing in snapshots.</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Nothing handles it (a stripped-down Windows without Settings): nothing more to do.
        }
    }

    public static void Open(Uri url) => Open(url.AbsoluteUri);
}

/// <summary>A drop-down button (Windows' ComboBox look): the current choice and a chevron; a click opens a menu of the
/// choices with a check beside the current one.</summary>
internal sealed class Dropdown : ChromeButton
{
    private readonly TextBlock _text = Ui.Text("", WRamp.Body);
    private readonly ResourceDictionary _theme;
    private readonly Func<IReadOnlyList<MenuEntry>> _entries;

    public Dropdown(string name, ResourceDictionary theme, Func<IReadOnlyList<MenuEntry>> entries, double width = 240)
    {
        _theme = theme;
        _entries = entries;
        Look = ButtonLook.Standard;
        Width = width;
        Height = 32;
        Padding = new Thickness(11, 0, 10, 1);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var grid = Ui.Columns("*", "Auto");
        grid.Children.Add(Ui.At(_text, 0));
        var chevron = Icons.Glyph(Icons.ChevronDown, 11);
        chevron.Margin = new Thickness(8, 1, 0, 0);
        grid.Children.Add(Ui.At(chevron, 1));
        Content = grid;
        Expanded = false;
        AutomationProperties.SetName(this, name);
        Click += (_, _) => MenuPopup.Show(this, _theme, _entries());
    }

    public string Text
    {
        get => _text.Text;
        set
        {
            _text.Text = value;
            AutomationProperties.SetItemStatus(this, value);
        }
    }
}
