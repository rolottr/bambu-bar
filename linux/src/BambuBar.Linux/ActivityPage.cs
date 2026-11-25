using System.Globalization;
using System.Text;
using BambuBar.Core;
using BambuBar.Core.App;

namespace BambuBar.Linux;

/// <summary>
/// The Activity page (the Mac's ActivityPage, 3.1.0): what each printer reports it has, a filterable list of recent
/// connection, print, issue, camera and command events, and a redacted JSON export. The GTK page serves GNOME and
/// Hyprland; <see cref="NativeActivity"/> feeds the same page to the Plasma and Omarchy windows. Both redraw only while
/// the page is on screen.
/// </summary>
internal sealed partial class Shell
{
    private const int ActivityPageSize = 200;
    private ActivityLog.Category? _activityFilter;
    private int _activityLimit = ActivityPageSize;
    private Action? _renderActivity;
    private bool _activityQueued;

    private void ResetActivityView() { _activityFilter = null; _activityLimit = ActivityPageSize; }

    private void ActivityChanged()
    {
        if (_disposed) return;
        if (UseNativeUi) { if (_nativePage == "Activity") QueueNative(); return; }
        if (_renderActivity is null || !_window.GetVisible() || _activityQueued) return;
        _activityQueued = true;
        GLib.Functions.IdleAdd(200, () => { _activityQueued = false; if (!_disposed) _renderActivity?.Invoke(); return false; });
    }

    private static string ActivityIcon(ActivityLog.Category category) => category switch
    {
        ActivityLog.Category.Connection => "network-wireless",
        ActivityLog.Category.Job => "printer",
        ActivityLog.Category.Issue => "dialog-warning",
        ActivityLog.Category.Camera => "camera-video",
        _ => "media-playback-start",
    };

    private static (string Label, bool On, string Detail)[] CapabilityItems(ActivityLog.CapabilitySummary cap) =>
    [
        (Loc.T("AMS"), cap.AmsCount > 0, cap.AmsCount > 0 ? cap.AmsCount.ToString(CultureInfo.CurrentCulture) : ""),
        (Loc.T("Chamber sensor"), cap.HasChamberSensor, cap.HasChamberHeater ? Loc.T("Heated") : ""),
        (Loc.T("Chamber light"), cap.HasChamberLight, cap.HasDualLight ? Loc.T("Dual") : ""),
        (Loc.T("Camera"), cap.CameraEnabled, Loc.T(cap.CameraEnabled ? "Enabled" : "Off")),
        (Loc.T("Nozzles"), true, cap.NozzleCount.ToString(CultureInfo.CurrentCulture)),
        (Loc.T("Aux & chamber fans"), cap.HasAuxFan || cap.HasChamberFan, (cap.HasAuxFan, cap.HasChamberFan) switch
        {
            (true, true) => Loc.T("Aux · Chamber"),
            (true, false) => Loc.T("Aux"),
            (false, true) => Loc.T("Chamber"),
            _ => "",
        }),
    ];

    private List<ActivityLog.Entry> FilteredActivity() =>
        _store.Activity.Entries.Reverse().Where(e => _activityFilter is null || e.Category == _activityFilter).ToList();

    private static string ActivityTime(ActivityLog.Entry entry) => entry.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    private static string ActivityFileName() =>
        "Bambu Bar Activity " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json";

    // MARK: GTK

