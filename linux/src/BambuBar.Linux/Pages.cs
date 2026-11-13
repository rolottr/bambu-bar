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
            _nav.SelectRow(_nav.GetRowAtIndex(Array.IndexOf(_pages, "Printer"))); Navigate("Printer");
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
                    _store.AddAll(printers); _nav.SelectRow(_nav.GetRowAtIndex(Array.IndexOf(_pages, "Printer"))); Navigate("Printer");
                }), true));
            if (_store.Discovered.Count == 0 && _store.SlicerPrinters.Count == 0) found.Append(Label(Loc.T("Looking for Bambu Lab printers on your network…"), "muted"));
        }
    }

    private void FilamentPage()
    {
        Header(Loc.T("Filament"), FilamentSummary());
        if (_store.Current is not { } session) { _pageHost.Append(Label(Loc.T("No printer set up"), "muted")); return; }
        if (!session.IsOnline || session.Status is null) { _pageHost.Append(Module(Label(Loc.T("Printer offline"), "heading"), Note("Filament appears once Bambu Bar is connected."))); return; }
        if (session.Status.Ams.Count == 0 && session.Status.ExternalSpool is null) { _pageHost.Append(Module(Label(Loc.T("No AMS connected"), "heading"), Note("Spools loaded in an AMS or on the external holder appear here."))); return; }
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
            var grid = CardGrid(); grid.Insert(SpoolCard(session, external.Id), -1);
            for (var i = 0; i < 3; i++) { var space = Column(); space.SetSizeRequest(173, -1); grid.Insert(space, -1); }
            _pageHost.Append(grid);
        }
    }
    private string FilamentSummary()
    {
        if (_store.Current?.Status is not { } s) return Loc.T("Spools in your AMS and on the external holder");
        var count = $"{s.AllSpools.Count(p => p.IsLoaded)} " + Loc.T("spools loaded");
        if (s.State.IsActive() && s.ActiveSpool is { } active) return $"{count} · {Loc.T("printing with")} {active.SlotName} {active.DisplayName}";
        var weights = s.AllSpools.Where(p => p.GramsLeft is not null).ToArray();
        return weights.Length == 0 ? count : $"{count} · {Format.Grams(weights.Sum(p => p.GramsLeft!.Value))} {Loc.T("of filament on hand")}";
    }
    private void FleetPage()
    {
        var summary = string.Join(" · ", _store.Sessions.GroupBy(s => s.IsOnline ? s.Status?.StateTitle ?? s.ShortState : Loc.T("Offline")).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}"));
        Header(Loc.T("All Printers"), summary);
        var grid = CardGrid(); grid.SetMaxChildrenPerLine(2);
        foreach (var session in _store.Sessions.OrderBy(s => !s.IsOnline ? 6 : s.Status?.State switch { JobState.Failed => 0, JobState.Paused => 1, JobState.Printing or JobState.Preparing => 2, JobState.Finished => 3, _ => 4 }).ThenBy(s => s.Status?.RemainingMinutes ?? int.MaxValue).ThenBy(s => s.Config.Name))
        {
            var row = Row(12); row.Append(PrinterThumbnail(session, 64));
            var info = Column(5); info.SetHexpand(true); var top = Row(6); var name = Label(session.Config.Name, "heading"); name.SetHexpand(true); name.SetEllipsize(Pango.EllipsizeMode.End); top.Append(name);
            top.Append(Live(() => session.IsOnline ? session.Status?.StateTitle ?? session.ShortState : session.ShortState, null, "badge")); info.Append(top);
            var detail = Live(() => session.Status?.MostSevereIssue?.Message ?? session.Status?.JobName ?? session.Config.ModelName, null, "caption", "muted"); detail.SetEllipsize(Pango.EllipsizeMode.End); info.Append(detail);
            var progress = Gtk.ProgressBar.New(); TintProgress(progress, () => session.Status?.ActiveSpool?.Color); info.Append(progress);
            var timing = Live(() => session.Status is { } state && session.IsOnline ? $"{state.Progress}% · {Format.Duration(state.RemainingMinutes)}" : "", null, "caption", "muted"); info.Append(timing);
            _updates.Add(() => { var active = session.IsOnline && (session.Status?.State.IsActive() == true || session.Status?.State == JobState.Finished); progress.SetOpacity(active ? 1 : 0); timing.SetOpacity(active ? 1 : 0); progress.SetFraction(Math.Clamp((session.Status?.Progress ?? 0) / 100.0, 0, 1)); });
            row.Append(info); var button = Gtk.Button.New(); button.AddCssClass("module"); button.SetChild(row);
            var stateClass = "";
            _updates.Add(() => { var next = "fleet-" + (session.IsOnline ? session.Status?.State.ToString().ToLowerInvariant() : "offline"); if (stateClass == next) return; if (stateClass.Length > 0) button.RemoveCssClass(stateClass); button.AddCssClass(next); stateClass = next; });
            button.OnClicked += (_, _) => { _store.Select(session.Id); Navigate("Printer"); }; grid.Insert(button, -1);
        }
        _pageHost.Append(grid);
    }
    private void PrintersPage()
    {
        Header(Loc.T("Printers"));
        var list = Module();
        foreach (var printer in _store.Sessions)
        {
            var row = Row(12); row.AddCssClass("printer-list-row");
            row.Append(PrinterThumbnail(printer, 30));
            var text = Column(3); text.SetHexpand(true); text.Append(Label(printer.Config.Name, "heading"));
            text.Append(Label($"Bambu Lab {printer.Config.ModelName} · {printer.Config.Host}", "caption", "muted")); row.Append(text);
            row.Append(Live(() => "● " + printer.ShortState, null, "caption", "muted"));
            if (_store.HasMultiplePrinters) row.Append(Button(printer == _store.Current ? "object-select-symbolic" : "go-next-symbolic", Loc.T("Select"), () => _store.Select(printer.Id)));
            list.Append(row); list.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        }
        var add = Row(); var note = Note(_store.IsConfigured ? "Bambu Bar watches all of them at once." : "No printer set up yet."); note.SetHexpand(true); add.Append(note);
        add.Append(Button("list-add-symbolic", Loc.T("Add Printer…"), () => BeginSetup(), true)); list.Append(add);
        _pageHost.Append(SettingsSection("Printers", "network-wired-symbolic", list));
        if (_store.Current is { } session)
        {
            var details = Module(); var name = Gtk.Entry.New(); name.SetText(session.Config.Name); name.SetWidthChars(22);
            var editName = Row(8); var revert = Button("edit-undo-symbolic", Loc.T("Revert"), () => name.SetText(session.Config.Name));
            var save = Button("object-select-symbolic", Loc.T("Save"), () => { _store.Rename(session.Id, name.GetText().Trim()); Rebuild(); });
            editName.Append(revert); editName.Append(save); editName.Append(name); details.Append(SettingRow("Name", editName));
            void NameChanged() { var dirty = name.GetText() != session.Config.Name; save.SetVisible(dirty); revert.SetVisible(dirty); save.SetSensitive(!string.IsNullOrWhiteSpace(name.GetText())); }
            name.OnChanged += (_, _) => NameChanged(); NameChanged();
            Detail(details, "Model", () => "Bambu Lab " + session.Config.ModelName);
            Detail(details, "Serial number", () => session.Config.Serial);
            Detail(details, "Status", () => "● " + session.LinkSummary);
            var address = Row(8); address.Append(Label(session.Config.Host, "muted")); address.Append(Button("document-edit-symbolic", Loc.T("Change…"), () => BeginSetup(session.Config), true));
            details.Append(SettingRow("IP address", address));
            Toggle(details, "Camera", session.Config.CameraEnabled == true, value => _store.SetCameraEnabled(value, session.Id));
            details.Append(Note("Shows the printer's live view. Turn on LAN Only Liveview in the printer's network settings when required."));
            details.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
            var forget = Row(); var follows = Note("Bambu Bar follows the printer if its IP address changes."); follows.SetHexpand(true); forget.Append(follows);
            forget.Append(Button("edit-delete-symbolic", Loc.T("Forget Printer…"), () => Confirm(Loc.T("Remove"), session.Config.Name, Loc.T("Remove"), () => _store.Forget(session.Id)), true)); details.Append(forget);
            _pageHost.Append(SettingsSection(_store.HasMultiplePrinters ? session.Config.Name : "Printer", "printer-symbolic", details));
        }
        var controls = Module(); Toggle(controls, "Ask before stopping a print", _store.ConfirmStop, value => _store.ConfirmStop = value);
        controls.Append(Note("Pause, stop and speed changes need LAN Only mode with Developer Mode on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode."));
        _pageHost.Append(SettingsSection("Controls", "preferences-system-symbolic", controls));
    }
    private void NotificationsPage()
    {
        Header(Loc.T("Notifications"), Loc.T("Bambu Bar tells you when a print needs you, even while its window is closed."));
        var box = Module();
        Toggle(box, "Print finished", _store.NotifyFinished, value => _store.NotifyFinished = value);
        Toggle(box, "Print failed, canceled or an error", _store.NotifyFailed, value => _store.NotifyFailed = value);
        Toggle(box, "Print paused (filament runout, user)", _store.NotifyPaused, value => _store.NotifyPaused = value);
        Toggle(box, "Print started", _store.NotifyStarted, value => _store.NotifyStarted = value);
        _pageHost.Append(SettingsSection("Notify me when", "preferences-system-notifications-symbolic", box));
        var test = Button("preferences-system-notifications-symbolic", Loc.T("Send test notification"), () => _notifier.Post("Bambu Bar", Loc.T("Your printer notifications will appear here."), "test"), true);
        test.SetHalign(Gtk.Align.Start); _pageHost.Append(test);
    }
    private void SettingsPage()
    {
        Header(Loc.T("Settings"));
        var theme = Gtk.DropDown.NewFromStrings([Loc.T("Follow desktop") + (Platform.IsOmarchy ? " (Omarchy)" : Platform.IsPlasma ? " (KDE)" : Platform.IsGnome ? " (GNOME)" : ""), "GNOME (Adwaita)", "KDE (Breeze)", "Bambu Bar", "Omarchy"]);
        theme.SetSelected((uint)Array.IndexOf(ThemeIds, SelectedTheme));
        theme.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "selected" || theme.GetSelected() >= ThemeIds.Length) return;
            _settings.Set("linuxTheme", ThemeIds[(int)theme.GetSelected()]);
            ApplyAppearance();
        };
        var appearance = Gtk.DropDown.NewFromStrings([Loc.T("Dark"), Loc.T("Light"), Loc.T("System")]);
        appearance.SetSelected((uint)Math.Clamp(_settings.Get("linuxAppearance", 2), 0, 2));
        appearance.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") { _settings.Set("linuxAppearance", (int)appearance.GetSelected()); ApplyAppearance(); } };
        var themeCard = Module(SettingRow("Theme", theme), SettingRow("Color mode", appearance), Note("Choose a desktop style or the original Bambu Bar look. Changes apply immediately."));
        if (Platform.IsOmarchy || SelectedTheme == "omarchy")
            themeCard.Append(Note("Omarchy follows the active desktop palette live. System uses its light/dark mode; explicit Light or Dark adapts the palette while retaining its accent. If theme files are unavailable, native Adwaita is used."));
        _pageHost.Append(SettingsSection("Theme", "preferences-desktop-display-symbolic", themeCard));

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
        var systemLanguage = CultureInfo.CurrentUICulture.Equals(CultureInfo.InvariantCulture) ? "English" : CultureInfo.CurrentUICulture.NativeName;
        var language = Gtk.DropDown.NewFromStrings([Loc.T("System") + " (" + systemLanguage + ")", .. languages.Select(c => CultureInfo.GetCultureInfo(c).NativeName)]);
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
        versionText.SetHexpand(true); version.Append(versionText); version.Append(Button("web-browser-symbolic", Loc.T("Downloads"), () => Open("https://bambubar.app/download"), true)); updates.Append(version);
        _pageHost.Append(SettingsSection("Updates", "folder-download-symbolic", updates));
    }
    private static Gtk.Box AboutSection(string title, string icon, Gtk.Widget content)
    {
        var group = Column(8); group.Append(Label(Loc.T(title).ToUpperInvariant(), "section-title")); group.Append(content); return group;
    }
    private void AboutPage()
    {
        var hero = Row(20); var icon = Gtk.Picture.NewForFilename(Path.Combine(AppContext.BaseDirectory, "AppIcon.png")); icon.SetSizeRequest(96, 96); icon.SetCanShrink(true); hero.Append(icon);
        var title = Column(4); title.SetValign(Gtk.Align.Center); title.Append(Label("Bambu Bar", "page-title")); title.Append(Label("NINA LABS, LLC", "muted"));
        title.Append(Label($"{Loc.T("Version")} {AppVersion.Marketing} ({Platform.Commit})", "muted")); title.Append(Note("Your Bambu Lab printer, one click away in the top bar.")); hero.Append(title); _pageHost.Append(hero);
        var columns = Row(16); columns.SetHomogeneous(true); var copy = Module();
        Detail(copy, "Version", () => $"{AppVersion.Marketing} ({Platform.Commit})");
        Detail(copy, "System", () => "Linux · " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        Detail(copy, "License", () => Loc.T(_license.HasAccess ? "Hero Supporter" : "Free · Community edition"));
        var version = Button("edit-copy-symbolic", Loc.T("Copy Version Info"), () => Gdk.Display.GetDefault()!.GetClipboard().SetText($"Bambu Bar {AppVersion.Marketing} ({Platform.Commit})\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}"), true); version.SetHalign(Gtk.Align.End); copy.Append(version);
        columns.Append(AboutSection("This copy", "help-about-symbolic", copy));
        var links = Module(); links.AddCssClass("about-links");
        foreach (var (label, url) in new[] { ("bambubar.app", "https://bambubar.app"), ("Release Notes", "https://bambubar.app/changelog"), ("Report an Issue", "https://github.com/rolottr/bambu-bar/issues/new") })
        { var link = Button("web-browser-symbolic", Loc.T(label), () => Open(url), true); link.SetHalign(Gtk.Align.Start); links.Append(link); }
        columns.Append(AboutSection("Links", "web-browser-symbolic", links)); _pageHost.Append(columns);
        var license = Module(Label(Loc.T("Support Bambu Bar"), "job-title"), Note("Bambu Bar is free to use. Activate a supporter license to receive the Hero Supporter badge."), Note("Manage your installations in your account. Deactivate an old installation to make room for another.")); license.AddCssClass("license-card");
        var key = Gtk.Entry.New(); key.SetVisibility(false); key.SetPlaceholderText(Loc.T("License key")); key.SetHexpand(true);
        var actions = Row();
        var activate = Button("object-select-symbolic", Loc.T("Activate"), () => { _license.Activate(key.GetText()); key.SetText(""); }, true);
        var check = Button("view-refresh-symbolic", Loc.T("Check License"), _license.Refresh, true);
        var deactivate = Button("list-remove-symbolic", Loc.T("Deactivate"), () => Confirm(Loc.T("Deactivate this installation?"), "", Loc.T("Deactivate"), _license.Deactivate), true);
        actions.Append(key); actions.Append(activate); actions.Append(check); actions.Append(deactivate); license.Append(actions);
        var purchase = Row(); var support = Button("starred-symbolic", Loc.T("Support this project"), () => Open("https://bambubar.app/download"), true); support.AddCssClass("support"); support.SetHexpand(true); purchase.Append(support); purchase.Append(Button("web-browser-symbolic", Loc.T("Manage Purchase"), () => Open(BambuBar.Core.Licensing.PolarConfiguration.PortalUrl.ToString()), true)); license.Append(purchase);
        var state = Live(() => _license.Busy ? Loc.T("Checking…") : _license.HasAccess ? Loc.T("Hero Supporter") : _license.Message ?? Loc.T("Support this project")); state.SetWrap(true); license.Append(state);
        _updates.Add(() => { activate.SetVisible(!_license.HasRecord); key.SetVisible(!_license.HasRecord); check.SetVisible(_license.HasRecord); deactivate.SetVisible(_license.HasRecord); activate.SetSensitive(!_license.Busy && !_license.HasRecord); check.SetSensitive(!_license.Busy && _license.HasRecord); deactivate.SetSensitive(!_license.Busy && _license.HasRecord); key.SetSensitive(!_license.Busy && !_license.HasRecord); });
        _pageHost.Append(AboutSection("Your license", "application-certificate-symbolic", license));
        _pageHost.Append(AboutSection("How it works", "network-wireless-symbolic", Module(Note("Bambu Bar connects straight to your printer on the local network over encrypted MQTT, using the printer’s access code. The printer streams its status; the app shows it in the tray and shares a snapshot with the companion desktop widgets."))));
        _pageHost.Append(AboutSection("Privacy", "changes-prevent-symbolic", Module(Note("Printer data stays on your local network. No app analytics. Printer access codes and supporter license credentials are stored in your desktop keyring. Supporter licenses are validated online."))));
        var about = Label(Loc.T("Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab."), "muted"); about.SetWrap(true); _pageHost.Append(AboutSection("Credits", "help-about-symbolic", Module(Note("Protocol details and HMS error texts come from the open-source ha-bambulab project. The printer models are visual approximations built for this project in Blender."), about)));
    }
}
