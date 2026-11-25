using System.Globalization;
using System.Text.Json;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using BambuBar.Core.Licensing;
using Tmds.DBus;

namespace BambuBar.Linux;

[DBusInterface("dev.rolottr.BambuBar.Native")]
public interface INativeHost : IDBusObject
{
    Task OpenAsync(string page);
    Task PanelAsync();
    Task OpenDemoAsync(string instance);
    Task<bool> SetAutostartAsync(bool enabled);
}

internal sealed partial class Shell
{
    private NativeUiServer? _native;
    private readonly string _nativeInstance = Guid.NewGuid().ToString("N");
    private bool _nativeQueued;
    private string _nativePage = "Printer";
    private string _nativeError = "";
    private NativeCameraViewer? _nativeCamera;
    private bool _nativeCameraRequested;
    private TaskCompletionSource<bool>? _nativeCapture;
    private int _nativePresentation;
    private bool UseNativeUi => Platform.IsPlasma || Platform.IsOmarchy;

    private async void HandleNativeCommand(string[] args, Action? complete)
    {
        try
        {
            if (args.Contains("--hide")) { _native?.Send(new { type = "hide" }); return; }
            var page = Array.IndexOf(args, "--page");
            if (page >= 0 && page + 1 < args.Length && (_pages.Contains(args[page + 1]) || args[page + 1] == "Printers")) PresentNative(args[page + 1]);
            if (args.Contains("--panel")) PresentNative("", panel: true);
            var snapshot = Array.IndexOf(args, "--snapshot");
            var screenshot = Array.IndexOf(args, "--screenshot");
            var index = snapshot >= 0 ? snapshot : screenshot;
            if (index < 0 || index + 1 >= args.Length) return;
            if (args.Contains("--panel")) throw new IOException("Capture native shell popups with the desktop screenshot tool.");
            PresentNative(_nativePage);
            for (var wait = 0; _native?.Connected != true && wait < 100; wait++) await Task.Delay(100);
            if (_native?.Connected != true) throw new IOException("Native UI did not connect.");
            var scenes = snapshot >= 0 ? _pages.Where(p => p != "All Printers" || _store.HasMultiplePrinters).ToArray() : [_nativePage];
            foreach (var scene in scenes)
            {
                _nativePage = scene; PublishNative();
                await Task.Delay(1200);
                var path = snapshot >= 0 ? Path.Combine(args[index + 1], "window-" + scene.ToLowerInvariant().Replace(' ', '-') + ".png") : args[index + 1];
                _nativeCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _native.Send(new { type = "capture", path = Path.GetFullPath(path) });
                if (!await _nativeCapture.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new IOException("Native captures must be PNG files inside the app data directory (for example /var/data/screenshots).");
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException) { Console.Error.WriteLine(e.Message); }
        finally
        {
            _nativeCapture = null; complete?.Invoke();
            if (_ephemeral && args.Contains("--snapshot")) DesktopAction("quit", null);
        }
    }

    private void StartNativeUi()
    {
        if (!UseNativeUi) return;
        _native = new NativeUiServer(NativeRequest, PublishNative, () => { _nativeCamera?.Dispose(); _nativeCamera = null; }, OwnsDesktop ? "" : "-" + _nativeInstance);
        _license.Changed += QueueNative;
        _store.SaveFailed += _ => { _nativeError = Loc.T("Changes could not be saved. Check file permissions and your keyring."); QueueNative(); };
    }

    private async void PresentNative(string page, bool panel = false)
    {
        var presentation = ++_nativePresentation;
        var requestedPage = _pages.Contains(page) ? page : "Printer";
        if (!panel) _nativePage = requestedPage;
        try
        {
            using var connection = new Connection(Address.Session);
            await connection.ConnectAsync();
            if (presentation != _nativePresentation) return;
            var host = connection.CreateProxy<INativeHost>(Platform.AppId + ".Native", new ObjectPath("/dev/rolottr/BambuBar/Native"));
            if (!OwnsDesktop) await host.OpenDemoAsync(_nativeInstance);
            else if (panel) await host.PanelAsync();
            else { await host.OpenAsync(requestedPage); PublishNative(); }
        }
        catch (Exception)
        {
            _notifier.Post("Bambu Bar", "Install the native desktop package for this desktop, then reopen Bambu Bar.", "native-ui-unavailable");
        }
    }

    private void QueueNative()
    {
        if (_nativeQueued || _native?.Connected != true) return;
        _nativeQueued = true;
        GLib.Functions.IdleAdd(200, () => { _nativeQueued = false; if (!_disposed) PublishNative(); return false; });
    }

    private sealed record UiOption(string value, string label);
    private sealed record UiField(string key, string label, string kind, object? value = null, UiOption[]? options = null, string? note = null, bool enabled = true, string? icon = null);
    private sealed record UiSection(string title, UiField[] fields);
    private static UiField Text(string label, object? value) => new("", Loc.T(label), "text", value?.ToString() ?? "—");
    private static UiField Paragraph(string value) => new("", "", "paragraph", Loc.T(value));
    private static UiField ToggleField(string key, string label, bool value) => new(key, Loc.T(label), "toggle", value);
    private static UiField ActionField(string key, string label, string? note = null) => new(key, Loc.T(label), "button", note: note);
    private static UiField InputField(string key, string label, string value = "", bool secret = false) => new(key, Loc.T(label), secret ? "secret" : "input", value);
    private static UiField Choice(string key, string label, object value, params UiOption[] options) => new(key, Loc.T(label), "choice", value.ToString(), options);
    private static UiOption Option(string value, string label) => new(value, Loc.T(label));
    private static UiSection Section(string title, params UiField[] fields) => new(Loc.T(title), fields);

    private UiSection[] NativeWidgetSections()
    {
        var sharing = _settings.Get("linuxWidgetsEnabled", false);
        var shared = Section("Desktop widgets",
            ToggleField("linuxWidgetsEnabled", "Share printer status with desktop widgets", sharing),
            Paragraph(sharing ? "Sharing is on. Desktop widgets follow live printer status." :
                Platform.IsPlasma ? "Turn on sharing to show live printer status in your Plasma widgets." :
                "Sharing is off. Your desktop cards are hidden."));
        static UiField Step(string icon, string instruction) => new(icon, "", "step", Loc.T(instruction));
        if (Platform.IsPlasma)
            return [shared, Section("Add a widget",
                Step("preferences-desktop-widgets", "Right-click the desktop and choose Enter Edit Mode."),
                Step("view-grid", "Open Add Widgets and search for Bambu Bar."),
                Step("printer", "Drag Bambu Bar onto your desktop."),
                Step("configure", "Open the widget’s settings to choose a printer and size.")),
                Section("On your desktop", Paragraph("Plasma manages widget placement. Enter Edit Mode on the desktop to move or remove a widget."))];

        var widgets = _settings.Get<List<DesktopWidgetConfig>>("linuxWidgets") ?? [];
        return [shared, Section("Add a widget",
            Choice("widgetSize", "Size", "medium", Option("small", "Small"), Option("medium", "Medium"), Option("large", "Large")),
            Choice("widgetPrinter", "Printer", "", [Option("", "Automatic"), .. _store.Sessions.Select(p => Option(p.Id, p.Config.Name))]),
            Choice("widgetCorner", "Corner", "top-right", Option("top-left", "Top left"), Option("top-right", "Top right"), Option("bottom-left", "Bottom left"), Option("bottom-right", "Bottom right")),
            ActionField("add-widget", "Add widget") with { enabled = widgets.Count < 8 }),
            Section("On your desktop", widgets.Count == 0 ? [Paragraph("Your added widgets will appear here.")] :
                widgets.Select(w => new UiField("remove-widget:" + w.Id, Loc.T("Remove"), "placed",
                    $"{_store.Session(w.Printer ?? "")?.Config.Name ?? Loc.T("Automatic")} · {Loc.T(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(w.Size))} · {Loc.T(w.Corner switch { "top-left" => "Top left", "bottom-left" => "Bottom left", "bottom-right" => "Bottom right", _ => "Top right" })}")).ToArray())];
    }

    private UiSection[] NativePrinterSections(PrinterSession session)
    {
        static UiField Detail(string icon, string label, string? value) =>
            new("", Loc.T(label), "text", value ?? "—", icon: icon);

        var sections = new List<UiSection>();
        if (session.IsOnline && session.Status is { } status)
        {
            var model = session.Config.Model;
            var nozzle = string.Join(" · ", new[] {
                status.NozzleDiameter is { } d ? $"{d:0.0} mm" : null,
                status.NozzleType is { } type ? NozzleMaterial(type) : null
            }.OfType<string>());
            var state = new List<UiField> {
                Detail("speedometer", "Speed", Loc.F("%@ (%lld%%)", session.Speed.Title(), session.Speed.Percent())),
                Detail("fire", "Nozzle", nozzle.Length == 0 ? null : nozzle)
            };
            if (status.DoorOpen is { } door)
                state.Add(Detail("door", "Door", Loc.T(door ? "Open" : "Closed", "Door")));
            if (status.ChamberLightOn is not null)
                state.Add(Detail("lightbulb", "Light", Loc.T(session.ChamberLightOn ? "On" : "Off")));

            sections.Add(Section("Printer", state.ToArray()));
            sections.Add(Section("Connection",
                Detail("network-wireless", "Network", status.WifiSignal is { } wifi ? $"{WifiDescription(wifi)} ({wifi} dBm)" : null),
                Detail(model?.Storage == StorageMedia.UsbDrive ? "drive-removable-media" : "media-flash", "Storage", (model?.Storage).Describe(status.HasSdCard)),
                Detail("camera-video", "Live View", status.LiveViewEnabled is { } live ? Loc.T(live ? "On" : "Off") : null),
                Detail("dialog-memory", "Firmware", (status.Firmware ?? "—") + (status.UpdateAvailable ? " · " + Loc.T("Update available") : ""))));
            if (model is not null)
            {
                sections.Add(Section("Specifications",
                    Detail("package", "Build volume", model.BuildVolumeText),
                    Detail("chamber", "Chamber", model.ChamberText),
                    Detail("fire", "Nozzles", Loc.F(model.NozzleCount == 2 ? "Two, up to %lld °C" : "One, up to %lld °C", model.NozzleMax))));
                var fans = new[] { Loc.T("Part cooling"), model.HasAuxFan ? Loc.T("aux") : null,
                    model.HasChamberFan ? Loc.T("chamber") : null };
                sections.Add(Section("Capabilities",
                    Detail("heat-waves", "Bed", Loc.F("Up to %lld °C", model.BedMax)),
                    Detail("fan", "Fans", string.Join(", ", fans.OfType<string>())),
                    Detail("media-optical", "Filament", model.UsesAmsLite ? "AMS Lite" : "AMS, AMS 2 Pro, AMS HT")));
            }
        }
        var settings = new List<UiField> {
            InputField("name", "Name", session.Config.Name), ActionField("rename", "Save"),
            Text("Model", "Bambu Lab " + session.Config.ModelName), Text("Serial number", session.Id),
            Text("Status", session.LinkSummary) with { key = "printerStatus" }, Text("IP address", session.Config.Host) with { key = "printerAddress" },
            InputField("existingHost", "IP address", session.Config.Host), InputField("replacementCode", "Access code", secret: true),
            ActionField("save-address", "Reconnect"), ToggleField("printerCamera", "Camera", session.Config.CameraEnabled == true)
        };
        if (_store.AnnouncedName(session.Id) is { } announced && announced != session.Config.Name)
            settings.Insert(2, new UiField("use-announced-name", Loc.F("Use “%@”", announced), "button"));
        settings.Add(Paragraph(PrinterCameraNote(session)));
        if (session.Config.CameraEnabled == true && !_store.CameraEnabled)
        {
            settings.Add(Paragraph("The camera is off for every printer in Settings."));
            settings.Add(ActionField("navigate:Settings", "Open Settings"));
        }
        var ignored = session.Config.IgnoredIssues ?? [];
        settings.Add(Text("Ignored codes", ignored.Count == 0 ? Loc.T("None") : "") with { key = "ignoredCodes" });
        if (ignored.Count == 0) settings.Add(Paragraph(IgnoredCodesHint));
        foreach (var id in ignored)
        {
            settings.Add(Text(id, HmsCatalog.Shared.IssueText(id, session.Id[..Math.Min(3, session.Id.Length)]) ?? ""));
            settings.Add(ActionField("unignore:" + id, "Show Again"));
        }
        settings.Add(ActionField("forget", "Forget Printer…", "confirmation") with { value = Loc.T("Bambu Bar follows the printer if its IP address changes.") });
        sections.Add(Section("Settings", settings.ToArray()));
        return sections.ToArray();
    }

    private UiSection[] NativeSections()
    {
        var s = _store.Current;
        var status = s?.Status;
        switch (_nativePage)
        {
            case "Printer" when s is null: goto case "Setup";
            case "Printer" when s is not null:
                return NativePrinterSections(s);
            case "Filament":
                if (s is null) return [Section("No printer set up", ActionField("navigate:Setup", "Add Printer…"))];
                if (!s.IsOnline || status is null) return [Section("Printer offline", Text("Filament", "Filament appears once Bambu Bar is connected."))];
                return status.Ams.Count == 0 && status.ExternalSpool is null ? [Section("No AMS connected", Text("Filament", "Spools loaded in an AMS or on the external holder appear here."))] : [];
            case "Notifications": return [Section("Notify me when", ToggleField("notifyFinished", "Print finished", _store.NotifyFinished), ToggleField("notifyFailed", "Print failed, canceled or an error", _store.NotifyFailed), ToggleField("notifyPaused", "Print paused (filament runout, user)", _store.NotifyPaused), ToggleField("notifyStarted", "Print started", _store.NotifyStarted)), Section("Notifications", ActionField("test-notification", "Send test notification"))];
            case "Menu Bar":
                var menuOptions = new List<UiField>();
                if (_store.HasMultiplePrinters)
                    menuOptions.Add(Choice("menuBarPrinter", "Printer in the menu bar", _store.MenuBarSerial ?? "",
                        [Option("", "Automatic"), .. _store.Sessions.Select(p => Option(p.Id, p.Config.Name))]));
                menuOptions.Add(Choice("menuBarStyle", "While printing, show", _store.MenuBarStyle.RawValue(),
                    Option("icon", "Icon only"), Option("percent", "Progress"), Option("remaining", "Time left"), Option("both", "Progress and time left")) with { kind = "radio" });
                var showTemperatures = _settings.Get(AppSettings.Keys.PanelTemperatures, true);
                return [
                    Section("Menu bar", menuOptions.ToArray()),
                    Section("What to show",
                        ToggleField(AppSettings.Keys.PanelTemperatures, "Temperatures", showTemperatures),
                        ToggleField(AppSettings.Keys.PanelChamber, "Chamber temperature", _settings.Get(AppSettings.Keys.PanelChamber, true)) with { enabled = showTemperatures },
                        ToggleField(AppSettings.Keys.PanelFans, "Fans", _settings.Get(AppSettings.Keys.PanelFans, false)),
                        ToggleField(AppSettings.Keys.PanelFilament, "Filament", _settings.Get(AppSettings.Keys.PanelFilament, true)),
                        Paragraph("Chamber temperature appears on printers with a chamber sensor."),
                        ToggleField("showPrinterActions", "Pause, stop and speed", _store.ShowPrinterActions),
                        Paragraph("Show print controls in Quick view and the main window, and print actions in the panel icon’s menu."))
                ];
            case "Settings": return [
                Section("Appearance", Text("Theme", Platform.IsOmarchy ? "Omarchy · active desktop theme" : "KDE Plasma · system Qt style"), Choice("linuxAppearance", "Color mode", _settings.Get("linuxAppearance", 2), Option("0", "Dark"), Option("1", "Light"), Option("2", "System"))),
                Section("General", ToggleField("openAtLogin", "Open at login", _settings.Get("openAtLogin", false))),
                Section("Language", Choice("appLanguage", "Language", _settings.Get<string>("appLanguage") ?? "", [Option("", "System"), .. (Strings.Current?.Languages ?? ["en"]).Select(l => new UiOption(l, CultureInfo.GetCultureInfo(l).NativeName))])),
                Section("Camera", ToggleField("cameraEnabled", "Enable cameras", _store.CameraEnabled), ToggleField("cameraStaysOnWhilePrinting", "Keep camera connected while printing", _store.CameraStaysOnWhilePrinting) with { enabled = _store.CameraEnabled }),
                Section("Controls", ToggleField("confirmStop", "Ask before stopping a print", _store.ConfirmStop)),
                Section("Updates", Text("Updates", "Install updated Flatpak and native desktop packages from GitHub Releases."))];
            case "Setup":
                var result = new List<UiSection>();

                result.Add(Section("Connection", InputField("newName", "Name"), InputField("host", "Address"), InputField("serial", "Serial number"), InputField("accessCode", "Access code", secret: true), ActionField("connect", "Connect"), ActionField("discover", "Search Again"), ActionField("import-file", "Choose File…")));
                if (_store.Discovered.Count > 0) result.Add(Section("Discovered printers", _store.Discovered.Select(p => ActionField("choose:" + p.Serial, $"{p.Name} · {p.Host}")).ToArray()));
                if (_store.ReadySlicerPrinters.Count > 0) result.Add(Section("Imported printers", _store.ReadySlicerPrinters.Select(p => ActionField("add-known:" + p.Serial, p.Name ?? p.Serial)).Append(ActionField("add-all", "Add all printers")).ToArray()));
                return result.ToArray();
            case "Widgets": return NativeWidgetSections();
            case "About":
                var licenseFields = new List<UiField> {
                    new("", Loc.T(_license.HasAccess ? "Hero Supporter" : "Support Bambu Bar"), "license-heading"),
                    Paragraph(_license.HasAccess ? "Thank you for supporting Bambu Bar." : "Bambu Bar is free to use. Activate a supporter license to receive the Hero Supporter badge.")
                };
                if (_license.HasRecord)
                {
                    licenseFields.Add(Text("Installation", _license.Installation));
                    licenseFields.Add(ActionField("check-license", "Check License") with { enabled = !_license.Busy });
                    licenseFields.Add(ActionField("deactivate-license", "Deactivate This Installation", "confirmation") with { enabled = !_license.Busy });
                }
                else
                {
                    licenseFields.Add(InputField("licenseKey", "License key", secret: true));
                    licenseFields.Add(ActionField("activate-license", "Activate") with { enabled = !_license.Busy });
                }
                licenseFields.Add(new("support", Loc.T("Support this project"), "license-links", Loc.T("Manage Purchase")));
                licenseFields.Add(Paragraph("Supporter activation needs an internet connection. Benefits stay active for up to seven days after a successful check."));
                if (_license.Busy || _license.Message is { Length: > 0 })
                    licenseFields.Add(new("", Loc.T("Status"), _license.Busy ? "license-busy" : "license-error", _license.Busy ? Loc.T("Checking…") : _license.Message));
                return [
                    Section("This copy", Text("Version", $"{AppVersion.Marketing} ({Platform.Commit})"),
                        Text("System", Platform.IsOmarchy ? "Omarchy · Quickshell" : "KDE Plasma · Qt/Kirigami"),
                        Text("License", Loc.T(_license.HasAccess ? "Hero Supporter" : "Free · Community edition")), ActionField("copy-version", "Copy Version Info")),
                    Section("Links", ActionField("website", "bambubar.app"), ActionField("releases", "Release Notes"),
                        ActionField("manage-purchase", "Manage Purchase"), ActionField("report-issue", "Report an Issue")),
                    Section("Your license", licenseFields.ToArray()),
                    Section("How it works", Paragraph("Bambu Bar connects directly to your printer over encrypted MQTT. It shows the printer’s status in the tray and shares a snapshot with enabled desktop widgets.")),
                    Section("Privacy", Paragraph("Printer data stays on your local network. No app analytics. Printer access codes and supporter license credentials are stored in your desktop keyring. Supporter licenses are validated online.")),
                    Section("Credits", Paragraph("Protocol details and HMS error texts come from the open-source ha-bambulab project. The printer models are visual approximations built for this project in Blender."),
                        Paragraph("Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab."))
                ];
            default: return [];
        }
    }

    private void PublishNative()
    {
        if (_native?.Connected != true || _disposed) return;
        if (_nativePage == "All Printers" && !_store.HasMultiplePrinters) _nativePage = "Printer";
        var state = _desktop is null ? "{}" : _desktop.MainState();
        var subtitle = _nativePage switch
        {
            "Filament" => FilamentSummary(),
            "Menu Bar" => Loc.T("What the menu bar shows while you print, and what you see when you click it."),
            "Widgets" => Loc.T("Your printers on the desktop, in three sizes."),
            _ => ""
        };
        _native.Send(new {
            type = "state", page = _nativePage, subtitle,
            pages = _pages.Where(p => p != "All Printers" || _store.HasMultiplePrinters).Select(p => new { id = p, title = Loc.T(p) }),
            sections = NativeSections(), desktop = JsonSerializer.Deserialize<JsonElement>(state),
            activity = _nativePage == "Activity" ? NativeActivity() : null,
            error = _nativeError, supporter = _license.HasAccess, confirmStop = _store.ConfirmStop,
            appearance = _settings.Get("linuxAppearance", 2), palette = Platform.IsOmarchy ? _omarchyTheme?.Palette : null
        }, "state");
    }

    private async Task NativeRequest(JsonElement request)
    {
        if (_disposed) return;
        try
        {
            var action = request.GetProperty("action").GetString() ?? "";
            var value = request.TryGetProperty("value", out var v) ? v : default;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var yes = value.ValueKind == JsonValueKind.True;
            string Field(string key) => request.TryGetProperty("fields", out var fields) && fields.TryGetProperty(key, out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
            var s = _store.Current;
            _nativeError = "";
            if (NativeActivityRequest(action, text)) { /* Handled: filter, more, clear or export. */ }
            else if (action.StartsWith("navigate:") && (_pages.Contains(action[9..]) || action is "navigate:Setup" or "navigate:Printers")) {
                var next = action == "navigate:Printers" ? "Printer" : action[9..];
                if (next == "Activity" && _nativePage != "Activity") ResetActivityView();
                _nativePage = next;
                if (_nativeCameraRequested) { _nativeCameraRequested = false; _native?.Send(new { type = "show-camera" }); }
            }
            else if (action.StartsWith("unignore:") && s?.Config.IgnoredIssues?.Contains(action[9..]) == true) _store.Unignore(action[9..], s.Id);
            else if (action.StartsWith("remove-widget:")) { var widgets = _settings.Get<List<DesktopWidgetConfig>>("linuxWidgets") ?? []; widgets.RemoveAll(w => w.Id == action[14..]); _settings.Set("linuxWidgets", widgets); }
            else if (action.StartsWith("choose:") && _store.Discovered.FirstOrDefault(p => p.Serial == action[7..]) is {} found)
                _native?.Send(new { type = "prefill", fields = new { newName = found.Name, host = found.Host, serial = found.Serial } });
            else if (action == "add-all" || action.StartsWith("add-known:"))
            {
                var imported = _store.ReadySlicerPrinters.Where(p => action == "add-all" || p.Serial == action[10..]).ToArray();
                if (!_ephemeral) foreach (var printer in imported) await _secrets.Save(printer.Serial, printer.AccessCodes[0]);
                _store.AddAll(imported); _nativePage = "Printer";
            }
            else switch (action)
            {
                case "ready": break;
                case "capture-result": _nativeCapture?.TrySetResult(yes); break;
                case "select": _nativeCamera?.Dispose(); _nativeCamera = null; _native?.Send(new { type = "close-camera" }); _store.Select(text); break;
                case "visible": if (!yes) { _nativeCamera?.Dispose(); _nativeCamera = null; } break;
                case "camera":
                    _nativeCamera?.Dispose(); _nativeCamera = null;
                    if (yes && s?.ShowsCamera == true && s.IsOnline) _nativeCamera = new NativeCameraViewer(s.Camera, _native!);
                    break;
                case "camera-retry": _nativeCamera?.Retry(); break;
                case "linuxAppearance": if (int.TryParse(text, out var appearance) && appearance is >= 0 and <= 2) { _settings.Set("linuxAppearance", appearance); ApplyAppearance(); } break;
                case "copy-issue": if (s?.Status?.MostSevereIssue is {} issue) _native?.Send(new { type = "clipboard", text = issue.Id }); break;
                case "issue-wiki": if (s?.Status?.MostSevereIssue?.WikiUrl is {} wiki) Open(wiki.ToString()); break;
                case "notifyFinished": _store.NotifyFinished = yes; break;
                case "notifyFailed": _store.NotifyFailed = yes; break;
                case "notifyPaused": _store.NotifyPaused = yes; break;
                case "notifyStarted": _store.NotifyStarted = yes; break;
                case "confirmStop": _store.ConfirmStop = yes; break;
                case "showPrinterActions": _store.ShowPrinterActions = yes; break;
                case "cameraEnabled": _store.CameraEnabled = yes; break;
                case "cameraStaysOnWhilePrinting": _store.CameraStaysOnWhilePrinting = yes; break;
                case "printerCamera": if (s is not null) _store.SetCameraEnabled(yes, s.Id); break;
                case "menuBarStyle": if (new[] { "icon", "percent", "remaining", "both" }.Contains(text)) _store.MenuBarStyle = TrayStyles.Parse(text); break;
                case "menuBarPrinter": if (text == "" || _store.Session(text) is not null) _store.MenuBarSerial = text == "" ? null : text; break;
                case AppSettings.Keys.PanelTemperatures: case AppSettings.Keys.PanelChamber: case AppSettings.Keys.PanelFans: case AppSettings.Keys.PanelFilament: case "linuxWidgetsEnabled": _settings.Set(action, yes); break;
                case "openAtLogin":
                    if (_ephemeral) break;
                    if (!await _background.Request(yes)) throw new IOException("Background startup was not allowed by the desktop.");
                    _settings.Set("openAtLogin", yes); break;
                case "appLanguage":
                    if (text != "" && Strings.Current?.Languages.Contains(text) != true) break;
                    _settings.Set<string?>("appLanguage", text == "" ? null : text);
                    var strings = Strings.LoadFor(c => text == "" ? c.Match(CultureInfo.CurrentUICulture) : text); strings.Use(text == "" ? strings.Match(CultureInfo.CurrentUICulture) : text); break;
                case "use-announced-name": if (s is not null && _store.AnnouncedName(s.Id) is { } announced) _store.Rename(s.Id, announced); break;
                case "ignore-issue": if (s?.Status?.MostSevereIssue is { } shown && shown.Id == text) _store.Ignore(shown, s.Id); break;
                case "rename": if (s is not null && Field("name").Trim() is { Length: > 0 and <= 120 } name) _store.Rename(s.Id, name); break;
                case "save-address":
                    var address = Field("existingHost").Trim();
                    if (s is null || !ValidPrinterAddress(address)) throw new IOException("Enter a valid network address.");
                    var updatedCode = Field("replacementCode");
                    if (updatedCode.Length == 0) updatedCode = s.AccessCode ?? "";
                    if (updatedCode.Length != 8) throw new IOException("Enter the 8-character access code from the printer.");
                    if (!_ephemeral && updatedCode != s.AccessCode) await _secrets.Save(s.Id, updatedCode);
                    _store.Connect(s.Config with { Host = address }, updatedCode);
                    _native?.Send(new { type = "connection-saved", serial = s.Id }); break;
                case "forget": if (s is not null && yes) _store.Forget(s.Id); break;
                case "import-text": if (text.Length <= 1024 * 1024) _store.ImportSlicerText(text); break;
                case "discover": _store.SearchForPrinters(); break;
                case "connect":
                    var host = Field("host").Trim(); var serial = Field("serial").Trim().ToUpperInvariant(); var code = Field("accessCode");
                    if (!ValidPrinterAddress(host) || code.Length != 8) throw new IOException("Enter a network address and the 8-character access code from the printer.");
                    if (serial.Length == 0) serial = (await _store.IdentifyPrinterAsync(host)).Serial ?? throw new IOException("Could not identify the printer. Enter its serial number.");
                    if (serial.Length > 64 || !serial.All(char.IsAsciiLetterOrDigit)) throw new IOException("Invalid printer serial number.");
                    if (!_ephemeral) await _secrets.Save(serial, code);
                    var config = _store.Session(serial)?.Config ?? new PrinterConfig { Serial = serial, Host = host, Name = PrinterConfig.PlaceholderName(serial, "") };
                    _store.Connect(config with { Host = host, Name = Field("newName").Trim() is { Length: > 0 and <= 120 } n ? n : config.Name }, code);
                    _nativePage = "Printer"; break;
                case "test-notification": _notifier.Post("Bambu Bar", Loc.T("Your printer notifications will appear here."), "test"); break;
                case "add-widget":
                    var list = _settings.Get<List<DesktopWidgetConfig>>("linuxWidgets") ?? [];
                    if (list.Count < 8) { list.Add(new DesktopWidgetConfig { Printer = _store.Session(Field("widgetPrinter"))?.Id, Size = new[] { "small", "medium", "large" }.Contains(Field("widgetSize")) ? Field("widgetSize") : "medium", Corner = new[] { "top-left", "top-right", "bottom-left", "bottom-right" }.Contains(Field("widgetCorner")) ? Field("widgetCorner") : "top-right" }); _settings.Set("linuxWidgets", list); _settings.Set("linuxWidgetsEnabled", true); } break;
                case "activate-license": if (!_license.Busy && Field("licenseKey").Length <= 256) _license.Activate(Field("licenseKey")); break;
                case "check-license": _license.Refresh(); break;
                case "deactivate-license": if (yes) _license.Deactivate(); break;
                case "website": Open("https://bambubar.app"); break;
                case "releases": Open("https://bambubar.app/changelog"); break;
                case "support-window": OpenSupport(PolarConfiguration.SupportPlacement.MainWindow); break;
                case "copy-version":
                    _native?.Send(new { type = "clipboard", text = $"Bambu Bar {AppVersion.Marketing} ({Platform.Commit})\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}" });
                    break;
                case "report-issue": Open("https://github.com/rolottr/bambu-bar/issues/new"); break;
                case "manage-purchase": Open(PolarConfiguration.PortalUrl.ToString()); break;
                case "support": OpenSupport(PolarConfiguration.SupportPlacement.License); break;
                case "panel": PresentNative("", panel: true); break;
                case "stop": if ((!_store.ConfirmStop || yes) && _store.ShowPrinterActions && s?.IsOnline == true && s.Status is { ControlsLocked: false } active && active.State.IsActive()) s.StopPrint(); break;
                case "refresh": case "light": case "pause": case "resume": case "dismiss-issue": case "quit": DesktopAction(action, s); break;
                default: if (action.StartsWith("speed:", StringComparison.Ordinal)) DesktopAction(action, s); break;
            }
            if (_nativeCamera is not null && s?.ShowsCamera != true) { _nativeCamera.Dispose(); _nativeCamera = null; }
            _desktop?.Queue();
            PublishNative();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            _nativeError = e is IOException ? e.Message : "The request could not be completed.";
            PublishNative();
        }
    }

    private sealed class NativeCameraViewer : ICameraViewer, IDisposable
    {
        private readonly CameraFeed _feed;
        private readonly NativeUiServer _server;
        private long _lastFrame;
        private bool _disposed, _encoding;
        public bool IsOnScreen => !_disposed;
        public NativeCameraViewer(CameraFeed feed, NativeUiServer server) { _feed = feed; _server = server; feed.FrameChanged += Frame; feed.PropertyChanged += Changed; feed.Attach(this); Status(); }
        public void Retry() => _feed.TryAgain();
        private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Status();
        private void Status() => _server.Send(new { type = "camera-status", message = _feed.State switch {
            CameraFeed.FeedState.Unavailable unavailable => unavailable.Reason,
            CameraFeed.FeedState.Failed failed => failed.Reason,
            CameraFeed.FeedState.Live => _feed.Detail ?? Loc.T("Live View"),
            _ => Loc.T("Connecting…")
        }, retry = _feed.State is CameraFeed.FeedState.Unavailable or CameraFeed.FeedState.Failed });
        private async void Frame()
        {
            if (_disposed || _encoding || _feed.Frame is not {} frame || Environment.TickCount64 - _lastFrame < 67) return;
            _lastFrame = Environment.TickCount64;
            _encoding = true;
            // CameraFeed may reuse its decode buffer; copy before leaving the UI context.
            var copy = new BambuBar.Core.Camera.Decoding.CameraFrame(frame.Width, frame.Height);
            frame.Pixels.CopyTo(copy.Pixels, 0);
            try
            {
                var image = await Task.Run(() => NativeCameraEncoding.Jpeg(copy));
                if (!_disposed) _server.Send(new { type = "camera", image = "data:image/jpeg;base64," + Convert.ToBase64String(image) }, "camera");
            }
            catch (IOException) { if (!_disposed) _server.Send(new { type = "camera-status", message = "Camera image unavailable." }); }
            finally { _encoding = false; }
        }
        public void Dispose() { if (_disposed) return; _disposed = true; _feed.FrameChanged -= Frame; _feed.PropertyChanged -= Changed; _feed.Detach(this); _server.Send(new { type = "camera", image = "" }, "camera"); }
    }
}
