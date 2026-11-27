using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Theme;

namespace BambuBar.AppWindow;

/// <summary>
/// What the printers report and what happened recently (the Mac's ActivityPage, 3.1.0): each printer's reported
/// capabilities, the events Core's <see cref="ActivityLog"/> recorded (connection, print, issues, camera, commands)
/// newest first with a filter, and an export as JSON without access codes, full serial numbers or addresses. The log
/// records whether or not the page is open; the page follows it only while it is on show (the window refreshes it),
/// and starts on All each time it is opened.
/// </summary>
internal sealed class ActivityPage(WindowContext ctx) : Page(ctx)
{
    /// <summary>Events shown at first, and added by each "Show more" (the Mac's displayLimit).</summary>
    private const int PageSize = 200;

    // Segoe Fluent Icons for the Mac's SF Symbols.
    private const string ClearGlyph = "";         // Delete (trash)
    private const string ExportGlyph = "";        // Export (square.and.arrow.up)
    private const string CapabilitiesGlyph = "";  // BulletedList (list.bullet.rectangle)
    private const string AbsentGlyph = "";        // Remove (minus)
    private const string ConnectionGlyph = "";    // Wifi (wifi)
    private const string ActionGlyph = "";        // TouchPointer (hand.tap.fill)

    private static readonly ButtonLook ChipLook = new(ThemeKeys.SubtleHover, ThemeKeys.Track, ThemeKeys.SubtlePressed);
    private static readonly ButtonLook PickedChipLook = new(ThemeKeys.ControlSelected, ThemeKeys.ControlSelected, ThemeKeys.SubtlePressed);

    private readonly Region _capabilities = new();
    private readonly Region _events = new();
    private readonly List<(ChromeButton Chip, TextBlock Label, ActivityLog.Category? Category)> _chips = [];
    private readonly DispatcherTimer _exportedTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private ChromeButton _clear = null!;
    private ChromeButton _export = null!;
    private StackPanel _exported = null!;
    private TextBlock _exportedText = null!;
    private ChromeButton? _more;
    private ActivityLog.Category? _filter;
    private int _limit = PageSize;

