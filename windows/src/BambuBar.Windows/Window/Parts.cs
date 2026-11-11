using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.CameraViews;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>Lays its children out in columns: a fixed number, or as many of at least <see cref="MinItemWidth"/> as fit
/// (the Mac's LazyVGrid with flexible or adaptive items). Rows are as tall as their tallest item.</summary>
internal sealed class AdaptiveGrid : System.Windows.Controls.Panel
{
    public int Columns { get; init; }
    public double MinItemWidth { get; init; } = 250;
    public double Spacing { get; init; } = 14;

    private int ColumnsFor(double width) =>
        Columns > 0 ? Columns : Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));

    private double ItemWidth(double width, int columns) => Math.Max(0, (width - (columns - 1) * Spacing) / columns);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinItemWidth * Math.Max(1, Columns) : available.Width;
        var columns = ColumnsFor(width);
        var item = ItemWidth(width, columns);
        double height = 0, row = 0;
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(item, double.PositiveInfinity));
            row = Math.Max(row, child.DesiredSize.Height);
            if (++i % columns == 0) { height += row + Spacing; row = 0; }
        }
        if (i % columns != 0) height += row; else if (i > 0) height -= Spacing;
        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = ColumnsFor(final.Width);
        var item = ItemWidth(final.Width, columns);
        var children = InternalChildren.Cast<UIElement>().ToList();
        double y = 0;
        for (var start = 0; start < children.Count; start += columns)
        {
            var rowItems = children.Skip(start).Take(columns).ToList();
            var row = rowItems.Max(c => c.DesiredSize.Height);
            for (var c = 0; c < rowItems.Count; c++) rowItems[c].Arrange(new Rect(c * (item + Spacing), y, item, row));
            y += row + Spacing;
        }
        return final;
    }
}

/// <summary>
/// One of the printer's issues (the Mac's IssueBanner): the message, its code with a copy button that ticks for
/// 1.5 s, the wiki link (resolved to the code's own page when clicked), and dismiss.
/// </summary>
internal sealed class IssueBanner : Border
{
    private static readonly HmsWiki Wiki = new();
    private readonly ChromeButton _copy;
    private readonly DispatcherTimer _copied = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly PrinterIssue _issue;
    private readonly WindowContext _ctx;

    public IssueBanner(WindowContext ctx, PrinterIssue issue, Action? dismiss)
    {
        _ctx = ctx;
        _issue = issue;
        var color = issue.Severity <= IssueSeverity.Fatal ? Palette.Failed : issue.Severity == IssueSeverity.Serious ? Palette.Paused : Palette.Preparing;
        var icon = Icons.Glyph(issue.Severity <= IssueSeverity.Serious ? Icons.Warning : Icons.Info, 16, null, ctx.Ink(color));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 12, 0);
        var message = Ui.Paragraph(issue.Message, WRamp.Detail, ThemeKeys.TextPrimary, FontWeights.SemiBold);
        var code = new TextBox
        {
            Text = issue.Id, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            FontSize = 12, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center,
        };
        code.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
        code.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextSecondary);
        AutomationProperties.SetName(code, issue.Id);
        _copy = new ChromeButton { Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
        _copy.Click += (_, _) => Copy();
        _copied.Tick += (_, _) => ShowCopied(false);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { code, _copy } };
        if (issue.WikiUrl is { } url)
        {
            var wiki = W.Link(Loc.T("Wiki"), async () =>
            {
                Uri? page = null;
                try { page = await Wiki.ResolveAsync(url); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
                W.Open(page ?? url);
            }, size: 12);
            wiki.Margin = new Thickness(6, 0, 0, 0);
            Ui.Tip(wiki, url.AbsoluteUri);
            row.Children.Add(wiki);
        }
        var grid = Ui.Columns("Auto", "*", "Auto");
        grid.Children.Add(Ui.At(icon, 0));
        grid.Children.Add(Ui.At(new Stack { Spacing = 4, Children = { message, row } }, 1));
        if (dismiss is not null)
        {
            var close = new ChromeButton { Width = 28, Height = 28, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, -4, -4, 0), Content = Icons.Glyph(Icons.Close, 11) };
            AutomationProperties.SetName(close, Loc.T("Dismiss until the printer clears it"));
            Ui.Tip(close, Loc.T("Dismiss until the printer clears it"));
            close.Click += (_, _) => dismiss();
            grid.Children.Add(Ui.At(close, 2));
        }
        Child = grid;
        CornerRadius = Metrics.Card;
        Padding = new Thickness(14, 12, 14, 12);
        BorderThickness = new Thickness(1);
        Ui.Tint(this, color);
        // A group named by the message, its copy, wiki and dismiss buttons inside (the Mac's banner).
        AutomationProperties.SetName(this, issue.Message);
        ShowCopied(false);
        Unloaded += (_, _) => _copied.Stop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    private void Copy()
    {
        try { Clipboard.SetText(_issue.Id); }
        catch (ExternalException) { return; }
        ShowCopied(true);
        _copied.Stop();
        _copied.Start();
    }

    private void ShowCopied(bool copied)
    {
        if (!copied) _copied.Stop();
        _copy.Content = Icons.Glyph(copied ? Icons.Check : Icons.Copy, 11, copied ? null : ThemeKeys.TextSecondary, copied ? _ctx.Ink(Palette.Accent) : null);
        var label = copied ? Loc.T("Copied") : Loc.T("Copy code");
        AutomationProperties.SetName(_copy, label);
        Ui.Tip(_copy, label);
    }
}

