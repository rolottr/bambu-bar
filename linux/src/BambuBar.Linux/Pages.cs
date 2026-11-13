using System.Globalization;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private void SetupPage()
    {
        var heading = Header(Loc.T("Add Printer…"));
        heading.Append(Button("view-refresh-symbolic", Loc.T("Search Again"), _store.SearchForPrinters));
        var found = Module(Label(Loc.T("Printers"), "section-title")); _pageHost.Append(found);
        var fields = Module();
        Gtk.Entry Field(string title, string placeholder, bool secret = false)
        {
            fields.Append(Label(Loc.T(title), "muted")); var entry = Gtk.Entry.New(); entry.SetPlaceholderText(placeholder); entry.SetVisibility(!secret); fields.Append(entry); return entry;
        }
        var name = Field("Name", "Bambu Lab"); var host = Field("Address", "192.168.1.100");
        var serial = Field("Serial number", ""); var code = Field("Access code", "••••••••", true); code.SetMaxLength(8);
        if (_setupConfig is { } edit) { name.SetText(edit.Name); host.SetText(edit.Host); serial.SetText(edit.Serial); }
        var connect = Button("network-connect-symbolic", Loc.T("Connect"), () => Async(async () =>
        {
            if (!System.Net.IPAddress.TryParse(host.GetText().Split(':')[0], out _) && Uri.CheckHostName(host.GetText()) == UriHostNameType.Unknown)
                throw new IOException("Enter the printer's network address.");
            if (code.GetText().Length != 8) throw new IOException("Enter the 8-character access code from the printer's Network settings.");
            var address = host.GetText().Trim(); var id = serial.GetText().Trim().ToUpperInvariant();
            if (id.Length == 0)
            {
                var identified = await _store.IdentifyPrinterAsync(address);
                id = identified.Serial ?? throw new IOException("Could not identify the printer. Check the address and enter its serial number.");
                serial.SetText(id);
            }
            var accessCode = code.GetText();
            if (!_ephemeral) await _secrets.Save(id, accessCode);
            _store.Connect(new PrinterConfig { Host = address, Serial = id, Name = string.IsNullOrWhiteSpace(name.GetText()) ? PrinterConfig.PlaceholderName(id, "") : name.GetText().Trim(), ModelCode = _setupConfig?.ModelCode ?? "", CameraEnabled = _setupConfig?.CameraEnabled }, accessCode);
            _nav.SelectRow(_nav.GetRowAtIndex(0)); Navigate("Printer");
        }), true); connect.AddCssClass("suggested-action"); fields.Append(connect); _pageHost.Append(fields);
        var help = Label(Loc.T("Check the access code on the printer's screen (Settings → Network) and enter it again."), "muted"); help.SetWrap(true); _pageHost.Append(help);
        var lastKey = "";
        var import = Button("document-open-symbolic", Loc.T("Choose File…"), () => Async(async () =>
        {
            var dialog = Gtk.FileDialog.New(); dialog.SetTitle(Loc.T("Choose Bambu Studio's settings"));
            try { var file = await dialog.OpenAsync(_window); if (file?.GetPath() is { } path) { _store.ImportSlicerSettings(path); lastKey = ""; Refresh(); } }
            catch (GLib.GException) { /* Canceling the chooser leaves setup intact. */ }
        }), true); _pageHost.Append(import);
        _updates.Add(UpdateFound);
        void UpdateFound()
        {
            var key = string.Join('|', _store.Discovered.Select(p => p.Serial)) + string.Join('|', _store.SlicerPrinters.Select(p => p.Serial));
            if (lastKey == key && found.GetFirstChild()?.GetNextSibling() is not null) return; lastKey = key;
            while (found.GetLastChild() is { } c && c != found.GetFirstChild()) found.Remove(c);
            foreach (var printer in _store.Discovered)
            {
                found.Append(Button("printer-symbolic", $"{printer.Name} · {printer.ModelName}", () =>
                {
                    name.SetText(printer.Name); host.SetText(printer.Host); serial.SetText(printer.Serial);
                    code.SetText(_store.SlicerPrinter(printer.Serial)?.AccessCodes.FirstOrDefault() ?? "");
                }, true));
            }
            foreach (var printer in _store.SlicerPrinters.Where(p => !_store.Discovered.Any(d => d.Serial == p.Serial)))
                found.Append(Button("printer-symbolic", $"{printer.Name ?? printer.Serial} · {printer.Source}", () => { name.SetText(printer.Name ?? ""); host.SetText(printer.Host ?? ""); serial.SetText(printer.Serial); code.SetText(printer.AccessCodes.FirstOrDefault() ?? ""); }, true));
            if (_store.ReadySlicerPrinters.Count > 1)
                found.Append(Button("list-add-symbolic", Loc.F("Add All %lld", _store.ReadySlicerPrinters.Count), () => Async(async () =>
                {
                    var printers = _store.ReadySlicerPrinters.ToArray();
                    if (!_ephemeral) foreach (var p in printers) await _secrets.Save(p.Serial, p.AccessCodes[0]);
                    _store.AddAll(printers); _nav.SelectRow(_nav.GetRowAtIndex(0)); Navigate("Printer");
                }), true));
            if (_store.Discovered.Count == 0 && _store.SlicerPrinters.Count == 0) found.Append(Label(Loc.T("Looking for Bambu Lab printers on your network…"), "muted"));
        }
    }

    private void FilamentPage()
    {
        Header(Loc.T("Filament"));
        if (_store.Current is not { } session) { _pageHost.Append(Label(Loc.T("No printer set up"), "muted")); return; }
        _pageHost.Append(Live(() => $"{session.Status?.AllSpools.Count(s => s.IsLoaded) ?? 0} spools loaded · {Format.Grams(session.Status?.AllSpools.Sum(s => s.GramsLeft ?? 0) ?? 0)} of filament on hand", null, "muted"));
        foreach (var unit in session.Status?.Ams ?? [])
        {
            var heading = Row(); var title = Label(unit.Title, "heading"); title.SetHexpand(true); heading.Append(title);
            heading.Append(Live(() => Climate(session.Status?.Ams.FirstOrDefault(a => a.Id == unit.Id)), null, "muted")); _pageHost.Append(heading);
            var grid = CardGrid();
            foreach (var spool in unit.Spools) grid.Insert(SpoolCard(session, spool.Id), -1);
            _pageHost.Append(grid);
        }
        if (session.Status?.ExternalSpool is { } external)
        {
            _pageHost.Append(Label(Loc.T("External spool"), "heading"));
            var grid = CardGrid(); grid.Insert(SpoolCard(session, external.Id), -1); _pageHost.Append(grid);
        }
    }
    private void FleetPage()
    {
        Header(Loc.T("All Printers"));
        foreach (var session in _store.Sessions.OrderBy(s => s.Status?.State switch { JobState.Failed => 0, JobState.Paused => 1, JobState.Printing => 2, _ => 3 }).ThenBy(s => s.Status?.RemainingMinutes ?? int.MaxValue))
        {
            var box = Module(); var row = Row(); var title = Column(4); title.SetHexpand(true);
            title.Append(Label(session.Config.Name, "heading")); title.Append(Live(() => $"{session.Config.ModelName} · {session.ShortState}", null, "muted")); row.Append(title);
            row.Append(Live(() => session.Status is { } s ? $"{s.Progress}% · {Format.Duration(s.RemainingMinutes)}" : "—"));
            row.Append(Button("go-next-symbolic", Loc.T("Open"), () => { _store.Select(session.Id); _nav.SelectRow(_nav.GetRowAtIndex(0)); Navigate("Printer"); })); box.Append(row);
            var progress = Gtk.ProgressBar.New(); _updates.Add(() => progress.SetFraction((session.Status?.Progress ?? 0) / 100.0)); box.Append(progress); _pageHost.Append(box);
        }
        if (!_store.IsConfigured) _pageHost.Append(Button("list-add-symbolic", Loc.T("Add Printer…"), () => BeginSetup(), true));
    }
    private void PrintersPage()
    {
        Header(Loc.T("Printers")).Append(Button("list-add-symbolic", Loc.T("Add Printer…"), () => BeginSetup(), true));
        foreach (var session in _store.Sessions)
        {
            var group = Module(); var row = Row(); var entry = Gtk.Entry.New(); entry.SetText(session.Config.Name); entry.SetHexpand(true); row.Append(entry);
            row.Append(Button("object-select-symbolic", Loc.T("Save"), () => { _store.Rename(session.Id, entry.GetText().Trim()); Rebuild(); }));
            row.Append(Button("edit-delete-symbolic", Loc.T("Remove"), () => Confirm(Loc.T("Remove"), session.Config.Name, Loc.T("Remove"), () => _store.Forget(session.Id)))); group.Append(row);
            Detail(group, "Model", () => session.Config.ModelName); Detail(group, "Address", () => session.Config.Host);
            Detail(group, "Status", () => session.LinkSummary);
            Toggle(group, "Camera", session.Config.CameraEnabled == true, value => _store.SetCameraEnabled(value, session.Id)); _pageHost.Append(group);
        }
    }
    private void NotificationsPage()
    {
        Header(Loc.T("Notifications"), Loc.T("Bambu Bar tells you when a print needs you, even while its window is closed."));
        _pageHost.Append(Label(Loc.T("Notify me when"), "heading")); var box = Module();
        Toggle(box, "Print finished", _store.NotifyFinished, value => _store.NotifyFinished = value);
        Toggle(box, "Print failed, canceled or an error", _store.NotifyFailed, value => _store.NotifyFailed = value);
        Toggle(box, "Print paused (filament runout, user)", _store.NotifyPaused, value => _store.NotifyPaused = value);
        Toggle(box, "Print started", _store.NotifyStarted, value => _store.NotifyStarted = value);
        _pageHost.Append(box); _pageHost.Append(Button("preferences-system-notifications-symbolic", Loc.T("Send test notification"), () => _notifier.Post("Bambu Bar", Loc.T("Your printer notifications will appear here."), "test"), true));
    }
    private void SettingsPage()
    {
        Header(Loc.T("Settings"));
        var general = Module();
        if (!_ephemeral) Toggle(general, "Open at login", Environment.GetEnvironmentVariable("FLATPAK_ID") is not null ? _settings.Get("openAtLogin", false) : Platform.Autostart, value => Async(async () =>
        {
            if (Environment.GetEnvironmentVariable("FLATPAK_ID") is not null)
            {
                var allowed = await _background.Request(value);
                if (value && !allowed) throw new IOException("Background startup was not allowed by the desktop.");
            }
            else Platform.SetAutostart(value);
            _settings.Set("openAtLogin", value);
        }));
        else Toggle(general, "Open at login", false, _ => { });
        general.Append(Note("At login Bambu Bar starts in the background. Open it from the top bar or the app launcher to see this window."));
        _pageHost.Append(SettingsSection("General", "emblem-system-symbolic", general));

        var languages = Strings.Current?.Languages.ToArray() ?? ["en"];
        var language = Gtk.DropDown.NewFromStrings([Loc.T("System") + " (" + CultureInfo.CurrentUICulture.NativeName + ")", .. languages.Select(c => CultureInfo.GetCultureInfo(c).NativeName)]);
        var selectedLanguage = _settings.Get<string>("appLanguage");
        language.SetSelected((uint)(selectedLanguage is null ? 0 : Math.Max(0, Array.IndexOf(languages, selectedLanguage) + 1)));
        language.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            var selected = language.GetSelected() == 0 ? null : languages[(int)language.GetSelected() - 1];
            _settings.Set<string?>("appLanguage", selected);
            var strings = Strings.LoadFor(catalog => selected ?? catalog.Match(CultureInfo.CurrentUICulture));
            strings.Use(selected ?? strings.Match(CultureInfo.CurrentUICulture));
            for (var i = 0; i < _pages.Length; i++) if (_nav.GetRowAtIndex(i)?.GetChild() is Gtk.Box row && row.GetLastChild() is Gtk.Label label) label.SetText(Loc.T(_pages[i]));
            GLib.Functions.IdleAdd(200, () => { if (!_disposed) Rebuild(); return false; });
        };
        var languageCard = Module(SettingRow("Language", language), Note("Bambu Bar follows your system language unless you pick another one here."));
        _pageHost.Append(SettingsSection("Language", "preferences-desktop-locale-symbolic", languageCard));

        var camera = Module(); Toggle(camera, "Show the printer camera", _store.CameraEnabled, v => { _store.CameraEnabled = v; BuildPanel(); });
        camera.Append(Note("Turn the camera on for each printer too, on the Printers page. While it's off, the camera is hidden everywhere: the Printer page, the top bar panel and the menus."));
        var advancedCamera = Gtk.Expander.New(Loc.T("Camera options")); var cameraOptions = Column(8);
        Toggle(cameraOptions, "Keep the camera connected during prints", _store.CameraStaysOnWhilePrinting, v => _store.CameraStaysOnWhilePrinting = v);
        advancedCamera.SetChild(cameraOptions); camera.Append(advancedCamera);
        _pageHost.Append(SettingsSection("Camera", "camera-video-symbolic", camera));

        var updates = Module(Note("Updates are managed by your Linux installation source. Install a newer Bambu Bar package to update this local build."));
        var version = Row(10); version.Append(Gtk.Image.NewFromIconName("software-update-available-symbolic"));
        var versionText = Column(2); versionText.Append(Label($"{Loc.T("Version")} {AppVersion.Marketing}", "heading"));
        versionText.Append(Label(Environment.GetEnvironmentVariable("FLATPAK_ID") is not null ? "Flatpak · " + Platform.Commit : "Linux · " + Platform.Commit, "muted", "caption"));
        versionText.SetHexpand(true); version.Append(versionText); updates.Append(version);
        updates.Append(Button("web-browser-symbolic", Loc.T("Downloads"), () => Open("https://bambubar.app/download"), true));
        _pageHost.Append(SettingsSection("Updates", "folder-download-symbolic", updates));

        var appearance = Gtk.DropDown.NewFromStrings([Loc.T("Dark"), Loc.T("Light"), Loc.T("System")]);
        appearance.SetSelected((uint)_settings.Get("linuxAppearance", 0));
        appearance.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") { _settings.Set("linuxAppearance", (int)appearance.GetSelected()); ApplyAppearance(); } };
        var preferences = Module(SettingRow("Appearance", appearance));
        Toggle(preferences, "Confirm before stopping a print", _store.ConfirmStop, v => _store.ConfirmStop = v);
        _pageHost.Append(SettingsSection("Preferences", "preferences-desktop-display-symbolic", preferences));
    }
    private void AboutPage()
    {
        Header("Bambu Bar"); var icon = Gtk.Picture.NewForFilename(Path.Combine(AppContext.BaseDirectory, "AppIcon.png")); icon.SetSizeRequest(112, 112); icon.SetHalign(Gtk.Align.Start); _pageHost.Append(icon);
        _pageHost.Append(Label($"{AppVersion.Marketing} ({Platform.Commit})", "heading"));
        _pageHost.Append(Label("© 2026 NINA LABS, LLC · MIT License", "muted"));
        _pageHost.Append(Button("web-browser-symbolic", "bambubar.app", () => Open("https://bambubar.app"), true));
        _pageHost.Append(Button("emblem-favorite-symbolic", Loc.T("Support this project"), () => Open("https://bambubar.app/download"), true));
        var license = Module(Label(Loc.T("License"), "section-title"));
        var key = Gtk.Entry.New(); key.SetVisibility(false); key.SetPlaceholderText(Loc.T("License key")); license.Append(key);
        var actions = Row();
        var activate = Button("emblem-ok-symbolic", Loc.T("Activate"), () => { _license.Activate(key.GetText()); key.SetText(""); }, true);
        var check = Button("view-refresh-symbolic", Loc.T("Check License"), _license.Refresh, true);
        var deactivate = Button("list-remove-symbolic", Loc.T("Deactivate"), () => Confirm(Loc.T("Deactivate this installation?"), "", Loc.T("Deactivate"), _license.Deactivate), true);
        actions.Append(activate); actions.Append(check); actions.Append(deactivate); license.Append(actions);
        var state = Live(() => _license.Busy ? Loc.T("Checking…") : _license.HasAccess ? Loc.T("Hero Supporter") : _license.Message ?? Loc.T("Support this project")); state.SetWrap(true); license.Append(state);
        _updates.Add(() => { activate.SetSensitive(!_license.Busy && !_license.HasRecord); check.SetSensitive(!_license.Busy && _license.HasRecord); deactivate.SetSensitive(!_license.Busy && _license.HasRecord); key.SetSensitive(!_license.Busy && !_license.HasRecord); });
        _pageHost.Append(license);
        var about = Label(Loc.T("Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab."), "muted"); about.SetWrap(true); _pageHost.Append(about);
    }
}