    public override FrameworkElement Build()
    {
        _clear = W.Button(Loc.T("Clear"), Clear, small: true, glyph: ClearGlyph);
        _export = W.Button(Loc.T("Export…"), Export, small: true, glyph: ExportGlyph);
        var check = Icons.Glyph(Icons.Check, 12, null, Ctx.Ink(Palette.Accent));
        check.Margin = new Thickness(0, 0, 6, 0);
        _exportedText = Ui.Text(Loc.T("Activity exported"), WRamp.Note, null, ThemeKeys.TextSecondary);
        // Read out when it appears, without taking the focus from Export.
        AutomationProperties.SetLiveSetting(_exportedText, AutomationLiveSetting.Polite);
        _exported = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            Visibility = Visibility.Collapsed, Children = { check, _exportedText },
        };
        _exportedTimer.Tick += (_, _) => HideExported();
        var actions = new Stack { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _exported, _clear, _export } };

        var capabilities = W.Group(Loc.T("Reported Capabilities"), CapabilitiesGlyph, out var capabilityBody);
        capabilityBody.Children.Add(_capabilities.Host);

        var icon = Icons.Glyph(AppPage.Activity.Glyph(), 16, ThemeKeys.TextPrimary);
        icon.Margin = new Thickness(0, 0, 8, 0);
        var heading = Ui.Text(Loc.T("Events"), WRamp.GroupTitle, FontWeights.SemiBold);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, heading },
        };
        // The filters wrap onto a second line in a language whose names don't fit beside the heading.
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        chips.Children.Add(Chip(null));
        foreach (var category in ActivityLog.Categories) chips.Children.Add(Chip(category));
        PaintChips();
        var header = Ui.Columns("Auto", "*");
        header.Children.Add(Ui.At(title, 0));
        header.Children.Add(Ui.At(chips, 1));
        // A group named by its heading, like the settings groups: the filters and the list belong to "Events".
        var events = new Stack
        {
            Spacing = 8, Role = AutomationControlType.Group,
            Children = { header, Ui.Card(_events.Host, Metrics.Card, new Thickness(0)) },
        };
        AutomationProperties.SetLabeledBy(events, heading);

        return new Stack { Spacing = 22, Children = { W.PageHeader(Loc.T("Activity"), null, actions), capabilities, events } };
    }

    /// <summary>After every change of the store, and of the log while the page is on show.</summary>
    public override void Refresh()
    {
        var summaries = Store.CapabilitySummaries();
        _capabilities.Show(string.Join('|', summaries) + "|" + Loc.Language, () => Capabilities(summaries));
        RenderEvents();
    }

    public override void Dispose() => _exportedTimer.Stop();

    // MARK: Capabilities

    private UIElement Capabilities(IReadOnlyList<ActivityLog.CapabilitySummary> summaries)
    {
        if (summaries.Count == 0)
        {
            var printer = Icons.Glyph(Icons.Printer, 14);
            printer.Margin = new Thickness(0, 0, 8, 0);
            var none = Ui.Text(Loc.T("No printer configured"), WRamp.Detail, null, ThemeKeys.TextSecondary);
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { printer, none } };
        }
        var stack = new Stack { Spacing = 14 };
        foreach (var cap in summaries)
        {
            if (stack.Children.Count > 0) stack.Children.Add(W.Divider());
            stack.Children.Add(Capability(cap));
        }
        return stack;
    }

    /// <summary>One printer: "● Name  P2S  22E…                Online" over what it has, two to a row.</summary>
    private Stack Capability(ActivityLog.CapabilitySummary cap)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(1, 1, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        if (cap.IsOnline) dot.Fill = Palette.Brush(Palette.Accent);
        else
        {
            dot.StrokeThickness = 1.5;
            dot.SetResourceReference(Shape.StrokeProperty, ThemeKeys.TextTertiary);
        }
        var name = Ui.Text("", WRamp.Body, FontWeights.SemiBold);
        name.Inlines.Add(new Run(cap.PrinterName));
        var model = new Run("  " + cap.ModelName) { FontSize = WRamp.Detail, FontWeight = FontWeights.Normal };
        model.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextSecondary);
        name.Inlines.Add(model);
        if (cap.SerialPrefix.Length > 0)
        {
            var serial = new Run("  " + cap.SerialPrefix + "…") { FontSize = WRamp.Note, FontWeight = FontWeights.Normal };
            serial.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontMono);
            serial.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextTertiary);
            name.Inlines.Add(serial);
        }
        var state = cap.IsOnline ? Loc.T("Online") : Loc.T("Offline");
        var status = Ui.Text(state, WRamp.Note, FontWeights.Medium, ThemeKeys.TextSecondary);
        if (cap.IsOnline) status.Foreground = Ctx.Ink(Palette.Accent);
        status.Margin = new Thickness(12, 0, 0, 0);
        var top = Ui.Columns("Auto", "*", "Auto");
        top.Children.Add(Ui.At(dot, 0));
        top.Children.Add(Ui.At(name, 1));
        top.Children.Add(Ui.At(status, 2));

        var items = Items(cap);
        var grid = Ui.Columns("*", "24", "*");
        for (var row = 0; row < (items.Length + 1) / 2; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < items.Length; i++)
        {
            var item = Item(items[i].Label, items[i].Has, items[i].Detail);
            item.Margin = new Thickness(0, i < 2 ? 0 : 8, 0, 0);
            grid.Children.Add(Ui.At(item, i % 2 * 2, i / 2));
        }
        return new Stack { Spacing = 10, Children = { Ui.ReadAsOne(top, $"{cap.PrinterName}, {cap.ModelName}, {state}"), grid } };
    }

    /// <summary>What the Mac's card lists, in its order.</summary>
    private static (string Label, bool Has, string Detail)[] Items(ActivityLog.CapabilitySummary cap) =>
    [
        (Loc.T("AMS"), cap.AmsCount > 0, cap.AmsCount > 0 ? cap.AmsCount.ToString(CultureInfo.CurrentCulture) : ""),
        (Loc.T("Chamber sensor"), cap.HasChamberSensor, cap.HasChamberHeater ? Loc.T("Heated") : ""),
        (Loc.T("Chamber light"), cap.HasChamberLight, cap.HasDualLight ? Loc.T("Dual") : ""),
        (Loc.T("Camera"), cap.CameraEnabled, cap.CameraEnabled ? Loc.T("Enabled") : Loc.T("Off")),
        (Loc.T("Nozzles"), true, cap.NozzleCount.ToString(CultureInfo.CurrentCulture)),
        (Loc.T("Aux & chamber fans"), cap.HasAuxFan || cap.HasChamberFan, (cap.HasAuxFan, cap.HasChamberFan) switch
        {
            (true, true) => Loc.T("Aux · Chamber"),
            (true, false) => Loc.T("Aux"),
            (false, true) => Loc.T("Chamber"),
            _ => "",
        }),
    ];

    /// <summary>"✓ Chamber sensor ……… Heated", or "– AMS" for what the printer doesn't have; read as "AMS: None".</summary>
    private FrameworkElement Item(string label, bool has, string detail)
    {
        var mark = has ? Icons.Glyph(Icons.Check, 11, null, Ctx.Ink(Palette.Accent)) : Icons.Glyph(AbsentGlyph, 11);
        mark.Width = 14;
        mark.Margin = new Thickness(0, 0, 8, 0);
        var grid = Ui.Columns("Auto", "*", "Auto");
        grid.Children.Add(Ui.At(mark, 0));
        grid.Children.Add(Ui.At(Ui.Text(label, WRamp.Detail, null, has ? ThemeKeys.TextPrimary : ThemeKeys.TextSecondary), 1));
        if (detail.Length > 0)
        {
            var value = Ui.Tabular(Ui.Text(detail, WRamp.Note, null, ThemeKeys.TextSecondary));
            value.Margin = new Thickness(8, 0, 0, 0);
            grid.Children.Add(Ui.At(value, 2));
        }
        var said = detail.Length > 0 ? detail : has ? "" : Loc.T("None");
        return Ui.ReadAsOne(grid, said.Length > 0 ? $"{label}: {said}" : label);
    }

    // MARK: Events

    /// <summary>A filter: one of a set, the picked one in the accent (the Mac's capsules).</summary>
    private ChromeButton Chip(ActivityLog.Category? category)
    {
        var title = category is { } c ? ActivityLog.Title(c) : Loc.T("All");
        var label = Ui.Text(title, WRamp.Note, FontWeights.Medium, ThemeKeys.TextSecondary);
        var chip = new ChromeButton
        {
            Content = label, Height = 26, Padding = new Thickness(10, 0, 10, 1), Margin = new Thickness(4, 2, 0, 2),
            CornerRadius = new CornerRadius(13), Role = AutomationControlType.RadioButton, Selected = false,
        };
        AutomationProperties.SetName(chip, title);
        chip.Click += (_, _) =>
        {
            _filter = category;
            _limit = PageSize;
            PaintChips();
            RenderEvents();
        };
        _chips.Add((chip, label, category));
        return chip;
    }

    private void PaintChips()
    {
        foreach (var (chip, label, category) in _chips)
        {
            var picked = category == _filter;
            chip.Selected = picked;
            chip.Look = picked ? PickedChipLook : ChipLook;
            label.FontWeight = picked ? FontWeights.SemiBold : FontWeights.Medium;
            if (picked) label.Foreground = Ctx.Ink(Palette.Tint);
            else label.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextSecondary);
        }
    }

    /// <summary>Draws the list again when the log, the filter or the number shown changed since it was drawn.</summary>
    private void RenderEvents()
    {
        var log = Store.Activity;
        _clear.IsEnabled = log.Count > 0;
        var showPrinter = Store.HasMultiplePrinters;
        _events.Show(string.Create(CultureInfo.InvariantCulture, $"{log.Version}|{_filter}|{_limit}|{showPrinter}|{Loc.Language}"),
            () => Events(log, showPrinter));
    }

    private UIElement Events(ActivityLog log, bool showPrinter)
    {
        _more = null;
        var entries = log.Entries.Reverse().Where(e => _filter is null || e.Category == _filter).ToList();
        if (entries.Count == 0)
        {
            var empty = Ui.Text(Loc.T("No activity recorded yet."), WRamp.Detail, null, ThemeKeys.TextSecondary);
            empty.Margin = new Thickness(16);
            return empty;
        }
        var rows = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var entry in entries.Take(_limit))
        {
            if (rows.Children.Count > 0) rows.Children.Add(RowDivider());
            rows.Children.Add(Row(entry, showPrinter));
        }
        if (entries.Count > _limit)
        {
            rows.Children.Add(RowDivider());
            _more = W.Link(Loc.T("Show more"), ShowMore);
            _more.HorizontalAlignment = HorizontalAlignment.Center;
            _more.Margin = new Thickness(0, 4, 0, 2);
            rows.Children.Add(_more);
        }
        return rows;
    }

    private void ShowMore()
    {
        var keyboard = _more?.IsKeyboardFocused == true;
        _limit += PageSize;
        RenderEvents();
        if (!keyboard) return;
        // The link went with the old rows. The focus goes to the new one, without scrolling down to it past the rows
        // that just appeared, or to the filters once everything is shown.
        if (_more is { } next)
        {
            next.RequestBringIntoView += Stay;
            next.Focus();
            next.RequestBringIntoView -= Stay;
        }
        else _chips[0].Chip.Focus();

        static void Stay(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;
    }

    /// <summary>One event: the category's icon, the printer (with several) and what happened, how the command went, and
    /// when. Read as one line.</summary>
    private FrameworkElement Row(ActivityLog.Entry entry, bool showPrinter)
    {
        // Redacted once more, as the Mac's rows are: an address inside a printer's name stays out of the list too.
        var printer = showPrinter ? ActivityLog.Redact(entry.PrinterName) : "";
        var summary = ActivityLog.Redact(entry.Summary);
        var outcome = entry.Outcome is { } o ? ActivityLog.Title(o) : "";
        var time = entry.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

        var icon = CategoryIcon(entry.Category);
        icon.Width = 18;
        icon.Margin = new Thickness(0, 2, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var text = Ui.Paragraph("", WRamp.Detail, ThemeKeys.TextPrimary);
        text.VerticalAlignment = VerticalAlignment.Top;
        if (printer.Length > 0)
        {
            var name = new Run(printer + "  ") { FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextSecondary);
            text.Inlines.Add(name);
        }
        text.Inlines.Add(new Run(summary));
        var clock = Ui.Text(time, WRamp.Note, null, ThemeKeys.TextTertiary);
        clock.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontMono);
        clock.Margin = new Thickness(10, 1, 0, 0);
        clock.VerticalAlignment = VerticalAlignment.Top;

        var grid = Ui.Columns("Auto", "*", "Auto", "Auto");
        grid.Children.Add(Ui.At(icon, 0));
        grid.Children.Add(Ui.At(text, 1));
        if (entry.Outcome is { } result) grid.Children.Add(Ui.At(Badge(result, outcome), 2));
        grid.Children.Add(Ui.At(clock, 3));
        var row = Ui.ReadAsOne(grid, string.Join(", ", new[] { printer, summary, outcome, time }.Where(part => part.Length > 0)));
        row.Padding = new Thickness(14, 7, 14, 7);
        return row;
    }

    /// <summary>The Mac's tints: the accent for connections and commands, green for prints, amber for issues.</summary>
    private TextBlock CategoryIcon(ActivityLog.Category category)
    {
        var (glyph, tint) = category switch
        {
            ActivityLog.Category.Connection => (ConnectionGlyph, (Color?)Palette.Tint),
            ActivityLog.Category.Job => (Icons.Printer, Palette.Accent),
            ActivityLog.Category.Issue => (Icons.Warning, Palette.Paused),
            ActivityLog.Category.Camera => (Icons.Camera, null),
            _ => (ActionGlyph, Palette.Tint),
        };
        return tint is { } color ? Icons.Glyph(glyph, 13, null, Ctx.Ink(color)) : Icons.Glyph(glyph, 13);
    }

    /// <summary>"Applied" in green, "Failed" in red, "Sent" plain: how a command or a camera stream went.</summary>
    private Border Badge(ActivityLog.Outcome outcome, string title)
    {
        var label = Ui.Text(title, Ramp.Caption, FontWeights.SemiBold, ThemeKeys.TextSecondary);
        var badge = new Border
        {
            Child = label, CornerRadius = Metrics.Pill, Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Ui.Capsule(badge);
        if (outcome == ActivityLog.Outcome.Sent) badge.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Track);
        else
        {
            var color = outcome == ActivityLog.Outcome.Applied ? Palette.Accent : Palette.Failed;
            label.Foreground = Ctx.Ink(color);
            Ui.TintCapsule(badge, color, Ctx.Dark);
        }
        return badge;
    }

    /// <summary>A hairline between two events, from where their text starts.</summary>
    private static Border RowDivider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(42, 0, 0, 0) };
        line.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Divider);
        return line;
    }

    // MARK: Actions

    private void Clear()
    {
        var keyboard = _clear.IsKeyboardFocused;
        Store.Activity.Clear();
        // Clear turns itself off: the focus moves on instead of getting lost with it.
        if (keyboard) _export.Focus();
    }

    /// <summary>Saves the Mac's export (the same keys) where the person says. Nothing in snapshots.</summary>
    private void Export()
    {
        if (Ctx.Services.Offscreen) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "Bambu Bar Activity " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json",
            Filter = "JSON|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true,
            // Where it opens the first time; afterwards Windows remembers where the last export went. Without it, a
            // copy started at sign-in opens the dialog in its working folder, System32.
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var owner = Window.GetWindow(_export);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
        try
        {
            System.IO.File.WriteAllBytes(dialog.FileName, Store.ExportActivityJson(AppVersion.Marketing));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            Dialogs.Notice(owner, Loc.T("Something went wrong"), e.Message, Loc.T("OK", "Windows: the button that closes an alert"));
            return;
        }
        _exported.Visibility = Visibility.Visible;
        Announce.Changed(_exportedText);
        _exportedTimer.Stop();
        _exportedTimer.Start();
    }

    private void HideExported()
    {
        _exportedTimer.Stop();
        _exported.Visibility = Visibility.Collapsed;
    }
}
