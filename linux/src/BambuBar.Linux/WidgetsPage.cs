using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
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
    private string _widgetPreviewSize = "medium";
    private JobState _widgetPreviewState = JobState.Preparing;
    private void WidgetShowcase()
    {
        var stage = Column(22); stage.AddCssClass("widget-stage"); stage.SetHalign(Gtk.Align.Start);
        var slot = Column(0); slot.SetHalign(Gtk.Align.Center); stage.Append(slot);
        var scenes = Row(2); scenes.AddCssClass("scene-picker"); scenes.SetHalign(Gtk.Align.Center);
        var sceneButtons = new List<(JobState State, Gtk.ToggleButton Button)>();
        var sizeButtons = new List<(string Size, Gtk.ToggleButton Button)>();
        var details = Module();
        foreach (var (state, title) in new[] { (JobState.Printing, "▣ Printing"), (JobState.Preparing, "♨ Heating"), (JobState.Paused, "Ⅱ Paused"), (JobState.Finished, "✓ Finished"), (JobState.Idle, "☾ Ready") })
        {
            var button = Gtk.ToggleButton.NewWithLabel(Loc.T(title)); if (sceneButtons.Count > 0) button.SetGroup(sceneButtons[0].Button); button.SetActive(_widgetPreviewState == state);
            button.OnToggled += (_, _) => { if (!button.GetActive()) return; _widgetPreviewState = state; Draw(); }; scenes.Append(button); sceneButtons.Add((state, button));
        }
        stage.Append(scenes); _pageHost.Append(stage);
        var sizes = Row(0); sizes.AddCssClass("linked"); sizes.AddCssClass("size-picker"); sizes.SetHalign(Gtk.Align.Start);
        foreach (var size in new[] { "small", "medium", "large" })
        {
            var button = Gtk.ToggleButton.NewWithLabel(Loc.T(char.ToUpperInvariant(size[0]) + size[1..])); if (sizeButtons.Count > 0) button.SetGroup(sizeButtons[0].Button); button.SetActive(_widgetPreviewSize == size);
            button.OnToggled += (_, _) => { if (!button.GetActive()) return; _widgetPreviewSize = size; Draw(); }; sizes.Append(button); sizeButtons.Add((size, button));
        }
        _pageHost.Append(sizes);
        _pageHost.Append(details); Draw();
        void Draw()
        {
            foreach (var (state, button) in sceneButtons) button.SetActive(state == _widgetPreviewState);
            foreach (var (size, button) in sizeButtons) button.SetActive(size == _widgetPreviewSize);
            while (slot.GetFirstChild() is { } old) slot.Remove(old);
            while (details.GetFirstChild() is { } old) details.Remove(old);
            var small = _widgetPreviewSize == "small"; var large = _widgetPreviewSize == "large";
            var card = Column(8); card.AddCssClass("widget-sample"); card.SetSizeRequest(small ? 180 : 370, small ? 180 : large ? 330 : 160);
            var printing = _widgetPreviewState == JobState.Printing; var preparing = _widgetPreviewState == JobState.Preparing;
            var percent = preparing || _widgetPreviewState == JobState.Idle ? 0 : _widgetPreviewState == JobState.Finished ? 100 : printing ? 50 : 64;
            var content = Column(6); content.SetHexpand(true);
            var heading = Row(); var name = Label(_store.Current?.Config.Name ?? "Bambulab P2S", "caption", "heading", "muted"); name.SetHexpand(true); name.SetEllipsize(Pango.EllipsizeMode.End); heading.Append(name);
            var stateLabel = Label("● " + _widgetPreviewState.Title(), "caption", "heading", preparing ? "preparing" : _widgetPreviewState == JobState.Paused ? "warning" : "success"); heading.Append(stateLabel); if (!small) content.Append(heading);
            if (!small) content.Append(Label(_widgetPreviewState == JobState.Idle ? Loc.T("Ready to print") : "Articulated Dragon", "heading"));
            if (small)
            {
                var ring = Gtk.Image.New(); ring.SetPixelSize(58); ring.SetSizeRequest(58, 58);
                var dial = _widgetPreviewState == JobState.Finished ? "✓" : preparing ? "♨" : percent + "%";
                var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'><circle cx='50' cy='50' r='42' fill='none' stroke='#505055' stroke-width='10'/><circle cx='50' cy='50' r='42' fill='none' stroke='#ff9959' stroke-width='10' stroke-dasharray='{percent * 2.64} 264' transform='rotate(-90 50 50)'/><text x='50' y='57' text-anchor='middle' font-family='sans-serif' font-weight='bold' font-size='26' fill='white'>{dial}</text></svg>";
                using var bytes = GLib.Bytes.New(System.Text.Encoding.UTF8.GetBytes(svg)); ring.SetFromGicon(Gio.BytesIcon.New(bytes));
                var top = Row(10); top.Append(ring); var primary = Column(2); primary.SetHexpand(true);
                primary.Append(Label(_widgetPreviewState.Title().ToUpperInvariant(), "caption", "heading", preparing ? "preparing" : "success"));
                var summary = Label(printing ? "1h 12m" : preparing ? Loc.T("Auto bed leveling") : _widgetPreviewState == JobState.Paused ? Loc.T("Filament ran out") : _widgetPreviewState == JobState.Finished ? "12m " + Loc.T("ago") : "220° · 55°", "heading"); summary.SetWrap(true); summary.SetMaxWidthChars(11); primary.Append(summary);
                if (printing) primary.Append(Label(Loc.T("left"), "caption", "muted")); top.Append(primary); content.Append(top);
                var spacer = Column(); spacer.SetVexpand(true); content.Append(spacer);
                if (_widgetPreviewState.IsActive()) content.Append(Label("◉ PLA Matte · A1", "caption", "muted"));
                content.Append(Label(_widgetPreviewState == JobState.Idle ? Loc.T("Ready to print") : "Articulated Dragon", "heading"));
                content.Append(Label(printing ? Loc.T("Done") + " 13:38 · 144/284" : preparing ? "Nozzle 201° → 220°" : _widgetPreviewState == JobState.Finished ? Loc.T("Ready to remove") : "182/284", "caption", "muted"));
            }
            else
            {
                var numbers = Row(); var progressLabel = Label(percent + "%", "sample-percent"); progressLabel.SetHexpand(true); numbers.Append(progressLabel); numbers.Append(Label(preparing ? "0/305" : $"{percent * 284 / 100}/284", "caption", "muted", "heading")); content.Append(numbers);
                var progress = Gtk.ProgressBar.New(); progress.SetFraction(percent / 100.0); content.Append(progress);
            }
            if (!small) content.Append(Label(preparing ? Loc.T("Auto bed leveling") : printing ? "1h 12m " + Loc.T("left") + "    " + Loc.T("Done") + " 13:38" : _widgetPreviewState == JobState.Paused ? Loc.T("Filament ran out") : _widgetPreviewState == JobState.Finished ? Loc.T("Finished") + " · 12m" : Loc.T("Ready to print"), "caption", "heading"));
            if (!small) { var hero = Row(14); hero.Append(PreviewArt(_widgetPreviewState, percent / 100.0, 104)); hero.Append(content); card.Append(hero); var temperatures = SampleTemperatures(true, large); if (!large) temperatures.SetMarginStart(118); card.Append(temperatures); }
            else card.Append(content);
            if (large) card.Append(SampleSpools(false)); slot.Append(card);
            details.Append(Label(Loc.T(char.ToUpperInvariant(_widgetPreviewSize[0]) + _widgetPreviewSize[1..]), "heading"));
            details.Append(Note(small ? "The essentials at a glance: progress, time left and finish time. Paused, it says why; finished, how long ago." : large ? "Everything: progress and timing, temperature tiles, AMS spools with what's left, the newest error in plain words, and how the last print went." : "The printer as it is: the part growing in the filament's colour, the job and its progress, finish time and layers, and nozzle, bed and chamber temperatures."));
            var chips = Gtk.FlowBox.New(); chips.SetSelectionMode(Gtk.SelectionMode.None); chips.SetColumnSpacing(6); chips.SetRowSpacing(6); chips.SetMaxChildrenPerLine(6); chips.SetHomogeneous(false);
            foreach (var feature in new[] { "%  Progress", "◷  Time left", "⚑  Finish time", "▤  Layers", "Ⅱ  Stage & pause reason", "♨  Temperatures", "◉  AMS spools", "▣  Live drawing", "⚠  Errors", "✓  Last print" })
            {
                var enabled = feature.Contains("Errors") || feature.Contains("Last print") ? large : feature.Contains("Live drawing") ? !small : true;
                var chip = Label(Loc.T(feature), "feature-chip", enabled ? "feature-on" : "feature-off"); chips.Insert(chip, -1);
            }
            details.Append(chips);
        }
    }
    private void WidgetsPage()
    {
        Header(Loc.T("Widgets"), Loc.T("Your printers on the desktop, in three sizes."));
        WidgetShowcase();
        _pageHost.Append(Label(Loc.T("ADD A WIDGET"), "section-title"));
        var steps = Module();
        foreach (var (number, text) in new[] { ("1", "Enable desktop widgets below. Keep the Bambu Bar GNOME extension enabled."), ("2", "Choose Add widget, then pick a size and a desktop corner."), ("3", "Choose a printer for each widget, or Automatic to follow the active printer."), ("4", "Click a desktop widget to open its printer. Bambu Bar keeps it updated in the background.") })
        { var line = Row(10); line.Append(Label(number, "step-number")); var instruction = Note(text); instruction.RemoveCssClass("muted"); line.Append(instruction); steps.Append(line); }
        _pageHost.Append(steps);
        var options = Module();
        Toggle(options, "Show desktop widgets", _settings.Get("linuxWidgetsEnabled", false), enabled => { _settings.Set("linuxWidgetsEnabled", enabled); _desktop?.Queue(); });
        _pageHost.Append(options);

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