    private void ActivityPage()
    {
        var log = _store.Activity;
        var header = Header(Loc.T("Activity"));
        var clear = Button("user-trash-symbolic", Loc.T("Clear"), log.Clear, true);
        clear.SetValign(Gtk.Align.Center);
        var export = Button("document-save-symbolic", Loc.T("Export…"), ExportActivity, true);
        export.SetValign(Gtk.Align.Center);
        header.Append(clear); header.Append(export);

        var capabilities = Column(14);
        _pageHost.Append(SettingsSection("Reported Capabilities", "view-list-bullet-symbolic", Module(capabilities)));
        var capabilityKey = "";
        void RenderCapabilities()
        {
            var summaries = _store.CapabilitySummaries();
            var key = string.Join('|', summaries);
            if (key == capabilityKey) return;
            capabilityKey = key;
            while (capabilities.GetFirstChild() is { } child) capabilities.Remove(child);
            if (summaries.Count == 0) { capabilities.Append(Label(Loc.T("No printer configured"), "muted")); return; }
            for (var i = 0; i < summaries.Count; i++)
            {
                if (i > 0) capabilities.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
                capabilities.Append(CapabilitySection(summaries[i]));
            }
        }

        var events = Column(10);
        var eventsHeader = Row(8);
        var eventsTitle = Row(8); eventsTitle.SetHexpand(true);
        var eventsIcon = Gtk.Image.NewFromIconName("document-open-recent-symbolic"); eventsIcon.SetPixelSize(14);
        eventsTitle.Append(eventsIcon); eventsTitle.Append(Label(Loc.T("Events"), "heading")); eventsHeader.Append(eventsTitle);
        var chips = Row(4); chips.SetValign(Gtk.Align.Center); eventsHeader.Append(chips); events.Append(eventsHeader);
        var chipButtons = new List<(Gtk.Button Button, ActivityLog.Category? Category)>();
        var card = Column(0); card.AddCssClass("module"); card.AddCssClass("activity-card");
        var empty = Label(Loc.T("No activity recorded yet."), "muted"); empty.SetMarginStart(16); empty.SetMarginEnd(16); empty.SetMarginTop(16); empty.SetMarginBottom(16);
        var list = Gtk.ListBox.New(); list.SetSelectionMode(Gtk.SelectionMode.None); list.AddCssClass("activity-list");
        var scroll = Gtk.ScrolledWindow.New(); scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetMinContentHeight(220); scroll.SetMaxContentHeight(360); scroll.SetPropagateNaturalHeight(true); scroll.SetChild(list);
        card.Append(empty); card.Append(scroll); events.Append(card);
        _pageHost.Append(events);

        var renderedKey = "";
        foreach (var category in new ActivityLog.Category?[] { null }.Concat(ActivityLog.Categories.Cast<ActivityLog.Category?>()))
        {
            var chip = Gtk.Button.NewWithLabel(category is { } c ? ActivityLog.Title(c) : Loc.T("All"));
            chip.AddCssClass("activity-chip");
            chip.OnClicked += (_, _) => { _activityFilter = category; _activityLimit = ActivityPageSize; RenderEvents(); };
            chips.Append(chip); chipButtons.Add((chip, category));
        }
        void RenderEvents()
        {
            var key = $"{log.Version}|{_activityFilter}|{_activityLimit}|{_store.HasMultiplePrinters}|{Loc.Language}";
            clear.SetSensitive(log.Count > 0);
            if (key == renderedKey) return;
            renderedKey = key;
            foreach (var (button, category) in chipButtons)
            {
                if (category == _activityFilter) button.AddCssClass("selected"); else button.RemoveCssClass("selected");
            }
            while (list.GetFirstChild() is { } child) list.Remove(child);
            var entries = FilteredActivity();
            empty.SetVisible(entries.Count == 0); scroll.SetVisible(entries.Count > 0);
            var showPrinter = _store.HasMultiplePrinters;
            foreach (var entry in entries.Take(_activityLimit)) list.Append(EventRow(entry, showPrinter));
            if (entries.Count > _activityLimit)
            {
                var more = Gtk.Button.NewWithLabel(Loc.T("Show more")); more.AddCssClass("flat"); more.SetHalign(Gtk.Align.Center);
                more.OnClicked += (_, _) => { _activityLimit += ActivityPageSize; RenderEvents(); };
                list.Append(more);
            }
        }

        _renderActivity = () => { RenderCapabilities(); RenderEvents(); };
        _cleanup.Add(() => _renderActivity = null);
        _updates.Add(RenderCapabilities);
        _updates.Add(RenderEvents);
        RenderCapabilities(); RenderEvents();
    }

    private static Gtk.Box CapabilitySection(ActivityLog.CapabilitySummary cap)
    {
        var section = Column(10);
        var top = Row(8);
        var dot = Label("●", cap.IsOnline ? "success" : "muted", "caption"); top.Append(dot);
        var name = Label(cap.PrinterName, "heading"); name.SetEllipsize(Pango.EllipsizeMode.End); top.Append(name);
        top.Append(Label(cap.ModelName, "muted"));
        if (cap.SerialPrefix.Length > 0) top.Append(Label(cap.SerialPrefix + "…", "monospace", "caption", "muted"));
        var status = Label(Loc.T(cap.IsOnline ? "Online" : "Offline"), cap.IsOnline ? "success" : "muted", "caption");
        status.SetHexpand(true); status.SetXalign(1); top.Append(status);
        section.Append(top);
        var grid = Gtk.Grid.New(); grid.SetColumnSpacing(24); grid.SetRowSpacing(8); grid.SetColumnHomogeneous(true);
        var items = CapabilityItems(cap);
        for (var i = 0; i < items.Length; i++)
        {
            var (label, on, detail) = items[i];
            var row = Row(8);
            var mark = Gtk.Image.NewFromIconName(on ? "object-select-symbolic" : "list-remove-symbolic"); mark.SetPixelSize(12);
            mark.AddCssClass(on ? "success" : "muted"); row.Append(mark);
            var text = on ? Label(label) : Label(label, "muted"); text.SetHexpand(true); text.SetEllipsize(Pango.EllipsizeMode.End); row.Append(text);
            if (detail.Length > 0) row.Append(Label(detail, "muted", "caption"));
            grid.Attach(row, i % 2, i / 2, 1, 1);
        }
        section.Append(grid);
        return section;
    }

