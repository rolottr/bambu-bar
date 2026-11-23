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
        if (!_store.HasMultiplePrinters) heading.Append(Button("list-add-symbolic", Loc.T("Add Printer…"), () => BeginSetup(), true));
        heading.Append(Button("view-refresh-symbolic", Loc.T("Refresh"), _store.Refresh));
        if (!session.IsOnline)
        {
            var connection = Module(Label(session.LinkSummary, "heading"));
            var explanation = Live(() => session.LinkDetail ?? "", null, "muted"); explanation.SetWrap(true); connection.Append(explanation);
            connection.Append(Button("network-connect-symbolic", Loc.T("Connect"), () => BeginSetup(session.Config), true)); _pageHost.Append(connection);
        }
        if (session.Phase is BambuBar.Core.Protocol.PrinterConnection.Phase.Unauthorized && _store.ReplacementCode is { } replacement)
            _pageHost.Append(Button("view-refresh-symbolic", Loc.T("Enter the access code again in the app"), () => Async(async () => { if (!_ephemeral) await _secrets.Save(replacement.Serial, replacement.AccessCodes[0]); _store.UseReplacementCode(); }), true));
        _pageHost.Append(JobCard(session, _updates));
        _pageHost.Append(IssueCard(session));
        _pageHost.Append(Label(Loc.T("TEMPERATURES & AIRFLOW"), "section-title"));
        _pageHost.Append(TemperatureStrip(session, true));
        _pageHost.Append(Label(Loc.T("FILAMENT"), "section-title"));
        _pageHost.Append(AmsStrip(session));
        if (session.ShowsCamera) AddCamera(session);
        _pageHost.Append(Label(Loc.T("PRINTER"), "section-title"));
        var row = Row(10); row.SetHomogeneous(true);
        var left = Module(); var right = Module();
        Detail(left, "Speed", () => $"{session.Speed.Title()} ({session.Speed.Percent()}%)", "speedometer-symbolic");
        Detail(left, "Nozzle", () => session.Status?.NozzleDiameter is { } d ? $"{d:0.0} mm · {NozzleMaterial(session.Status?.NozzleType)}" : "—", "applications-engineering-symbolic");
        if (session.Status?.DoorOpen is not null) Detail(left, "Door", () => Loc.T(session.Status?.DoorOpen == true ? "Open" : "Closed"), "door-symbolic");
        if (session.Status?.ChamberLightOn is not null) Detail(left, "Light", () => Loc.T(session.ChamberLightOn ? "On" : "Off"), "display-brightness-symbolic");
        Detail(right, "Network", () => session.Status?.WifiSignal is { } signal ? $"{WifiDescription(signal)} ({signal} dBm)" : "—", "network-wireless-symbolic");
        Detail(right, "Storage", () => session.Status?.HasSdCard is { } sd ? Loc.T(sd ? "SD card inserted" : "No SD card") : "—", "media-flash-symbolic");
        Detail(right, "Live View", () => session.Status?.LiveViewEnabled is { } live ? Loc.T(live ? "On" : "Off") : "—", "camera-video-symbolic");
        Detail(right, "Firmware", () => session.Status?.Firmware ?? "—", "application-x-firmware-symbolic");
        row.Append(left); row.Append(right); _pageHost.Append(row);
        if (session.Config.Model is { } model)
        {
            var specs = Row(10); specs.SetHomogeneous(true); var dimensions = Module(); var capabilities = Module();
            Detail(dimensions, "Build volume", () => model.BuildVolumeText, "package-x-generic-symbolic");
            Detail(dimensions, "Chamber", () => model.ChamberText, "view-paged-symbolic");
            Detail(dimensions, "Nozzles", () => Loc.F(model.NozzleCount == 2 ? "Two, up to %lld °C" : "One, up to %lld °C", model.NozzleMax), "applications-engineering-symbolic");
            Detail(capabilities, "Bed", () => Loc.F("Up to %lld °C", model.BedMax), "weather-fog-symbolic");
            Detail(capabilities, "Fans", () => string.Join(", ", new[] { Loc.T("Part cooling"), model.HasAuxFan ? Loc.T("aux") : null, model.HasChamberFan ? Loc.T("chamber") : null }.OfType<string>()), "sensors-fan-symbolic");
            Detail(capabilities, "Filament", () => model.UsesAmsLite ? "AMS Lite" : "AMS, AMS 2 Pro, AMS HT", "media-optical-symbolic");
            specs.Append(dimensions); specs.Append(capabilities); _pageHost.Append(specs);
        }
        AppendPrinterSettings(session);
    }
    private static string WifiDescription(int signal) => Loc.T(signal == -90 ? "Ethernet" : signal >= -55 ? "Wi-Fi excellent" : signal >= -67 ? "Wi-Fi good" : signal >= -75 ? "Wi-Fi fair" : signal >= -89 ? "Wi-Fi weak" : "Wi-Fi poor");
    private static string NozzleMaterial(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        var upper = code.ToUpperInvariant();
        if (upper.Length < 4) return code;
        var material = upper[2..4] switch { "00" => Loc.T("Stainless steel"), "01" => Loc.T("Hardened steel"), "05" => Loc.T("Tungsten carbide"), _ => code };
        return upper[1] switch { 'U' => Loc.F("%@, TPU high flow", material), 'H' or 'E' => Loc.F("%@, high flow", material), _ => upper.Contains("HARDENED") ? Loc.T("Hardened steel") : upper.Contains("STAINLESS") ? Loc.T("Stainless steel") : material };
    }
    private static string Temp(Temperature? temperature) => temperature is { } t ? t.Target > 0 && !t.IsAtTarget ? $"{t.Current:0}° → {t.Target:0}°" : $"{t.Current:0}°" : "—";
    private static string Percent(int? value) => value is >= 0 and { } v ? $"{v}%" : "—";

    private Gtk.Box JobCard(PrinterSession session, List<Action> updates, bool compact = false)
    {
        var card = Module(); card.AddCssClass("job");
        var stateClass = "";
        updates.Add(() => { var next = session.Status?.State switch { JobState.Failed => "state-failed", JobState.Paused => "state-paused", JobState.Finished => "state-finished", JobState.Printing or JobState.Preparing => "state-printing", _ => "state-idle" }; if (next == stateClass) return; if (stateClass.Length > 0) card.RemoveCssClass(stateClass); card.AddCssClass(next); stateClass = next; });
        var status = Live(() => "● " + (session.IsOnline ? session.Status?.StateTitle ?? session.ShortState : session.ShortState), updates, "state-label", "badge"); status.SetHalign(Gtk.Align.Start);
        var main = Row(18);
        if (!compact)
        {
            var model = new ModelView(session, false, VisibleInPage); main.Append(model.Widget); updates.Add(model.Update); _cleanup.Add(model.Dispose);
        }
        else
        {
            var picture = Gtk.Image.New(); picture.SetPixelSize(110); picture.SetSizeRequest(110, 140);
            picture.SetTooltipText(session.Config.ModelName + " printer");
            var art = Assets.PathOf("Printers", (session.Config.Model?.Shape ?? PrinterShape.Enclosed).AssetName() + "-body.png");
            if (File.Exists(art)) { using var bytes = GLib.Bytes.New(File.ReadAllBytes(art)); picture.SetFromGicon(Gio.BytesIcon.New(bytes)); } main.Append(picture);
            var last = ""; var generation = 0; var alive = true;
            (compact ? _panelCleanup : _cleanup).Add(() => alive = false);
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
                    if (!alive || mine != generation || rgba is null || _disposed) return;
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
        var stage = Live(() => session.Status is { State: JobState.Failed } failed ? Loc.F("Stopped at layer %lld of %lld", failed.Layer, failed.TotalLayers) : session.Status?.ActiveSpool is { } spool ? $"{spool.DisplayName} · {spool.SlotName}" : session.Status?.DetailText ?? session.LinkSummary, updates, "muted"); stage.SetEllipsize(Pango.EllipsizeMode.End); info.Append(stage);
        var numbers = Row(); var percent = Live(() => session.Status is { } s && session.IsOnline ? $"{s.Progress}%" : "—", updates, "progress-number"); percent.SetHexpand(true); numbers.Append(percent);
        var layers = Live(() => session.Status is { TotalLayers: > 0 } s ? $"{s.Layer}/{s.TotalLayers}\nlayers" : "", updates, "muted", "caption"); layers.SetXalign(1); layers.SetValign(Gtk.Align.End); numbers.Append(layers); info.Append(numbers);
        var progress = Gtk.ProgressBar.New(); TintProgress(progress, () => session.Status?.ActiveSpool?.Color, updates); updates.Add(() => progress.SetFraction(Math.Clamp((session.Status?.Progress ?? 0) / 100.0, 0, 1))); info.Append(progress);
        var timing = Row(8); var timeLeft = Live(() => session.Status?.State == JobState.Failed ? "⚠ " + Loc.T("Needs attention") : session.Status is { } s && s.State.IsActive() ? Format.Duration(s.RemainingMinutes) + " " + Loc.T("left") : session.LinkSummary, updates, "job-detail");
        timeLeft.SetHexpand(true); timing.Append(timeLeft);
        timing.Append(Live(() => session.Status?.EstimatedEnd is { } end ? Loc.T("Done") + " " + end.ToLocalTime().ToString("HH:mm") : "", updates, "muted", "caption", "heading")); info.Append(timing);
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
        stop.AddCssClass("stop-control"); pause.SetHexpand(true); stop.SetHexpand(true); controls.Append(pause); controls.Append(stop);
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
        controls.Append(speed);
        var result = compact ? card : Column(18);
        if (!compact) result.Append(card); result.Append(controls);
        updates.Add(() => controls.SetVisible(_store.ShowPrinterActions && session.Status?.State.IsActive() == true));
        foreach (var update in updates.ToArray()) update();
        return result;
    }
    private TrayPlacement? _trayPlacement;
    private HyprlandPopup? _hyprlandPopup;
    private int? _hyprlandAvailableHeight;
    private void PreparePanel()
    {
        if (_panel is not null) return;
        _panel = Adw.ApplicationWindow.New(_app); ApplyWindowTheme(_panel); WindowSize.Track(_panel);
        _trayPlacement = TrayPlacement.Create(_panel);
        if (_trayPlacement is not null || Platform.IsHyprland) _panel.AddCssClass("tray-popup");
        if (Platform.IsHyprland && _trayPlacement is null) _hyprlandPopup = new HyprlandPopup(() => _panel.SetVisible(false));
        _panel.SetTitle("Bambu Bar — Quick view" + (_hyprlandPopup is null ? "" : " " + _hyprlandPopup.Token)); _panel.SetDefaultSize(420, 1); _panel.SetHideOnClose(true); _panel.SetResizable(false);
        BuildPanel();
        var keys = Gtk.EventControllerKey.New(); keys.OnKeyPressed += (_, e) => { if (e.Keyval != 65307) return false; _panel.SetVisible(false); return true; }; _panel.AddController(keys);
        var hadFocus = false;
        _panel.OnMap += (_, _) => hadFocus = false;
        _panel.OnNotify += (sender, e) =>
        {
            if (e.Pspec.GetName() == "visible" && !_panel.GetVisible())
            {
                if (_hyprlandPopup is not null) _ = _hyprlandPopup.Hide();
            }
            if (e.Pspec.GetName() != "is-active") return;
            var active = _panel.GetIsActive();
            Platform.Trace($"panel focus active={active}, previously={hadFocus}");
            if (active) hadFocus = true;
            else if (hadFocus && !_positioningPanel) _panel.SetVisible(false);
        };
    }
    private void ShowPanel() => ShowPanelAt(-1, -1);
    private bool _positioningPanel;
    private bool _panelFromTray;
    private async void ShowPanelAt(int x, int y)
    {
        if (_disposed || _positioningPanel) return;
        if (UseNativeUi) { PresentNative("", panel: true); return; }
        PreparePanel();
        if (_panel!.GetVisible()) { _panel.SetVisible(false); return; }
        _positioningPanel = true;
        try
        {
            if (_trayPlacement is not null) _trayPlacement.Position(x, y);
            if (_hyprlandPopup is not null && await _hyprlandPopup.Prepare() is { } anchor)
                _hyprlandAvailableHeight = Math.Max(100, anchor.Height - 70);
        }
        finally { _positioningPanel = false; }
        if (_disposed) return;
        if (_store.MenuBarSession is { } session) _store.Select(session.Id);
        BuildPanel();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (_hyprlandPopup is not null) { _panel!.SetOpacity(0.01); _positioningPanel = true; }
        _panel!.Present();
        if (_hyprlandPopup is not null)
        {
            _panel.GetDefaultSize(out _, out var popupHeight);
            _panelFromTray = (x, y) != (-1, -1);
            var positioned = await _hyprlandPopup.Show(420, popupHeight, _panelFromTray);
            if (_disposed) { _positioningPanel = false; return; }
            _panel.GetDefaultSize(out _, out var latestHeight);
            if (positioned && latestHeight != popupHeight) positioned = await _hyprlandPopup.Show(420, latestHeight, _panelFromTray);
            _positioningPanel = false;
            if (_disposed) return;
            if (!positioned)
            {
                _panel.SetVisible(false); Present();
                Error("Install or update the Bambu Bar Hyprland integration to open Quick view beside the tray.");
                return;
            }
            _panel.SetOpacity(1);
        }
        if (_panel.GetFrameClock() is { } clock)
        {
            GObject.SignalHandler<Gdk.FrameClock>? painted = null;
            painted = (_, _) => { clock.OnAfterPaint -= painted; if (!_ephemeral) Platform.Log($"panel first frame {watch.Elapsed.TotalMilliseconds:0.0} ms"); };
            clock.OnAfterPaint += painted;
        }
        Refresh();
    }
    private async void ResizeHyprlandPanel()
    {
        _positioningPanel = true;
        try
        {
            _panel!.GetDefaultSize(out _, out var height);
            var positioned = await _hyprlandPopup!.Show(420, height, _panelFromTray);
            if (!positioned && !_disposed) _panel.SetVisible(false);
        }
        finally { _positioningPanel = false; }
    }
    private void BuildPanel()
    {
        _desktop?.Queue();
        if (_panel is null) return;
        _panelSchema = Schema;
        foreach (var clean in _panelCleanup) clean(); _panelCleanup.Clear();
        _panelUpdates.Clear();
        var root = Column(12); root.AddCssClass("quick-panel"); root.SetMarginTop(18); root.SetMarginBottom(20); root.SetMarginStart(20); root.SetMarginEnd(20);
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
        if (!_license.HasAccess)
        {
            var links = Row();
            var support = Button("emote-love-symbolic", Loc.T("Support this project"), () => DesktopAction("support-panel", null), true);
            var manage = Button("changes-allow-symbolic", Loc.T("Manage License…"), () => DesktopAction("license", null), true);
            support.AddCssClass("flat"); manage.AddCssClass("flat");
            support.SetHalign(Gtk.Align.Start); support.SetHexpand(true);
            links.Append(support); links.Append(manage); root.Append(links);
        }
        var scroll = Gtk.ScrolledWindow.New(); scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic); scroll.SetPropagateNaturalHeight(true);
        var availableHeight = 700;
        if (_panel.GetSurface() is { } surface && Gdk.Display.GetDefault()?.GetMonitorAtSurface(surface) is { } monitor)
        {
            monitor.GetGeometry(out var geometry); using (geometry) availableHeight = Math.Max(300, geometry.Height - 70);
        }
        if (_trayPlacement is not null) availableHeight = _trayPlacement.AvailableHeight;
        else if (_hyprlandAvailableHeight is { } height) availableHeight = height;
        scroll.SetMaxContentHeight(availableHeight); scroll.SetChild(root); _panel.SetContent(scroll);
        root.Measure(Gtk.Orientation.Vertical, 420, out _, out var naturalHeight, out _, out _);
        _panel.SetDefaultSize(420, Math.Min(naturalHeight, availableHeight));
        if (_hyprlandPopup is not null && _panel.GetVisible() && !_positioningPanel) ResizeHyprlandPanel();
        Platform.Trace($"panel natural={naturalHeight}, available={availableHeight}");
    }
}
