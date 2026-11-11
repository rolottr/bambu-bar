using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.Protocol;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>
/// The panel's header (MenuPanel.header): the printer's name (a menu of every printer when there are several), its
/// link dot and "model · Wi-Fi" line, or "All Printers" with the fleet summary; then the all-printers switch, refresh
/// and settings.
/// </summary>
internal sealed class HeaderSection : PanelSection, IDisposable
{
    private readonly Grid _root;
    private readonly TextBlock _allTitle;
    private readonly ChromeButton _picker;
    private readonly TextBlock _pickerName;
    private readonly TextBlock _plainTitle;
    private readonly TextBlock _summary;
    private readonly FrameworkElement _linkRow;
    private readonly Ellipse _linkDot;
    private readonly TextBlock _linkLine;
    private readonly ChromeToggle _allToggle;
    private readonly ChromeButton _refresh;
    private readonly RotateTransform _spin = new();
    private readonly DispatcherTimer _spinLimit = new() { Interval = TimeSpan.FromSeconds(4) };
    private DateTimeOffset? _requestedAt;

    public HeaderSection(PanelContext ctx) : base(ctx)
    {
        _root = Ui.Columns("*", "Auto");
        _root.Margin = new Thickness(4, 2, 0, 0);

        _allTitle = Ui.Text(Loc.T("All Printers"), Ramp.Title, FontWeights.SemiBold);
        _plainTitle = Ui.Text("", Ramp.Title, FontWeights.SemiBold);
        _pickerName = Ui.Text("", Ramp.Title, FontWeights.SemiBold);
        var chevron = Icons.Glyph(Icons.ChevronDown, 10);
        chevron.Margin = new Thickness(6, 2, 0, 0);
        _picker = new ChromeButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pickerName, chevron } },
            Padding = new Thickness(6, 2, 6, 3), Margin = new Thickness(-6, -2, 0, -1), HorizontalAlignment = HorizontalAlignment.Left,
            Expanded = false,
        };
        Ui.Tip(_picker, Loc.T("Show another printer"));
        _picker.Click += (_, _) => ShowPrinterMenu();

        _summary = Ui.Text("", Ramp.Footnote, null, ThemeKeys.TextSecondary);
        _linkDot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) };
        _linkLine = Ui.Text("", Ramp.Footnote, null, ThemeKeys.TextSecondary);
        var linkGrid = Ui.Columns("Auto", "*");
        linkGrid.Children.Add(Ui.At(_linkDot, 0));
        linkGrid.Children.Add(Ui.At(_linkLine, 1));
        _linkRow = linkGrid;

        var titles = new Stack { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { _allTitle, _picker, _plainTitle, _summary, _linkRow } };
        _root.Children.Add(Ui.At(titles, 0));

        _allToggle = new ChromeToggle
        {
            Width = Metrics.HeaderButton, Height = Metrics.HeaderButton, Padding = new Thickness(0), Content = Icons.Glyph(Icons.AllPrinters, 13),
            CornerRadius = Metrics.Round, Look = Circle,
        };
        AutomationProperties.SetName(_allToggle, Loc.T("All Printers"));
        _allToggle.Click += (_, _) => Ctx.SetShowsAll(_allToggle.IsChecked == true);

        var refreshGlyph = Icons.Glyph(Icons.Refresh, 13);
        refreshGlyph.RenderTransform = _spin;
        refreshGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
        _refresh = HeaderButton(refreshGlyph, Loc.T("Refresh"), Loc.T("Ask the printer for a full status report"));
        _refresh.Click += (_, _) => StartRefresh();
        _spinLimit.Tick += (_, _) => FinishRefresh();

        var settings = HeaderButton(Icons.Glyph(Icons.Settings, 13), Loc.T("Settings"), Loc.T("Settings"));
        settings.Click += (_, _) => Ctx.Navigate(PanelDestination.Settings, null);

        var buttons = new Stack { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(_allToggle);
        buttons.Children.Add(_refresh);
        buttons.Children.Add(settings);
        _root.Children.Add(Ui.At(buttons, 1));
    }

    public override FrameworkElement Root => _root;

    private static readonly ButtonLook Circle = new(ThemeKeys.ControlFill, ThemeKeys.ControlHover, ThemeKeys.SubtleHover);

    private static ChromeButton HeaderButton(UIElement glyph, string name, string help)
    {
        // The Mac's HeaderIconStyle: a circle of faint fill around the icon.
        var button = new ChromeButton
        {
            Width = Metrics.HeaderButton, Height = Metrics.HeaderButton, Padding = new Thickness(0), Content = glyph,
            CornerRadius = Metrics.Round, Look = Circle,
        };
        AutomationProperties.SetName(button, name);
        Ui.Tip(button, help);
        return button;
    }

    protected override void Fill()
    {
        var multiple = Store.HasMultiplePrinters;
        var all = Ctx.ShowsAll() && multiple;
        var configured = Store.IsConfigured;
        _allTitle.Visibility = Show(all);
        _summary.Visibility = Show(all);
        _picker.Visibility = Show(!all && multiple);
        _plainTitle.Visibility = Show(!all && !multiple);
        _linkRow.Visibility = Show(!all);
        if (all) _summary.Text = PanelText.FleetSummary(Store);
        _pickerName.Text = Store.PrinterName;
        AutomationProperties.SetName(_picker, Store.PrinterName);
        _plainTitle.Text = configured ? Store.PrinterName : "Bambu Bar";
        _linkLine.Text = PanelText.LinkLine(Store);
        _linkDot.Fill = Store.Current?.Phase switch
        {
            PrinterConnection.Phase.Online => Palette.Brush(Palette.Accent),
            PrinterConnection.Phase.Connecting => Palette.Brush(Palette.Paused),
            PrinterConnection.Phase.Idle or null => Ctx.Brush(ThemeKeys.TextSecondary),
            _ => Palette.Brush(Palette.Failed),
        };

        _allToggle.Visibility = Show(multiple);
        _allToggle.IsChecked = all;
        Ui.Tip(_allToggle, all ? Loc.T("Show one printer") : Loc.T("Show all printers"));
        _refresh.Visibility = Show(configured);

        // A reply after the request ends the spin.
        if (_requestedAt is { } asked && Store.Current?.LastHeard is { } heard && heard >= asked) FinishRefresh();
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void ShowPrinterMenu()
    {
        var entries = Store.Sessions.Select(s => new MenuEntry(Loc.F("%@ — %@", s.Config.Name, s.ShortState), () => Store.Select(s.Id),
            Checked: s.Id == Store.Current?.Id)).ToList();
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry(Loc.T("Add Printer…"), () => Ctx.Navigate(PanelDestination.AddPrinter, null)));
        MenuPopup.Show(_picker, Ctx.Theme, entries);
    }

    /// <summary>Asks for a full report; the arrow turns until fresh data arrives, or for at most 4 s.</summary>
    private void StartRefresh()
    {
        if (_requestedAt is not null) return;
        _requestedAt = DateTimeOffset.Now;
        if (Ctx.Animate)
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        _spinLimit.Start();
        Store.Refresh();
    }

    private void FinishRefresh()
    {
        _requestedAt = null;
        _spinLimit.Stop();
        if (!Ctx.Animate)
        {
            // Animations turned off mid-turn: stop where it is.
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        // Finish the turn instead of snapping back.
        var angle = _spin.Angle % 360;
        _spin.BeginAnimation(RotateTransform.AngleProperty, angle < 1 ? null
            : new DoubleAnimation(angle, 360, TimeSpan.FromSeconds(0.9 * (360 - angle) / 360)) { FillBehavior = FillBehavior.Stop });
    }

    public void Dispose() => _spinLimit.Stop();
}
