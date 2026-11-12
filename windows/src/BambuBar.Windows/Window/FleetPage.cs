using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>Every printer at a glance (the Mac's FleetPage): a card each, the ones that need someone first, then by
/// soonest finish, then by name; a click opens the printer's page. Cards are kept and updated in place.</summary>
internal sealed class FleetPage(WindowContext ctx) : Page(ctx)
{
    private TextBlock _subtitle = null!;
    private readonly AdaptiveGrid _grid = new() { MinItemWidth = 250, Spacing = 14 };
    private readonly Dictionary<string, FleetCard> _cards = [];

    public override FrameworkElement Build()
    {
        _subtitle = W.Subtitle("");
        var add = W.Button(Loc.T("Add Printer…"), () => Ctx.AddPrinter(null));
        return new Stack { Spacing = 18, Children = { W.PageHeader(Ui.Text(Loc.T("All Printers"), WRamp.PageTitle, FontWeights.SemiBold, display: true), _subtitle, add), _grid } };
    }

    public override void Refresh()
    {
        _subtitle.Text = Loc.P("%lld printers", Store.Sessions.Count) + " · " + PanelText.FleetSummary(Store);
        var order = PanelText.FleetOrder(Store);
        foreach (var gone in _cards.Keys.Except(order.Select(s => s.Id)).ToList()) _cards.Remove(gone);
        var changed = _grid.Children.Count != order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            var session = order[i];
            if (!_cards.TryGetValue(session.Id, out var card))
            {
                card = new FleetCard(Ctx, session.Id);
                _cards[session.Id] = card;
            }
            card.Update(session);
            if (!changed && _grid.Children[i] != card) changed = true;
        }
        if (!changed) return;
        _grid.Children.Clear();
        foreach (var session in order) _grid.Children.Add(_cards[session.Id]);
    }

    /// <summary>One printer: its drawing, name and state, what it's doing, and the progress of its job.</summary>
    private sealed class FleetCard : ChromeButton
    {
        private readonly WindowContext _ctx;
        private readonly Image _art = ArtImage.Element(64, 76);
        private readonly TextBlock _name = Ui.Text("", 14, FontWeights.SemiBold);
        private readonly Border _chip = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _detail = Ui.Text("", WRamp.Note, null, ThemeKeys.TextSecondary);
        private readonly Grid _progress = new() { Height = 26 };
        private readonly ProgressCapsule _bar = new(5) { VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _percent = Ui.Tabular(Ui.Text("", Ramp.Caption, FontWeights.SemiBold));
        private readonly TextBlock _timing = Ui.Tabular(Ui.Text("", Ramp.Caption, null, ThemeKeys.TextSecondary));
        private readonly Border _frame = new() { CornerRadius = Metrics.Card, Padding = new Thickness(12), BorderThickness = new Thickness(1) };
        private string _chipKey = "";

        public FleetCard(WindowContext ctx, string id)
        {
            _ctx = ctx;
            var top = Ui.Columns("*", "Auto");
            top.Children.Add(Ui.At(_name, 0));
            _chip.Margin = new Thickness(6, 0, 0, 0);
            top.Children.Add(Ui.At(_chip, 1));
            var numbers = Ui.Columns("Auto", "*");
            numbers.VerticalAlignment = VerticalAlignment.Bottom;
            numbers.Children.Add(Ui.At(_percent, 0));
            _timing.HorizontalAlignment = HorizontalAlignment.Right;
            _timing.Margin = new Thickness(6, 0, 0, 0);
            numbers.Children.Add(Ui.At(_timing, 1));
            _progress.Children.Add(_bar);
            _progress.Children.Add(numbers);
            var text = new Stack { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { top, _detail, _progress } };
            var grid = Ui.Columns("Auto", "12", "*");
            grid.Children.Add(Ui.At(_art, 0));
            grid.Children.Add(Ui.At(text, 2));
            // The outline is the frame's, so a card that needs attention keeps its tinted edge on hover.
            _frame.Child = grid;
            Content = _frame;
            CornerRadius = Metrics.Card;
            Padding = new Thickness(0);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Click += (_, _) =>
            {
                ctx.Store.Select(id);
                ctx.Show(AppPage.Printer);
            };
        }

        public void Update(PrinterSession session)
        {
            var s = session.Status;
            var online = session.IsOnline && s is not null;
            ArtImage.Show(_art, ArtImage.Scene(s, session.Config.ModelName, session.ChamberLightOn, dimmed: !online));
            _name.Text = session.Config.Name;
            var chipKey = $"{PanelText.ChipText(session)}|{s?.State}|{s?.Canceled}|{online}|{session.Phase.GetType().Name}";
            if (chipKey != _chipKey)
            {
                _chipKey = chipKey;
                _chip.Child = StateChip.For(_ctx, session);
            }
            var detail = PanelText.FleetDetail(session);
            _detail.Text = detail;
            var showsProgress = online && (s!.State.IsActive() || s.State == JobState.Finished);
            // Kept at the same height without a job, so the cards line up.
            _progress.Opacity = showsProgress ? 1 : 0;
            if (showsProgress)
            {
                _bar.Tint = Palette.For(s!);
                _bar.SetProgress(s!.Progress / 100.0, false);
                _percent.Text = $"{s.Progress}%";
                _timing.Text = PanelText.FleetTiming(s);
            }
            var attention = PanelText.NeedsAttention(session);
            var tint = s is null ? Palette.Failed : Palette.For(s);
            Look = attention
                ? ButtonLook.Tinted(tint, 0.12, 0.17, 0.22) with { Stroke = null }
                : new ButtonLook(ThemeKeys.CardFill, ThemeKeys.ControlHover, ThemeKeys.ControlPressed);
            if (attention) _frame.BorderBrush = Palette.Brush(Palette.WithAlpha(tint, 0.55));
            else _frame.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.CardStroke);
            AutomationProperties.SetName(this, session.Config.Name);
            AutomationProperties.SetItemStatus(this, session.ShortState);
            Ui.Tip(this, Loc.F("%@: %@", session.Config.Name, detail));
        }
    }
}