/// <summary>
/// "Updated 3s ago" and a refresh button (the Mac's RefreshControl): asks the printer for a full report; the arrow
/// turns until fresh data arrives, or for at most 4 s, and the label says how recent the data is.
/// </summary>
internal sealed class RefreshControl : StackPanel, IDisposable
{
    private readonly WindowContext _ctx;
    private readonly TextBlock _label = Ui.Tabular(Ui.Text("", WRamp.Note, null, ThemeKeys.TextSecondary));
    private readonly RotateTransform _spin = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _limit = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly ChromeButton _button;
    private DateTimeOffset? _requestedAt;

    public RefreshControl(WindowContext ctx)
    {
        _ctx = ctx;
        Orientation = Orientation.Horizontal;
        _label.Margin = new Thickness(0, 0, 10, 0);
        var glyph = Icons.Glyph(Icons.Refresh, 14);
        glyph.RenderTransform = _spin;
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        _button = new ChromeButton { Width = 32, Height = 32, Padding = new Thickness(0), Content = glyph, Look = ButtonLook.Standard, CornerRadius = Metrics.Round };
        AutomationProperties.SetName(_button, Loc.T("Refresh"));
        AutomationProperties.SetAcceleratorKey(_button, "Ctrl+R");
        Ui.Tip(_button, Loc.T("Ask the printer for a full status report"));
        _button.Click += (_, _) => Start();
        Children.Add(_label);
        Children.Add(_button);
        _tick.Tick += (_, _) => Update();
        _limit.Tick += (_, _) => Finish();
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => Dispose();
        Update();
    }

    public void Update()
    {
        var store = _ctx.Store;
        _button.IsEnabled = store.IsConfigured;
        if (_requestedAt is { } asked && store.Current?.LastHeard is { } heard && heard >= asked) Finish();
        _label.Text = Label();
    }

    private string Label()
    {
        var store = _ctx.Store;
        if (_requestedAt is not null) return Loc.T("Refreshing…");
        if (store.Current is not { LastHeard: { } heard, IsOnline: true }) return store.LinkSummary;
        var seconds = (int)(DateTimeOffset.Now - heard).TotalSeconds;
        if (seconds < 3) return Loc.T("Updated just now");
        if (seconds < 60) return Loc.F("Updated %llds ago", seconds);
        return Loc.F("Updated %@", PanelText.Ago(heard));
    }

    private void Start()
    {
        if (_requestedAt is not null) return;
        _requestedAt = DateTimeOffset.Now;
        if (_ctx.Animate)
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        _limit.Start();
        _ctx.Store.Refresh();
        _label.Text = Label();
    }

    private void Finish()
    {
        _requestedAt = null;
        _limit.Stop();
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _label.Text = Label();
    }

    public void Dispose()
    {
        _tick.Stop();
        _limit.Stop();
    }
}

