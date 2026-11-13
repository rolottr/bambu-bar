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
        var heading = Header(session.Config.Name, session.Config.ModelName);
        if (session.ShowsCamera) heading.Append(Button("camera-video-symbolic", Loc.T("Camera"), () => OpenCamera(session)));
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
        var temps = Row();
        Gtk.Box Tile(string title, Func<string> text)
        {
            var box = Column(6); box.AddCssClass("tile"); box.SetHexpand(true); box.Append(Label(Loc.T(title), "muted")); box.Append(Live(text, null, "metric")); return box;
        }
        var status = session.Status;
        temps.Append(Tile(status?.SecondNozzle is null ? "Nozzle" : "Right", () => Temp(session.Status?.Nozzle)));
        if (status?.SecondNozzle is not null) temps.Append(Tile("Left", () => Temp(session.Status?.SecondNozzle)));
        temps.Append(Tile("Bed", () => Temp(session.Status?.Bed)));
        if (session.Config.Model?.HasChamberSensor == true || status?.Chamber is not null)
            temps.Append(Tile("Chamber", () => session.Status?.Chamber is { } t ? $"{t:0}°" : "—"));
        _pageHost.Append(temps);
        var fans = Module(Label(Loc.T("Fans"), "section-title"));
        Detail(fans, "Part cooling", () => Percent(session.Status?.PartFan));
        if (session.Config.Model?.HasAuxFan == true) Detail(fans, "Auxiliary", () => Percent(session.Status?.AuxFan));
        if (session.Config.Model?.HasChamberFan == true) Detail(fans, "Chamber", () => Percent(session.Status?.ExhaustFan));
        _pageHost.Append(fans);
        if (session.ShowsCamera) AddCamera(session);
        var details = Module(Label(Loc.T("Printer"), "section-title"));
        Detail(details, "Speed", () => session.Speed.Title());
        if (session.Status?.NozzleDiameter is not null) Detail(details, "Nozzle", () => $"{session.Status?.NozzleDiameter:0.0} mm · {session.Status?.NozzleType}");
        if (session.Status?.DoorOpen is not null) Detail(details, "Door", () => Loc.T(session.Status?.DoorOpen == true ? "Open" : "Closed"));
        Detail(details, "Network", () => session.Status?.WifiSignal is { } signal ? $"{signal} dBm" : "—");
        Detail(details, "Address", () => session.Config.Host);
        Detail(details, "Firmware", () => session.Status?.Firmware ?? "—");
        Detail(details, "Storage", () => session.Status?.HasSdCard is { } has ? Loc.T(has ? "SD card inserted" : "No SD card") : "—");
        if (session.Config.Model is { } model) Detail(details, "Build volume", () => model.BuildVolumeText);
        _pageHost.Append(details);
        var issues = Module(Label(Loc.T("Errors"), "section-title"));
        var issueText = Live(() => session.Status?.MostSevereIssue?.Message ?? "—"); issueText.SetWrap(true); issues.Append(issueText);
        var issueActions = Row(8);
        var wiki = Button("help-browser-symbolic", Loc.T("Open"), () => { if (session.Status?.MostSevereIssue?.WikiUrl is { } url) Open(url.ToString()); }, true);
        var copy = Button("edit-copy-symbolic", Loc.T("Copy code"), () => { if (session.Status?.MostSevereIssue is { } issue) Gdk.Display.GetDefault()?.GetClipboard().SetText(issue.Id); }, true);
        var dismiss = Button("window-close-symbolic", Loc.T("Dismiss until the printer clears it"), () => { if (session.Status?.MostSevereIssue is { } issue) session.Dismiss(issue); });
        issueActions.Append(wiki); issueActions.Append(copy); issueActions.Append(dismiss); issues.Append(issueActions);
        _updates.Add(() => { wiki.SetSensitive(session.Status?.MostSevereIssue?.WikiUrl is not null); copy.SetSensitive(session.Status?.MostSevereIssue is not null); dismiss.SetSensitive(session.Status?.MostSevereIssue is not null); });
        _pageHost.Append(issues);
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
        var status = Live(() => session.ShortState, updates, "state-label", "heading"); card.Append(status);
        var main = Row(18);
        if (!compact)
        {
            var model = new ModelView(session, false); main.Append(model.Widget); updates.Add(model.Update); _cleanup.Add(model.Dispose);
        }
        else
        {
            var picture = Gtk.Picture.New(); picture.SetSizeRequest(110, 140); picture.SetCanShrink(true); picture.SetContentFit(Gtk.ContentFit.Contain);
            picture.SetAlternativeText(session.Config.ModelName + " printer");
            var art = Assets.PathOf("Printers", (session.Config.Model?.Shape ?? PrinterShape.Enclosed).AssetName() + "-body.png");
            if (File.Exists(art)) picture.SetFilename(art); main.Append(picture);
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
                    using var bytes = GLib.Bytes.New(rgba.Pixels);
                    using var texture = Gdk.MemoryTexture.New(rgba.Width, rgba.Height, Gdk.MemoryFormat.R8g8b8a8, bytes, (nuint)(rgba.Width * 4));
                    picture.SetPaintable(texture);
                }
            });
        }
        var info = Column(10); info.SetHexpand(true); info.SetValign(Gtk.Align.Center);
        var job = Live(() => session.Status?.JobName is { Length: > 0 } name ? name : session.Status is null ? session.LinkSummary : Loc.T("Ready to print"), updates, "heading"); job.SetWrap(true); info.Append(job);
        info.Append(Live(() => session.Status is { } s && session.IsOnline ? $"{s.Progress}%" : "—", updates, "progress-number"));
        info.Append(Live(() => session.Status is { } s && s.State.IsActive() ? Format.Duration(s.RemainingMinutes) + " " + Loc.T("left") : session.LinkSummary, updates, "muted"));
        var progress = Gtk.ProgressBar.New(); updates.Add(() => progress.SetFraction(Math.Clamp((session.Status?.Progress ?? 0) / 100.0, 0, 1))); info.Append(progress);
        var detail = Live(() => session.Status?.StageText ?? session.LinkDetail ?? "", updates, "muted"); detail.SetWrap(true); info.Append(detail);
        info.Append(Live(() => session.Status is { TotalLayers: > 0 } s ? Loc.F("Layer %lld of %lld", s.Layer, s.TotalLayers) : "", updates, "muted"));
        main.Append(info); card.Append(main);
        var locked = Live(() => session.Status?.ControlsLocked == true ? Loc.T("Turn on LAN Only and Developer Mode on the printer to control prints from Bambu Bar.") : "", updates, "muted");
        locked.SetWrap(true); card.Append(locked);
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
        if (session.Config.Model?.HasChamberLight == true || session.Status?.ChamberLightOn is not null)
        {
            var light = Button("display-brightness-symbolic", Loc.T("Light"), _store.ToggleLight);
            updates.Add(() => { light.SetSensitive(session.IsOnline); light.SetTooltipText(Loc.T(session.ChamberLightOn ? "Turn light off" : "Turn light on")); }); controls.Append(light);
        }
        var speed = Gtk.DropDown.NewFromStrings(Enum.GetValues<SpeedLevel>().Select(s => s.Title()).ToArray());
        updates.Add(() => { speed.SetSensitive(session.IsOnline && session.Status?.ControlsLocked == false); speed.SetSelected((uint)Array.IndexOf(Enum.GetValues<SpeedLevel>(), session.Speed)); });
        speed.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected" && speed.GetSelected() < 4 && Enum.GetValues<SpeedLevel>()[(int)speed.GetSelected()] != session.Speed) _store.SetSpeed(Enum.GetValues<SpeedLevel>()[(int)speed.GetSelected()]); };
        controls.Append(speed); card.Append(controls);
        foreach (var update in updates.ToArray()) update();
        return card;
    }
    private void QuickViewPage()
    {
        Header(Loc.T("Quick view")).Append(Button("view-grid-symbolic", Loc.T("Open"), ShowPanel, true));
        var options = Module();
        Toggle(options, "Temperatures", _settings.Get(AppSettings.Keys.PanelTemperatures, true), value => { _settings.Set(AppSettings.Keys.PanelTemperatures, value); BuildPanel(); });
        Toggle(options, "Filament", _settings.Get(AppSettings.Keys.PanelFilament, true), value => { _settings.Set(AppSettings.Keys.PanelFilament, value); BuildPanel(); });
        Toggle(options, "Fans", _settings.Get(AppSettings.Keys.PanelFans, false), value => { _settings.Set(AppSettings.Keys.PanelFans, value); BuildPanel(); });
        _pageHost.Append(options);
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
        if (_panel is null) return;
        _panelUpdates.Clear();
        var root = Column(12); root.SetMarginTop(12); root.SetMarginBottom(14); root.SetMarginStart(14); root.SetMarginEnd(14);
        var header = Row(); var title = Label(_store.PrinterName, "heading"); title.SetHexpand(true); header.Append(title);
        header.Append(Button("window-new-symbolic", Loc.T("Open Bambu Bar"), Present)); header.Append(Button("application-exit-symbolic", Loc.T("Quit"), _app.Quit)); root.Append(header);
        if (_store.Current is { } session)
        {
            root.Append(JobCard(session, _panelUpdates, compact: true));
            if (_settings.Get(AppSettings.Keys.PanelTemperatures, true)) root.Append(Live(() => Loc.T("Nozzle") + " " + Temp(session.Status?.Nozzle) + "   ·   " + Loc.T("Bed") + " " + Temp(session.Status?.Bed), _panelUpdates));
            var filament = Live(() => session.Status?.ActiveSpool is { } spool ? $"{spool.Name} · {spool.RemainDetail}" : "", _panelUpdates, "muted"); filament.SetWrap(true); if (_settings.Get(AppSettings.Keys.PanelFilament, true)) root.Append(filament);
            if (_settings.Get(AppSettings.Keys.PanelFans, false)) root.Append(Live(() => Loc.T("Part cooling") + " " + Percent(session.Status?.PartFan), _panelUpdates, "muted"));
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
