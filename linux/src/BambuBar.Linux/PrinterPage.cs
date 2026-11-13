using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private void PrinterPage()
    {
        var session = _store.Current!;
        var heading = Header(session.Config.Name, $"Bambu Lab {session.Config.ModelName} · {session.Config.Host} · Firmware {session.Status?.Firmware ?? "—"}");
        heading.Append(Button("view-refresh-symbolic", Loc.T("Refresh"), _store.Refresh));
        if (_store.HasMultiplePrinters)
        {
            var selector = Gtk.DropDown.NewFromStrings(_store.Sessions.Select(s => s.Config.Name).ToArray());
            selector.SetSelected((uint)_store.Sessions.ToList().IndexOf(session));
            selector.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected" && selector.GetSelected() < _store.Sessions.Count) _store.Select(_store.Sessions[(int)selector.GetSelected()].Id); };
            _pageHost.Append(selector);
        }
        if (!session.IsOnline)
        {
            var connection = Module(Label(session.LinkSummary, "heading"));
            var explanation = Live(() => session.LinkDetail ?? "", null, "muted"); explanation.SetWrap(true); connection.Append(explanation);
            connection.Append(Button("network-connect-symbolic", Loc.T("Connect"), () => BeginSetup(session.Config), true)); _pageHost.Append(connection);
        }
        if (_store.ReplacementCode is { } replacement)
            _pageHost.Append(Button("view-refresh-symbolic", Loc.T("Enter the access code again in the app"), () => Async(async () => { if (!_ephemeral) await _secrets.Save(replacement.Serial, replacement.AccessCodes[0]); _store.UseReplacementCode(); }), true));
        _pageHost.Append(JobCard(session, _updates));
        _pageHost.Append(IssueCard(session));
        _pageHost.Append(Label(Loc.T("TEMPERATURES & AIRFLOW"), "section-title"));
        _pageHost.Append(TemperatureStrip(session, true));
        _pageHost.Append(Label(Loc.T("FILAMENT"), "section-title"));
        _pageHost.Append(AmsStrip(session));
        if (session.ShowsCamera) AddCamera(session);
        _pageHost.Append(Label(Loc.T("PRINTER"), "section-title"));
        var row = Row(); var left = Module(); left.SetHexpand(true); var right = Module(); right.SetHexpand(true);
        Detail(left, "Speed", () => session.Speed.Title());
        Detail(left, "Nozzle", () => session.Status?.NozzleDiameter is { } d ? $"{d:0.0} mm · {session.Status?.NozzleType}" : "—");
        Detail(left, "Door", () => Loc.T(session.Status?.DoorOpen == true ? "Open" : "Closed"));
        Detail(left, "Light", () => Loc.T(session.ChamberLightOn ? "On" : "Off"));
        Detail(right, "Network", () => session.Status?.WifiSignal is { } signal ? $"Wi-Fi ({signal} dBm)" : session.LinkSummary);
        Detail(right, "Storage", () => Loc.T(session.Status?.HasSdCard == true ? "SD card inserted" : "No SD card"));
        Detail(right, "Live View", () => Loc.T(session.ShowsCamera ? "On" : "Off"));
        Detail(right, "Firmware", () => session.Status?.Firmware ?? "—");
        row.Append(left); row.Append(right); _pageHost.Append(row);
        if (session.Config.Model is { } model)
        {
            var specs = Module(); Detail(specs, "Build volume", () => model.BuildVolumeText);
            Detail(specs, "Chamber", () => model.HasChamberSensor ? Loc.T("Enclosed, with temperature sensor") : "—");
            _pageHost.Append(specs);
        }
        var history = _store.HistoryFor(session.Id);
        if (history.Count > 0)
        {
            var recent = Module(Label(Loc.T("Last print"), "section-title"));
            foreach (var job in history) recent.Append(Label($"{job.Name} · {job.Outcome.Title()}"));
            _pageHost.Append(recent);
        }
    }
    private static string Temp(Temperature? temperature) => temperature is { } t ? t.Target > 0 && !t.IsAtTarget ? $"{t.Current:0}° → {t.Target:0}°" : $"{t.Current:0}°" : "—";
    private static string Percent(int? value) => value is >= 0 and { } v ? $"{v}%" : "—";

    private Gtk.Box JobCard(PrinterSession session, List<Action> updates, bool compact = false)
    {
        var card = Module(); card.AddCssClass("job");
        var stateClass = "";
        updates.Add(() => { var next = session.Status?.State switch { JobState.Failed => "state-failed", JobState.Paused => "state-paused", JobState.Finished => "state-finished", JobState.Printing or JobState.Preparing => "state-printing", _ => "state-idle" }; if (next == stateClass) return; if (stateClass.Length > 0) card.RemoveCssClass(stateClass); card.AddCssClass(next); stateClass = next; });
        var status = Live(() => "● " + session.ShortState, updates, "state-label", "badge"); status.SetHalign(Gtk.Align.Start);
        var main = Row(18);
        if (!compact)
        {
            var model = new ModelView(session, false); main.Append(model.Widget); updates.Add(model.Update); _cleanup.Add(model.Dispose);
        }
        else
        {
            var picture = Gtk.Image.New(); picture.SetPixelSize(110); picture.SetSizeRequest(110, 140);
            picture.SetTooltipText(session.Config.ModelName + " printer");
            var art = Assets.PathOf("Printers", (session.Config.Model?.Shape ?? PrinterShape.Enclosed).AssetName() + "-body.png");
            if (File.Exists(art)) { using var bytes = GLib.Bytes.New(File.ReadAllBytes(art)); picture.SetFromGicon(Gio.BytesIcon.New(bytes)); } main.Append(picture);
            var last = ""; var generation = 0;
            updates.Add(() =>
            {
                var s = session.Status;
                var spools = s?.Ams.SelectMany(a => a.Spools).Select(p => p.IsLoaded ? p.Color : null).ToArray() ?? [];
                var key = $"{s?.State}:{s?.Progress}:{s?.ActiveSpool?.Color}:{session.ChamberLightOn}:{session.IsOnline}:" + string.Join(',', spools);
                if (key == last) return; last = key; var mine = ++generation;
                var scene = new PrinterArt.Scene { Shape = session.Config.Model?.Shape ?? PrinterShape.Enclosed, State = s?.State ?? JobState.Idle, Progress = (s?.Progress ?? 0) / 100.0, Filament = s?.ActiveSpool?.Color, Spools = spools, LightOn = session.ChamberLightOn, Dimmed = !session.IsOnline };
                _ = Render();
                async Task Render()
                {
                    var rgba = await Task.Run(() => PrinterArt.Render(scene, 220, 280));
                    if (mine != generation || rgba is null || _disposed) return;
                    using var bytes = GLib.Bytes.New(Png.Encode(rgba));
                    picture.SetFromGicon(Gio.BytesIcon.New(bytes));
                }
            });
        }
        var info = Column(8); info.SetHexpand(true); info.SetValign(Gtk.Align.Center);
        var top = Row(); status.SetHexpand(true); top.Append(status);
        if (session.Config.Model?.HasChamberLight == true || session.Status?.ChamberLightOn is not null)
        {
            var light = Button("display-brightness-symbolic", Loc.T("Light"), session.ToggleLight, true); light.AddCssClass("pill"); top.Append(light);
            updates.Add(() => light.SetSensitive(session.IsOnline));
        }
        info.Append(top);
        var job = Live(() => session.Status?.JobName is { Length: > 0 } name ? name : session.Status is null ? session.LinkSummary : Loc.T("Ready to print"), updates, "job-title");
        job.SetEllipsize(Pango.EllipsizeMode.End); info.Append(job);
        var stage = Live(() => session.Status is { State: JobState.Failed } failed ? Loc.F("Stopped at layer %lld of %lld", failed.Layer, failed.TotalLayers) : session.Status?.DetailText ?? session.LinkSummary, updates, "muted"); stage.SetEllipsize(Pango.EllipsizeMode.End); info.Append(stage);
        var numbers = Row(); var percent = Live(() => session.Status is { } s && session.IsOnline ? $"{s.Progress}%" : "—", updates, "progress-number"); percent.SetHexpand(true); numbers.Append(percent);
        var layers = Live(() => session.Status is { TotalLayers: > 0 } s ? $"{s.Layer}/{s.TotalLayers}\nlayers" : "", updates, "muted", "caption"); layers.SetXalign(1); layers.SetValign(Gtk.Align.End); numbers.Append(layers); info.Append(numbers);
        var progress = Gtk.ProgressBar.New(); updates.Add(() => progress.SetFraction(Math.Clamp((session.Status?.Progress ?? 0) / 100.0, 0, 1))); info.Append(progress);
        info.Append(Live(() => session.Status?.State == JobState.Failed ? "⚠ " + Loc.T("Needs attention") : session.Status is { } s && s.State.IsActive() ? Format.Duration(s.RemainingMinutes) + " " + Loc.T("left") : session.LinkSummary, updates, "job-detail"));
        main.Append(info); card.Append(main);
        var locked = Live(() => Loc.T("Enable Developer Mode on the printer to use controls"), updates, "muted", "caption"); locked.SetWrap(true); card.Append(locked);
        updates.Add(() => locked.SetVisible(_store.ShowPrinterActions && session.Status is { ControlsLocked: true } active && active.State.IsActive()));
        var controls = Row(8);
        var pause = Button("media-playback-pause-symbolic", Loc.T("Pause"), () => { if (session.Status?.State == JobState.Paused) _store.Resume(); else _store.Pause(); }, true);
        var stop = Button("media-playback-stop-symbolic", Loc.T("Stop"), () =>
        {
            if (_store.ConfirmStop) Confirm(Loc.T("Stop this print?"), session.Status?.JobName ?? "", Loc.T("Stop"), () => session.StopPrint());
            else session.StopPrint();
        }, true);
        pause.SetHexpand(true); stop.SetHexpand(true); controls.Append(pause); controls.Append(stop);
        updates.Add(() =>
        {
            var s = session.Status;
            pause.SetSensitive(session.IsOnline && s is { ControlsLocked: false } && s.State is JobState.Printing or JobState.Paused);
            pause.SetTooltipText(Loc.T(s?.ControlsLocked == true ? "Enable Developer Mode on the printer to use controls" : s?.State == JobState.Paused ? "Resume" : "Pause"));
            if (pause.GetChild() is Gtk.Box box && box.GetLastChild() is Gtk.Label label) label.SetText(Loc.T(s?.State == JobState.Paused ? "Resume" : "Pause"));
            stop.SetSensitive(session.IsOnline && s is { ControlsLocked: false } && s.State.IsActive());
        });
        var speed = Gtk.DropDown.NewFromStrings(Enum.GetValues<SpeedLevel>().Select(s => s.Title()).ToArray());
        updates.Add(() => { speed.SetSensitive(session.IsOnline && session.Status?.ControlsLocked == false); speed.SetSelected((uint)Array.IndexOf(Enum.GetValues<SpeedLevel>(), session.Speed)); });
        speed.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected" && speed.GetSelected() < 4 && Enum.GetValues<SpeedLevel>()[(int)speed.GetSelected()] != session.Speed) _store.SetSpeed(Enum.GetValues<SpeedLevel>()[(int)speed.GetSelected()]); };
        controls.Append(speed); card.Append(controls);
        updates.Add(() => controls.SetVisible(_store.ShowPrinterActions && session.Status?.State.IsActive() == true));
        foreach (var update in updates.ToArray()) update();
        return card;
    }
    private void QuickViewPage()
    {
        Header(Loc.T("Menu Bar"), Loc.T("What the menu bar shows while you print, and what you see when you click it."));
        var layout = Row(18); var options = Column(16); options.SetSizeRequest(280, -1); options.SetHexpand(true);
        var style = Gtk.DropDown.NewFromStrings(new[] { Loc.T("Icon only"), Loc.T("Progress"), Loc.T("Time left"), Loc.T("Progress and time left") });
        style.SetSelected((uint)Array.IndexOf(Enum.GetValues<TrayStyle>(), _store.MenuBarStyle));
        style.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected" && style.GetSelected() < Enum.GetValues<TrayStyle>().Length) { _store.MenuBarStyle = Enum.GetValues<TrayStyle>()[(int)style.GetSelected()]; _desktop?.Queue(); } };
        options.Append(Module(Label(Loc.T("While printing, show"), "heading"), style));
        var content = Module(Label(Loc.T("What to show"), "heading"));
        void Update(string key, bool value) { _settings.Set(key, value); BuildPanel(); GLib.Functions.IdleAdd(200, () => { Rebuild(); return false; }); }
        Toggle(content, "Temperatures", _settings.Get(AppSettings.Keys.PanelTemperatures, true), value => Update(AppSettings.Keys.PanelTemperatures, value));
        Toggle(content, "Chamber temperature", _settings.Get(AppSettings.Keys.PanelChamber, true), value => Update(AppSettings.Keys.PanelChamber, value));
        Toggle(content, "Fans", _settings.Get(AppSettings.Keys.PanelFans, false), value => Update(AppSettings.Keys.PanelFans, value));
        Toggle(content, "Filament", _settings.Get(AppSettings.Keys.PanelFilament, true), value => Update(AppSettings.Keys.PanelFilament, value));
        Toggle(content, "Pause, stop and speed", _store.ShowPrinterActions, value => { _store.ShowPrinterActions = value; BuildPanel(); });
        options.Append(content); options.SetValign(Gtk.Align.Start); layout.Append(options);
        if (_store.Current is { } session)
        {
            var preview = Column(10); preview.SetSizeRequest(330, -1); preview.SetHexpand(true); preview.SetValign(Gtk.Align.Start); preview.AddCssClass("menu-preview");
            preview.Append(Label(session.Config.Name, "heading")); preview.Append(JobCard(session, _updates, compact: true));
            if (_settings.Get(AppSettings.Keys.PanelTemperatures, true)) preview.Append(TemperatureStrip(session, false));
            if (_settings.Get(AppSettings.Keys.PanelFilament, true)) preview.Append(AmsStrip(session));
            if (_settings.Get(AppSettings.Keys.PanelFans, false)) preview.Append(Live(() => DesktopPanel.FanText(session.Status), null, "caption", "muted"));
            preview.Append(Label(Loc.T("Live preview · changes appear here as you make them"), "caption", "muted")); layout.Append(preview);
        }
        _pageHost.Append(layout);
        if (_store.HasMultiplePrinters)
        {
            var sessions = _store.Sessions.ToArray();
            var selector = Gtk.DropDown.NewFromStrings([Loc.T("Automatic"), .. sessions.Select(s => s.Config.Name)]);
            selector.SetSelected((uint)Math.Max(0, Array.FindIndex(sessions, s => s.Id == _store.MenuBarSerial) + 1));
            selector.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") _store.MenuBarSerial = selector.GetSelected() == 0 ? null : sessions[(int)selector.GetSelected() - 1].Id; };
            _pageHost.Append(Module(Label(Loc.T("Printer"), "section-title"), selector));
        }
    }
    private void PreparePanel()
    {
        if (_panel is not null) return;
        _panel = Adw.ApplicationWindow.New(_app); WindowSize.Track(_panel);
        _panel.SetTitle("Bambu Bar — Quick view"); _panel.SetDefaultSize(420, 500); _panel.SetHideOnClose(true); _panel.SetResizable(false);
        _panel.Realize(); BuildPanel();
        var keys = Gtk.EventControllerKey.New(); keys.OnKeyPressed += (_, e) => { if (e.Keyval != 65307) return false; _panel.SetVisible(false); return true; }; _panel.AddController(keys);
        _panel.OnNotify += (_, e) => { if (e.Pspec.GetName() == "is-active" && !_panel.GetIsActive()) _panel.SetVisible(false); };
    }
    private void ShowPanel()
    {
        PreparePanel();
        if (_store.MenuBarSession is { } session) _store.Select(session.Id);
        BuildPanel();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _panel!.Present();
        if (_panel.GetFrameClock() is { } clock)
        {
            GObject.SignalHandler<Gdk.FrameClock>? painted = null;
            painted = (_, _) => { clock.OnAfterPaint -= painted; if (!_ephemeral) Platform.Log($"panel first frame {watch.Elapsed.TotalMilliseconds:0.0} ms"); };
            clock.OnAfterPaint += painted;
        }
        Refresh();
    }
    private void BuildPanel()
    {
        _desktop?.Queue();
        if (_panel is null) return;
        _panelSchema = Schema;
        _panelUpdates.Clear();
        var root = Column(12); root.AddCssClass("quick-panel"); root.SetMarginTop(12); root.SetMarginBottom(14); root.SetMarginStart(14); root.SetMarginEnd(14);
        var header = Row(); var title = Label(_store.PrinterName, "heading"); title.SetHexpand(true); header.Append(title);
        header.Append(Button("window-new-symbolic", Loc.T("Open Bambu Bar"), Present)); header.Append(Button("application-exit-symbolic", Loc.T("Quit"), _app.Quit)); root.Append(header);
        if (_store.Current is { } session)
        {
            root.Append(JobCard(session, _panelUpdates, compact: true));
            root.Append(IssueCard(session, _panelUpdates));
            if (_settings.Get(AppSettings.Keys.PanelTemperatures, true)) root.Append(TemperatureStrip(session, false, _panelUpdates));
            if (_settings.Get(AppSettings.Keys.PanelFilament, true)) root.Append(AmsStrip(session, _panelUpdates));
            if (_settings.Get(AppSettings.Keys.PanelFans, false)) root.Append(Live(() => DesktopPanel.FanText(session.Status), _panelUpdates, "caption", "muted"));
        }
        else root.Append(Button("list-add-symbolic", Loc.T("Add Printer…"), Present, true));
        var scroll = Gtk.ScrolledWindow.New(); scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic); scroll.SetPropagateNaturalHeight(true);
        var availableHeight = 700;
        if (_panel.GetSurface() is { } surface && Gdk.Display.GetDefault()?.GetMonitorAtSurface(surface) is { } monitor)
        {
            monitor.GetGeometry(out var geometry); using (geometry) availableHeight = Math.Max(300, geometry.Height - 70);
        }
        scroll.SetMaxContentHeight(availableHeight); scroll.SetChild(root); _panel.SetContent(scroll);
    }
}
