using System.Text;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private void ApplyAppearance()
    {
        Adw.StyleManager.GetDefault().SetColorScheme(_settings.Get("linuxAppearance", 0) switch
        { 1 => Adw.ColorScheme.ForceLight, 2 => Adw.ColorScheme.Default, _ => Adw.ColorScheme.ForceDark });
        UpdateAppearanceClass();
    }
    private void UpdateAppearanceClass()
    {
        if (_window is null) return;
        if (Adw.StyleManager.GetDefault().GetDark()) _window.AddCssClass("bambu-dark");
        else _window.RemoveCssClass("bambu-dark");
    }

    private static string Climate(AmsUnit? unit) => string.Join(" · ", new[]
    { unit?.Humidity is { } h ? $"{h}% humidity" : null, unit?.Temperature is { } t ? $"{t:0}°" : null }.OfType<string>());

    private static Gtk.FlowBox CardGrid()
    {
        var grid = Gtk.FlowBox.New(); grid.SetSelectionMode(Gtk.SelectionMode.None); grid.SetHomogeneous(true);
        grid.SetMinChildrenPerLine(1); grid.SetMaxChildrenPerLine(4); grid.SetColumnSpacing(12); grid.SetRowSpacing(12); return grid;
    }
    private Gtk.Image SpoolRing(Func<Spool?> current, int size, List<Action>? updates = null)
    {
        var image = Gtk.Image.New(); image.SetPixelSize(size); image.SetSizeRequest(size, size);
        string? previous = null;
        (updates ?? _updates).Add(() =>
        {
            var s = current(); var color = s?.Color ?? "888888";
            color = color.Length >= 6 && color[..6].All(Uri.IsHexDigit) ? color[..6] : "888888";
            var key = color + s?.IsLoaded; if (previous == key) return; previous = key;
            var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64' viewBox='0 0 64 64'><circle cx='32' cy='32' r='29' fill='none' stroke='#686868' stroke-width='5'/><circle cx='32' cy='32' r='22' fill='#{color}' stroke='#ffffff' stroke-opacity='.2' stroke-width='2'/><circle cx='32' cy='32' r='6' fill='#000000' fill-opacity='.48'/></svg>";
            using var bytes = GLib.Bytes.New(Encoding.UTF8.GetBytes(svg)); image.SetFromGicon(Gio.BytesIcon.New(bytes));
            image.SetTooltipText(s is null ? "Empty" : $"{s.SlotName} · {s.DisplayName} · {s.RemainDetail}");
        }); return image;
    }
    private Gtk.Box SpoolCard(PrinterSession session, int id)
    {
        Spool? Current() => session.Status?.AllSpools.FirstOrDefault(s => s.Id == id);
        var card = Module(); card.AddCssClass("spool-card"); card.SetSizeRequest(145, 174);
        var top = Row(); top.Append(SpoolRing(Current, 62));
        var slot = Live(() => Current()?.SlotName ?? "—", null, "badge"); slot.SetHexpand(true); slot.SetHalign(Gtk.Align.End); slot.SetValign(Gtk.Align.Start); top.Append(slot); card.Append(top);
        card.Append(Live(() => Current()?.DisplayName ?? "—", null, "heading"));
        var maker = Live(() => $"{Current()?.Brand ?? "Generic"} · {Current()?.ColorName ?? "#" + Current()?.Color[..Math.Min(6, Current()?.Color.Length ?? 0)]}", null, "muted", "caption");
        maker.SetEllipsize(Pango.EllipsizeMode.End); card.Append(maker);
        var amount = Live(() => Current()?.GramsLeft is { } g ? Loc.T("Remaining") + "   " + Format.Grams(g) : "", null, "caption"); card.Append(amount);
        var progress = Gtk.ProgressBar.New(); card.Append(progress);
        _updates.Add(() => { var s = Current(); progress.SetVisible(s?.Remain is not null); progress.SetFraction(Math.Clamp((s?.Remain ?? 0) / 100.0, 0, 1)); progress.SetShowText(true); progress.SetText(s?.RemainText ?? ""); });
        return card;
    }
    private Gtk.Box AmsStrip(PrinterSession session, List<Action>? updates = null)
    {
        var strip = Column(10);
        foreach (var unit in session.Status?.Ams ?? [])
        {
            var card = Module(); card.AddCssClass("ams-card"); var header = Row();
            var title = Label(unit.Title.ToUpperInvariant(), "section-title"); title.SetHexpand(true); header.Append(title);
            header.Append(Live(() => Climate(session.Status?.Ams.FirstOrDefault(a => a.Id == unit.Id)), updates, "muted", "caption")); card.Append(header);
            var row = Row(12);
            foreach (var spool in unit.Spools) Add(spool.Id);
            if (unit == session.Status?.Ams.LastOrDefault() && session.Status.ExternalSpool is { IsLoaded: true } external)
            { row.Append(Gtk.Separator.New(Gtk.Orientation.Vertical)); Add(external.Id); }
            card.Append(row); strip.Append(card);
            void Add(int id)
            {
                Spool? Current() => session.Status?.AllSpools.FirstOrDefault(s => s.Id == id);
                var column = Column(4); column.SetHexpand(true); var ring = SpoolRing(Current, 36, updates); ring.SetHalign(Gtk.Align.Center); column.Append(ring);
                var material = Live(() => Current()?.Material ?? "—", updates, "caption", "heading"); material.SetXalign(.5f); column.Append(material);
                var amount = Live(() => Current()?.AmountText ?? "—", updates, "caption", "muted"); amount.SetXalign(.5f); column.Append(amount); row.Append(column);
            }
        }
        if (session.Status?.Ams.Count == 0 && session.Status.ExternalSpool is { IsLoaded: true })
        {
            var card = Module(Label(Loc.T("External spool"), "section-title")); var row = Row();
            row.Append(SpoolRing(() => session.Status?.ExternalSpool, 36, updates));
            row.Append(Live(() => session.Status?.ExternalSpool?.DisplayName ?? "—", updates, "heading"));
            row.Append(Live(() => session.Status?.ExternalSpool?.AmountText ?? "—", updates, "muted")); card.Append(row); strip.Append(card);
        }
        return strip;
    }
    private Gtk.Box IssueCard(PrinterSession session, List<Action>? updates = null)
    {
        var card = Module(); card.AddCssClass("issue-banner"); var row = Row(10);
        row.Append(Gtk.Image.NewFromIconName("dialog-information-symbolic"));
        var text = Column(4); text.SetHexpand(true);
        var message = Live(() => session.Status?.MostSevereIssue?.Message ?? "", updates, "heading", "caption"); message.SetWrap(true); text.Append(message);
        var codeRow = Row(6); codeRow.Append(Live(() => session.Status?.MostSevereIssue?.Id ?? "", updates, "monospace", "muted", "caption"));
        codeRow.Append(Button("edit-copy-symbolic", Loc.T("Copy code"), () => { if (session.Status?.MostSevereIssue is { } issue) Gdk.Display.GetDefault()?.GetClipboard().SetText(issue.Id); }));
        codeRow.Append(Button("help-browser-symbolic", "Wiki", () => { if (session.Status?.MostSevereIssue?.WikiUrl is { } url) Open(url.ToString()); }, true)); text.Append(codeRow);
        row.Append(text); var dismiss = Button("window-close-symbolic", Loc.T("Dismiss until the printer clears it"), () => { if (session.Status?.MostSevereIssue is { } issue) session.Dismiss(issue); }); dismiss.SetValign(Gtk.Align.Start); row.Append(dismiss); card.Append(row);
        (updates ?? _updates).Add(() => card.SetVisible(session.Status?.MostSevereIssue is not null)); return card;
    }
    private Gtk.Box TemperatureStrip(PrinterSession session, bool fans, List<Action>? updates = null)
    {
        var row = Row(10);
        void Tile(string title, Func<string> value)
        { var tile = Module(Label(Loc.T(title), "muted", "caption"), Live(value, updates, "metric")); tile.AddCssClass("temperature-card"); tile.SetHexpand(true); row.Append(tile); }
        Tile(session.Status?.SecondNozzle is null ? "Nozzle" : "Right", () => Temp(session.Status?.Nozzle));
        if (session.Status?.SecondNozzle is not null) Tile("Left", () => Temp(session.Status?.SecondNozzle));
        Tile("Bed", () => Temp(session.Status?.Bed));
        if (session.Status?.Chamber is not null && (fans || _settings.Get(AppSettings.Keys.PanelChamber, true))) Tile("Chamber", () => $"{session.Status?.Chamber:0}°");
        if (fans)
        {
            var box = Module(Label(Loc.T("Fans"), "muted", "caption")); box.SetSpacing(5); box.SetHexpand(true);
            foreach (var field in new (string, Func<int?>)[] { ("Part", () => session.Status?.PartFan), ("Aux", () => session.Status?.AuxFan), (session.Status?.ExhaustFan is null ? "Hotend" : "Chamber", () => session.Status?.ExhaustFan ?? session.Status?.HeatbreakFan) })
            {
                var line = Row(6); line.Append(Label(Loc.T(field.Item1), "muted", "caption"));
                var progress = Gtk.ProgressBar.New(); progress.SetHexpand(true); progress.SetValign(Gtk.Align.Center); line.Append(progress);
                line.Append(Live(() => Percent(field.Item2()), updates, "caption")); (updates ?? _updates).Add(() => progress.SetFraction(Math.Clamp((field.Item2() ?? 0) / 100.0, 0, 1))); box.Append(line);
            }
            row.Append(box);
        }
        return row;
    }
}
