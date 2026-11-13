using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private void QuickViewPage()
    {
        Header(Loc.T("Menu Bar"), Loc.T("What the menu bar shows while you print, and what you see when you click it."));
        if (Platform.IsPlasma) _pageHost.Append(Note("Plasma shows a tray icon; progress and time appear in its tooltip, not as panel text. Click the icon for Quick view, middle-click to open Bambu Bar, or right-click for Open and Quit. The preview below illustrates the content, not a native Plasma popup."));
        var layout = Gtk.FlowBox.New(); layout.SetSelectionMode(Gtk.SelectionMode.None); layout.SetMinChildrenPerLine(1); layout.SetMaxChildrenPerLine(2);
        layout.AddCssClass("menu-layout"); layout.SetColumnSpacing(16); layout.SetRowSpacing(18); layout.SetValign(Gtk.Align.Start); layout.SetHalign(Gtk.Align.Start);
        var options = Column(22); options.SetSizeRequest(300, -1); options.SetHexpand(false); options.SetValign(Gtk.Align.Start);
        var example = Column(10); example.SetSizeRequest(390, -1); example.SetValign(Gtk.Align.Start);
        var frame = Column(0); frame.AddCssClass("preview-wallpaper"); example.Append(frame);
        var caption = Note("Sample print. Changes show here as you make them."); caption.SetXalign(.5f); caption.SetJustify(Gtk.Justification.Center); example.Append(caption);
        var topBar = Row(8); topBar.AddCssClass("sample-topbar"); var spacer = Label(""); spacer.SetHexpand(true); topBar.Append(spacer);
        var indicator = Row(5); indicator.AddCssClass("sample-indicator"); var printerIcon = Gtk.Image.NewFromGicon(Gio.FileIcon.New(Gio.FileHelper.NewForPath(Path.Combine(AppContext.BaseDirectory, "tray-icons", TrayIcon.Name("printing-10") + ".svg")))); printerIcon.SetPixelSize(18); indicator.Append(printerIcon);
        var indicatorText = Label("", "heading"); indicator.Append(indicatorText); topBar.Append(indicator);
        var wifi = Gtk.Image.NewFromIconName("network-wireless-signal-excellent-symbolic"); wifi.SetPixelSize(14); topBar.Append(wifi); topBar.Append(Label("Fri 12:26", "caption", "heading")); frame.Append(topBar);
        var panel = Column(10); panel.AddCssClass("sample-panel"); frame.Append(panel);
        var header = Row(8); var identity = Column(1); identity.SetHexpand(true); identity.Append(Label(_store.Current?.Config.Name ?? "Bambulab P2S", "heading"));
        identity.Append(Label("● " + (_store.Current?.Config.ModelName ?? "P2S") + " · " + Loc.T("Wi-Fi excellent"), "caption", "muted")); header.Append(identity);
        header.Append(PreviewControl("view-refresh-symbolic", "")); header.Append(PreviewControl("emblem-system-symbolic", "")); panel.Append(header);
        var hero = Module(); hero.AddCssClass("sample-job"); var main = Row(12); main.Append(PreviewArt(JobState.Printing, .5, 92));
        var info = Column(5); info.SetHexpand(true); var state = Label("● " + Loc.T("Printing"), "badge", "sample-printing"); state.SetHalign(Gtk.Align.Start);
        var heroTop = Row(); state.SetHexpand(true); heroTop.Append(state); heroTop.Append(PreviewControl("display-brightness-symbolic", "Light")); info.Append(heroTop);
        info.Append(Label("Articulated Dragon", "heading")); info.Append(Label("PLA Matte · A1", "caption", "muted"));
        var numbers = Row(); var percent = Label("50%", "sample-percent"); percent.SetHexpand(true); numbers.Append(percent); var layers = Label("144/284\n" + Loc.T("layers"), "caption", "muted"); layers.SetXalign(1); layers.SetValign(Gtk.Align.End); numbers.Append(layers); info.Append(numbers);
        var progress = Gtk.ProgressBar.New(); progress.SetFraction(.5); info.Append(progress);
        var timing = Row(); var remaining = Label("◷ 1h 12m " + Loc.T("left"), "caption", "heading"); remaining.SetHexpand(true); timing.Append(remaining); timing.Append(Label(Loc.T("Done") + " 13:38", "caption", "muted")); info.Append(timing);
        main.Append(info); hero.Append(main); panel.Append(hero);
        var temperatures = SampleTemperatures(true); var fans = Module(Label(Loc.T("Fans") + "    70% · 40% · 50%", "caption", "muted")); var filament = SampleSpools();
        panel.Append(temperatures); panel.Append(fans); panel.Append(filament);
        var controls = Row(8); var pause = PreviewControl("media-playback-pause-symbolic", "Pause"); pause.SetHexpand(true); controls.Append(pause);
        var stop = PreviewControl("media-playback-stop-symbolic", "Stop", "sample-stop"); stop.SetHexpand(true); controls.Append(stop); controls.Append(PreviewControl("speedometer-symbolic", "")); panel.Append(controls);
        var footer = Row(); var open = PreviewControl("window-new-symbolic", "Open Bambu Bar"); open.SetHexpand(true); footer.Append(open); footer.Append(PreviewControl("application-exit-symbolic", "Quit")); footer.AddCssClass("sample-footer"); panel.Append(footer);
        var styles = Module(Label(Loc.T("While printing, show"), "heading")); styles.SetSpacing(4);
        Gtk.CheckButton? group = null;
        var styleNames = new[] { "Icon only", "Progress", "Time left", "Progress and time left" };
        foreach (var (value, index) in Enum.GetValues<TrayStyle>().Select((value, index) => (value, index)))
        {
            var radio = Gtk.CheckButton.NewWithLabel(Loc.T(styleNames[index])); if (group is null) group = radio; else radio.SetGroup(group);
            radio.SetActive(_store.MenuBarStyle == value);
            radio.OnToggled += (_, _) => { if (!radio.GetActive()) return; _store.MenuBarStyle = value; _desktop?.Queue(); RefreshPreview(); }; styles.Append(radio);
        }
        options.Append(SettingsSection("Menu bar", "view-dual-symbolic", styles));
        var content = Module();
        void Update(string key, bool value) { _settings.Set(key, value); BuildPanel(); RefreshPreview(); }
        Toggle(content, "Temperatures", _settings.Get(AppSettings.Keys.PanelTemperatures, true), value => Update(AppSettings.Keys.PanelTemperatures, value));
        Toggle(content, "Chamber temperature", _settings.Get(AppSettings.Keys.PanelChamber, true), value => Update(AppSettings.Keys.PanelChamber, value));
        Toggle(content, "Fans", _settings.Get(AppSettings.Keys.PanelFans, false), value => Update(AppSettings.Keys.PanelFans, value));
        Toggle(content, "Filament", _settings.Get(AppSettings.Keys.PanelFilament, true), value => Update(AppSettings.Keys.PanelFilament, value));
        content.Append(Note("What the panel under the menu bar icon shows below the print. The chamber temperature appears only for printers with a chamber sensor (not the P1 and A series)."));
        content.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        Toggle(content, "Pause, stop and speed", _store.ShowPrinterActions, value => { _store.ShowPrinterActions = value; BuildPanel(); RefreshPreview(); });
        content.Append(Note("Show pause, resume, stop and print speed controls in the menu bar panel and main window, and print actions in the Printer menu."));
        for (var child = content.GetFirstChild(); child is not null; child = child.GetNextSibling())
            if (child is Gtk.Label note) note.SetMaxWidthChars(36);
        options.Append(SettingsSection("What to show", "view-list-symbolic", content));
        layout.Insert(options, -1); layout.Insert(example, -1); _pageHost.Append(layout);
        if (_store.HasMultiplePrinters)
        {
            var sessions = _store.Sessions.ToArray();
            var selector = Gtk.DropDown.NewFromStrings([Loc.T("Automatic"), .. sessions.Select(s => s.Config.Name)]);
            selector.SetSelected((uint)Math.Max(0, Array.FindIndex(sessions, s => s.Id == _store.MenuBarSerial) + 1));
            selector.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") _store.MenuBarSerial = selector.GetSelected() == 0 ? null : sessions[(int)selector.GetSelected() - 1].Id; };
            _pageHost.Append(Module(SettingRow("Printer", selector)));
        }
        RefreshPreview();
        void RefreshPreview()
        {
            var sample = new PrinterStatus { State = JobState.Printing, Progress = 50, RemainingMinutes = 72 };
            indicatorText.SetText(_store.MenuBarStyle.Text(sample) ?? ""); indicatorText.SetVisible(_store.MenuBarStyle != TrayStyle.Icon);
            temperatures.SetVisible(_settings.Get(AppSettings.Keys.PanelTemperatures, true));
            temperatures.GetLastChild()?.SetVisible(_settings.Get(AppSettings.Keys.PanelChamber, true));
            fans.SetVisible(_settings.Get(AppSettings.Keys.PanelFans, false)); filament.SetVisible(_settings.Get(AppSettings.Keys.PanelFilament, true)); controls.SetVisible(_store.ShowPrinterActions);
        }
    }
}
