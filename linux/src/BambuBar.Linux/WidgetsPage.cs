using BambuBar.Core;
using BambuBar.Core.App;
namespace BambuBar.Linux;

internal sealed record DesktopWidgetConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? Printer { get; init; }
    public string Size { get; init; } = "medium";
    public string Corner { get; init; } = "top-right";
}
internal sealed partial class Shell
{
    private void WidgetsPage()
    {
        Header(Loc.T("Widgets"), "Live printer cards on your GNOME desktop, beneath your windows.");
        var options = Module();
        Toggle(options, "Show desktop widgets", _settings.Get("linuxWidgetsEnabled", false), enabled => { _settings.Set("linuxWidgetsEnabled", enabled); _desktop?.Queue(); });
        _pageHost.Append(options);
        var help = Label("The Bambu Bar GNOME extension displays these widgets. Keep Bambu Bar running in the background for live updates. Click a widget to open its printer.", "muted"); help.SetWrap(true); _pageHost.Append(help);
        var widgets = _settings.Get<List<DesktopWidgetConfig>>("linuxWidgets") ?? [];
        foreach (var widget in widgets)
        {
            var card = Module(); var title = Row(); var name = Label(Loc.T("Printer"), "heading"); name.SetHexpand(true); title.Append(name);
            title.Append(Button("edit-delete-symbolic", Loc.T("Remove"), () => Save(widgets.Where(w => w.Id != widget.Id).ToList()))); card.Append(title);
            var sessions = _store.Sessions.ToArray();
            var printer = Gtk.DropDown.NewFromStrings([Loc.T("Automatic"), .. sessions.Select(s => s.Config.Name)]);
            printer.SetSelected((uint)Math.Max(0, Array.FindIndex(sessions, s => s.Id == widget.Printer) + 1));
            printer.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") Replace(widget with { Printer = printer.GetSelected() == 0 ? null : sessions[(int)printer.GetSelected() - 1].Id }); };
            card.Append(printer);
            var choices = Row(); var sizes = new[] { "small", "medium", "large" }; var corners = new[] { "top-left", "top-right", "bottom-left", "bottom-right" };
            var size = Gtk.DropDown.NewFromStrings([Loc.T("Small"), Loc.T("Medium"), Loc.T("Large")]); size.SetSelected((uint)Math.Max(0, Array.IndexOf(sizes, widget.Size))); size.SetHexpand(true);
            size.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") Replace(widget with { Size = sizes[(int)size.GetSelected()] }); };
            var corner = Gtk.DropDown.NewFromStrings(["Top left", "Top right", "Bottom left", "Bottom right"]); corner.SetSelected((uint)Math.Max(0, Array.IndexOf(corners, widget.Corner))); corner.SetHexpand(true);
            corner.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") Replace(widget with { Corner = corners[(int)corner.GetSelected()] }); };
            choices.Append(size); choices.Append(corner); card.Append(choices); _pageHost.Append(card);
        }
        var add = Button("list-add-symbolic", "Add widget", () => { widgets.Add(new DesktopWidgetConfig()); _settings.Set("linuxWidgetsEnabled", true); Save(widgets); }, true); add.SetSensitive(widgets.Count < 8); _pageHost.Append(add);
        void Replace(DesktopWidgetConfig replacement) => Save(widgets.Select(w => w.Id == replacement.Id ? replacement : w).ToList());
        void Save(List<DesktopWidgetConfig> value)
        { _settings.Set("linuxWidgets", value); _desktop?.Queue(); GLib.Functions.IdleAdd(200, () => { if (!_disposed && _page == "Widgets") Rebuild(); return false; }); }
    }
}