    private static Gtk.Box EventRow(ActivityLog.Entry entry, bool showPrinter)
    {
        var row = Row(10); row.AddCssClass("activity-row");
        var icon = Gtk.Image.NewFromIconName(ActivityIcon(entry.Category) + "-symbolic"); icon.SetPixelSize(14);
        icon.AddCssClass("activity-" + ActivityLog.Key(entry.Category)); icon.SetValign(Gtk.Align.Start); row.Append(icon);
        var text = Row(6); text.SetHexpand(true);
        if (showPrinter) { var printer = Label(ActivityLog.Redact(entry.PrinterName), "heading", "muted"); printer.SetValign(Gtk.Align.Start); text.Append(printer); }
        var summary = Label(ActivityLog.Redact(entry.Summary)); summary.SetWrap(true); summary.SetLines(2); summary.SetEllipsize(Pango.EllipsizeMode.End);
        summary.SetHexpand(true); text.Append(summary); row.Append(text);
        if (entry.Outcome is { } outcome)
        {
            var badge = Label(ActivityLog.Title(outcome), "badge", "activity-outcome-" + ActivityLog.Key(outcome));
            badge.SetValign(Gtk.Align.Center); row.Append(badge);
        }
        var time = Label(ActivityTime(entry), "monospace", "caption", "muted"); time.SetValign(Gtk.Align.Center); row.Append(time);
        return row;
    }

    private void ExportActivity() => Async(async () =>
    {
        var dialog = Gtk.FileDialog.New(); dialog.SetTitle(Loc.T("Export…")); dialog.SetInitialName(ActivityFileName());
        Gio.File? file;
        try { file = await dialog.SaveAsync(_window); }
        catch (GLib.GException) { return; } // Canceled.
        if (file?.GetPath() is not { } path) return;
        await File.WriteAllBytesAsync(path, _store.ExportActivityJson(AppVersion.Marketing));
        _store.ShowToast(Loc.T("Activity exported"));
    });

    // MARK: Plasma and Omarchy

    private object NativeActivity()
    {
        var entries = FilteredActivity();
        var showPrinter = _store.HasMultiplePrinters;
        return new
        {
            filter = _activityFilter is { } f ? ActivityLog.Key(f) : "",
            filters = new[] { new UiOption("", Loc.T("All")) }
                .Concat(ActivityLog.Categories.Select(c => new UiOption(ActivityLog.Key(c), ActivityLog.Title(c)))),
            canClear = _store.Activity.Count > 0,
            more = entries.Count > _activityLimit,
            capabilities = _store.CapabilitySummaries().Select(c => new
            {
                name = c.PrinterName, model = c.ModelName, serial = c.SerialPrefix.Length > 0 ? c.SerialPrefix + "…" : "",
                online = c.IsOnline, status = Loc.T(c.IsOnline ? "Online" : "Offline"),
                items = CapabilityItems(c).Select(i => new { label = i.Label, on = i.On, detail = i.Detail }),
            }),
            events = entries.Take(_activityLimit).Select(e => new
            {
                id = e.Id.ToString("N"), category = ActivityLog.Key(e.Category), icon = ActivityIcon(e.Category),
                printer = showPrinter ? ActivityLog.Redact(e.PrinterName) : "", summary = ActivityLog.Redact(e.Summary),
                outcome = e.Outcome is { } o ? ActivityLog.Key(o) : "", outcomeLabel = e.Outcome is { } t ? ActivityLog.Title(t) : "",
                time = ActivityTime(e),
            }),
            labels = new
            {
                capabilities = Loc.T("Reported Capabilities"), noPrinter = Loc.T("No printer configured"), events = Loc.T("Events"),
                empty = Loc.T("No activity recorded yet."), more = Loc.T("Show more"), clear = Loc.T("Clear"), export = Loc.T("Export…"),
            },
        };
    }

    /// <summary>The native windows' Activity requests. False when <paramref name="action"/> is not one of them.</summary>
    private bool NativeActivityRequest(string action, string text)
    {
        if (action.StartsWith("activity-filter:", StringComparison.Ordinal))
        {
            var key = action["activity-filter:".Length..];
            if (key.Length == 0 || ActivityLog.ParseCategory(key) is not null) { _activityFilter = ActivityLog.ParseCategory(key); _activityLimit = ActivityPageSize; }
            return true;
        }
        switch (action)
        {
            case "activity-clear": _store.Activity.Clear(); return true;
            case "activity-more": _activityLimit += ActivityPageSize; return true;
            case "export-activity":
                // The bridge writes the file on the desktop's side, and only to the path its own save dialog chose.
                _native?.Send(new
                {
                    type = "activity-export", path = text,
                    data = Encoding.UTF8.GetString(_store.ExportActivityJson(AppVersion.Marketing)),
                    done = Loc.T("Activity exported"),
                });
                return true;
            default: return false;
        }
    }
}