/// <summary>The printer's state as a small coloured label (the Mac's StateChip).</summary>
internal static class StateChip
{
    public static Border For(WindowContext ctx, PrinterSession session)
    {
        var online = session.IsOnline && session.Status is not null;
        var connecting = session.Phase is PrinterConnection.Phase.Connecting;
        var color = online ? Palette.For(session.Status!) : connecting ? Palette.Paused : Palette.Idle;
        var text = Ui.Text(PanelText.ChipText(session), 12, FontWeights.SemiBold);
        text.Foreground = online || connecting ? ctx.Ink(color) : ctx.Brush(ThemeKeys.TextSecondary);
        var chip = new Border
        {
            Child = text, CornerRadius = Metrics.Pill, Padding = new Thickness(8, 1, 8, 2), VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.Capsule(chip);
        Ui.TintCapsule(chip, color, ctx.Dark);
        return chip;
    }
}

/// <summary>The camera on the Printer page (the Mac's CameraCard): a heading with the stream's details, the pop-out
/// button (Ctrl+K) and Hide/Show, then the picture at the page's width, as tall as its shape makes it.</summary>
internal static class CameraCard
{
    public static FrameworkElement Build(WindowContext ctx, PrinterSession session, Action changed)
    {
        var shown = ctx.Settings.Get(WindowSettings.CameraOnPrinterPage, true);
        var feed = session.Camera;
        var header = Ui.Columns("*", "Auto", "Auto", "Auto");
        header.Children.Add(Ui.At(Ui.SectionLabel(Loc.T("Camera"), null), 0));
        var detail = Ui.Text("", Ramp.Caption, null, ThemeKeys.TextTertiary);
        detail.Margin = new Thickness(6, 0, 4, 0);
        header.Children.Add(Ui.At(detail, 1));
        var popOut = new ChromeButton { Width = 28, Height = 28, Padding = new Thickness(0), Content = Icons.Glyph(Icons.OpenWindow, 12) };
        AutomationProperties.SetName(popOut, Loc.T("Open the camera in its own window"));
        AutomationProperties.SetAcceleratorKey(popOut, "Ctrl+K");
        Ui.Tip(popOut, Loc.T("Open the camera in its own window"));
        popOut.Click += (_, _) => CameraWindow.Show(session);
        header.Children.Add(Ui.At(popOut, 2));
        var toggle = W.Link(shown ? Loc.T("Hide") : Loc.T("Show"), () =>
        {
            ctx.Settings.Set(WindowSettings.CameraOnPrinterPage, !shown);
            changed();
        }, size: 12);
        header.Children.Add(Ui.At(toggle, 3));
        var stack = new Stack { Spacing = 8, Children = { header } };
        AspectBox? box = null;
        if (shown)
        {
            box = new AspectBox { Ratio = feed.AspectRatio, Child = new CameraPlayer(compact: false, cornerRadius: Metrics.Hero.TopLeft) { Session = session } };
            stack.Children.Add(box);
        }
        void Update()
        {
            detail.Text = shown ? feed.Detail ?? "" : "";
            if (box is not null) box.Ratio = feed.AspectRatio;
        }
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();
        stack.Loaded += (_, _) => { feed.PropertyChanged += OnChanged; Update(); };
        stack.Unloaded += (_, _) => feed.PropertyChanged -= OnChanged;
        Update();
        return stack;
    }
}

/// <summary>The fans beside the temperatures (the Mac's FanTile): each fan's name, a hairline bar and its speed.</summary>
internal static class FanTile
{
    public static string Key(PrinterStatus s) => string.Join("#", FanSection.Readings(s).Select(f => $"{f.Title}{f.Value}")) + (s.PartFan > 0);

    public static FrameworkElement Build(WindowContext ctx, PrinterStatus s)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Icons.Draw(Icons.Vector.Fan, 12, (s.PartFan ?? 0) > 0 ? Palette.Brush(Palette.Chamber) : ctx.Brush(ThemeKeys.TextSecondary)));
        var title = Ui.Text(Loc.T("Fans"), Ramp.Footnote, FontWeights.Medium, ThemeKeys.TextSecondary);
        title.Margin = new Thickness(5, 0, 0, 0);
        header.Children.Add(title);
        var rows = new Stack { Spacing = 8, Children = { header } };
        foreach (var (name, value) in FanSection.Readings(s))
        {
            var row = Ui.Columns("52", "*", "40");
            row.Children.Add(Ui.At(Ui.Text(name, Ramp.Caption, null, ThemeKeys.TextSecondary), 0));
            var bar = new ProgressCapsule(4) { Tint = Palette.WithAlpha(Palette.Chamber, 0.85), VerticalAlignment = VerticalAlignment.Center };
            bar.SetProgress((value ?? 0) / 100.0, false);
            row.Children.Add(Ui.At(bar, 1));
            var percent = Ui.Tabular(Ui.Text(value is { } v ? $"{v}%" : "–", Ramp.Caption, FontWeights.Medium));
            percent.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(Ui.At(percent, 2));
            var item = new AccessibleStack { Children = { row } };
            AutomationProperties.SetName(item, $"{name}, {(value is { } p ? $"{p}%" : "–")}");
            rows.Children.Add(item);
        }
        var card = Ui.Card(rows, Metrics.Tile, new Thickness(11));
        card.MinWidth = 210;
        return card;
    }
}
