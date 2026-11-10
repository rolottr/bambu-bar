using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>No printer yet (the Mac's SetupPrompt): what the search has found (up to three, each opens setup on it)
/// and "Set Up…".</summary>
internal sealed class SetupSection : PanelSection
{
    private readonly Border _root;
    private readonly TextBlock _found;
    private readonly Stack _rows = new() { Spacing = 6 };
    private string _key = "-";

    public SetupSection(PanelContext ctx) : base(ctx)
    {
        var art = ArtImage.Element(Metrics.SmallArtWidth, Metrics.SmallArtHeight);
        ArtImage.Show(art, ArtImage.Scene(null, null, lightOn: true, dimmed: false));
        _found = Ui.Paragraph("");
        var titles = new Stack { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Paragraph(Loc.T("Connect your printer"), Ramp.CardTitle, ThemeKeys.TextPrimary, FontWeights.SemiBold));
        titles.Children.Add(_found);
        var top = Ui.Columns("Auto", "14", "*");
        top.Children.Add(Ui.At(art, 0));
        top.Children.Add(Ui.At(titles, 2));

        var setUp = new ChromeButton
        {
            Look = ButtonLook.Accent, Height = Metrics.ControlHeight, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.Text(Loc.T("Set Up…"), Ramp.Control, FontWeights.SemiBold, ThemeKeys.TextOnAccent),
        };
        AutomationProperties.SetName(setUp, Loc.T("Set Up…"));
        setUp.Click += (_, _) => Ctx.Navigate(PanelDestination.Setup, null);

        _root = Ui.Card(new Stack { Spacing = 12, Children = { top, _rows, setUp } }, Metrics.Card, new Thickness(14));
    }

    public override FrameworkElement Root => _root;

    protected override void Fill()
    {
        var discovered = Store.Discovered;
        _found.Text = discovered.Count == 0
            ? Loc.T("Looking for Bambu Lab printers on your network…")
            : Loc.P("Found %lld printer(s) on your network.", discovered.Count);
        var shown = discovered.Take(3).ToList();
        var key = string.Join("#", shown.Select(p => $"{p.Serial}{p.Name}{p.Host}{p.ModelCode}"));
        if (key == _key) return;
        _key = key;
        _rows.Children.Clear();
        _rows.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var printer in shown) _rows.Children.Add(Row(printer));
    }

    private ChromeButton Row(DiscoveredPrinter printer)
    {
        var grid = Ui.Columns("Auto", "*", "Auto");
        var icon = Icons.Glyph(Icons.Printer, 14, null, Palette.Brush(Palette.Accent));
        icon.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(Ui.At(icon, 0));
        var name = Ui.Text(printer.Name, Ramp.Footnote, FontWeights.SemiBold);
        var detail = Ui.Text(Loc.F("%@ · %@", printer.ModelName, printer.Host), Ramp.Caption, null, ThemeKeys.TextSecondary);
        grid.Children.Add(Ui.At(new Stack { Spacing = 1, Children = { name, detail } }, 1));
        grid.Children.Add(Ui.At(Icons.Glyph(Icons.ChevronRight, 10, ThemeKeys.TextTertiary), 2));
        var row = new ChromeButton
        {
            Content = grid, Look = ButtonLook.Card, CornerRadius = Metrics.Tile, Padding = new Thickness(10, 8, 10, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(row, printer.Name);
        AutomationProperties.SetHelpText(row, Loc.F("%@ · %@", printer.ModelName, printer.Host));
        row.Click += (_, _) => Ctx.Navigate(PanelDestination.Setup, printer);
        return row;
    }
}

/// <summary>Every printer, one row each, sorted by what needs attention (the Mac's FleetList). Rows are kept and
/// updated in place, so keyboard focus survives the reports.</summary>
internal sealed class FleetSection : PanelSection
{
    private readonly ScrollViewer _root;
    private readonly Stack _list = new() { Spacing = 6 };
    private readonly Dictionary<string, FleetRow> _rows = [];

    public FleetSection(PanelContext ctx) : base(ctx)
    {
        _root = new ScrollViewer
        {
            Content = _list, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
    }

    public override FrameworkElement Root => _root;

    protected override void Fill()
    {
        var order = PanelText.FleetOrder(Store);
        foreach (var gone in _rows.Keys.Except(order.Select(s => s.Id)).ToList()) _rows.Remove(gone);
        var changed = _list.Children.Count != order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            var session = order[i];
            if (!_rows.TryGetValue(session.Id, out var row))
            {
                row = new FleetRow(Ctx, session);
                _rows[session.Id] = row;
            }
            row.Update(session);
            if (!changed && _list.Children[i] != row.Button) changed = true;
        }
        if (!changed) return;
        // A new order: re-add, keeping the row objects (and focus, when the focused row stays).
        _list.Children.Clear();
        foreach (var session in order) _list.Children.Add(_rows[session.Id].Button);
    }

    private sealed class FleetRow
    {
        private readonly PanelContext _ctx;
        private readonly Image _art = ArtImage.Element(Metrics.RowArtWidth, Metrics.RowArtHeight);
        private readonly TextBlock _name = Ui.Text("", Ramp.Footnote, FontWeights.SemiBold);
        private readonly Border _chip = new() { CornerRadius = Metrics.Pill, Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _chipText = Ui.Text("", Ramp.Caption, FontWeights.SemiBold);
        private readonly TextBlock _detail = Ui.Text("", Ramp.Caption, null, ThemeKeys.TextSecondary);
        private readonly Stack _progress = new() { Spacing = 3 };
        private readonly ProgressCapsule _bar = new(4);
        private readonly TextBlock _percent = Ui.Tabular(Ui.Text("", Ramp.Caption, FontWeights.SemiBold));
        private readonly TextBlock _timing = Ui.Tabular(Ui.Text("", Ramp.Caption, null, ThemeKeys.TextSecondary));

        public ChromeButton Button { get; }

        public FleetRow(PanelContext ctx, PrinterSession session)
        {
            _ctx = ctx;
            _chip.Child = _chipText;
            var top = Ui.Columns("*", "Auto");
            top.Children.Add(Ui.At(_name, 0));
            _chip.Margin = new Thickness(6, 0, 0, 0);
            top.Children.Add(Ui.At(_chip, 1));
            var numbers = Ui.Columns("Auto", "*");
            numbers.Children.Add(Ui.At(_percent, 0));
            _timing.HorizontalAlignment = HorizontalAlignment.Right;
            _timing.Margin = new Thickness(6, 0, 0, 0);
            numbers.Children.Add(Ui.At(_timing, 1));
            _progress.Children.Add(_bar);
            _progress.Children.Add(numbers);
            var text = new Stack { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { top, _detail, _progress } };
            var grid = Ui.Columns("Auto", "10", "*");
            grid.Children.Add(Ui.At(_art, 0));
            grid.Children.Add(Ui.At(text, 2));
            Button = new ChromeButton
            {
                Content = grid, CornerRadius = Metrics.Tile, Padding = new Thickness(10, 7, 10, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var id = session.Id;
            Button.Click += (_, _) =>
            {
                ctx.Store.Select(id);
                ctx.SetShowsAll(false);
            };
        }

        public void Update(PrinterSession session)
        {
            var s = session.Status;
            var online = session.IsOnline && s is not null;
            ArtImage.Show(_art, ArtImage.Scene(s, session.Config.ModelName, s?.ChamberLightOn ?? false, dimmed: !online));
            _name.Text = session.Config.Name;
            var color = online ? Palette.For(s!) : session.Phase is PrinterConnection.Phase.Connecting ? Palette.Paused : Palette.Idle;
            _chip.Background = Palette.Brush(Palette.WithAlpha(color, 0.14));
            _chipText.Foreground = online || session.Phase is PrinterConnection.Phase.Connecting ? _ctx.Ink(color) : _ctx.Brush(ThemeKeys.TextSecondary);
            _chipText.Text = PanelText.ChipText(session);
            var showsProgress = online && (s!.State.IsActive() || s.State == JobState.Finished);
            _progress.Visibility = showsProgress ? Visibility.Visible : Visibility.Collapsed;
            _detail.Visibility = showsProgress ? Visibility.Collapsed : Visibility.Visible;
            var detail = PanelText.FleetDetail(session);
            _detail.Text = detail;
            if (showsProgress)
            {
                _bar.Tint = color;
                _bar.SetProgress(s!.Progress / 100.0, false);
                _percent.Text = $"{s.Progress}%";
                _timing.Text = PanelText.FleetTiming(s);
            }
            var attention = PanelText.NeedsAttention(session);
            Button.Look = attention ? ButtonLook.Tinted(color, 0.12, 0.17, 0.22) with { Stroke = null }
                : new ButtonLook(ThemeKeys.CardFill, ThemeKeys.ControlHover, ThemeKeys.ControlPressed, ThemeKeys.CardStroke);
            AutomationProperties.SetName(Button, session.Config.Name);
            AutomationProperties.SetItemStatus(Button, session.ShortState);
            Ui.Tip(Button, Loc.F("%@: %@", session.Config.Name, detail));
        }
    }
}

/// <summary>The selected printer isn't live (the Mac's ConnectionCard): connecting, access code rejected (with the
/// slicer's newer code when one is known), certificate not trusted, or offline, with Try Again and Printers.</summary>
internal sealed class ConnectionSection : PanelSection
{
    private readonly Border _root;
    private readonly Image _art = ArtImage.Element(Metrics.SmallArtWidth, Metrics.SmallArtHeight);
    private readonly TextBlock _title = Ui.Paragraph("", Ramp.CardTitle, ThemeKeys.TextPrimary, FontWeights.SemiBold);
    private readonly TextBlock _detail = Ui.Paragraph("");
    private readonly Spinner _spinner;
    private readonly ChromeButton _useCode;
    private readonly ChromeButton _tryAgain;
    private readonly ChromeButton _printers;

    public ConnectionSection(PanelContext ctx) : base(ctx)
    {
        _spinner = new Spinner(16, ctx.Animate) { Margin = new Thickness(0, 2, 0, 0) };
        _useCode = SmallButton("");
        _useCode.Click += (_, _) => Store.UseReplacementCode();
        _tryAgain = SmallButton(Loc.T("Try Again"));
        _tryAgain.Click += (_, _) => Store.Refresh();
        _printers = SmallButton(Loc.T("Printers"));
        _printers.Click += (_, _) => Ctx.Navigate(PanelDestination.Printers, null);
        var buttons = new Stack { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0), Children = { _spinner, _useCode, _tryAgain, _printers } };
        var text = new Stack { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail, buttons } };
        var grid = Ui.Columns("Auto", "14", "*");
        grid.Children.Add(Ui.At(_art, 0));
        grid.Children.Add(Ui.At(text, 2));
        _root = Ui.Card(grid, Metrics.Card, new Thickness(14));
    }

    public override FrameworkElement Root => _root;

    private static ChromeButton SmallButton(string text)
    {
        var button = new ChromeButton { Look = ButtonLook.Standard, Padding = new Thickness(11, 4, 11, 5), Content = Ui.Text(text, Ramp.Footnote, FontWeights.Medium) };
        AutomationProperties.SetName(button, text);
        return button;
    }

    protected override void Fill()
    {
        var session = Store.Current!;
        var phase = session.Phase;
        ArtImage.Show(_art, ArtImage.Scene(null, session.Config.ModelName, lightOn: false, dimmed: true));
        var name = Store.PrinterName;
        _title.Text = phase switch
        {
            PrinterConnection.Phase.Connecting => Loc.F("Connecting to %@…", name),
            PrinterConnection.Phase.Unauthorized => Loc.T("Access code rejected"),
            PrinterConnection.Phase.Certificate => Loc.T("Can't verify the printer"),
            _ => Loc.F("%@ is offline", name),
        };
        _detail.Text = session.LinkDetail ?? Loc.T("Bambu Bar keeps trying in the background.");
        var connecting = phase is PrinterConnection.Phase.Connecting;
        var replacement = phase is PrinterConnection.Phase.Unauthorized ? Store.ReplacementCode : null;
        _spinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        _useCode.Visibility = replacement is not null ? Visibility.Visible : Visibility.Collapsed;
        _tryAgain.Visibility = !connecting && replacement is null ? Visibility.Visible : Visibility.Collapsed;
        _printers.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
        if (replacement is not null)
        {
            var label = Loc.F("Use Code from %@", replacement.Source);
            ((TextBlock)_useCode.Content).Text = label;
            AutomationProperties.SetName(_useCode, label);
        }
    }
}
