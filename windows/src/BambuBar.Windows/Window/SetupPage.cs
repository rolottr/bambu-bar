using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;
using BambuBar.Platform;
using BambuBar.Theme;

namespace BambuBar.AppWindow;

/// <summary>
/// First run, and "Add Printer…" (the Mac's SetupPage, P3.6): pick the printer the search found (or type its address),
/// then the access code: from Bambu Studio or OrcaSlicer when either knows the printer (on this PC, or a settings file
/// from another computer picked or dropped here), else typed from the printer's screen. Connect saves it and opens its
/// page. Windows Firewall keeping the search out gets a notice with the way to allow it (ledger §4 trap 5).
/// </summary>
internal sealed class SetupPage(WindowContext ctx, DiscoveredPrinter? candidate) : Page(ctx)
{
    private string _host = "", _serial = "", _name = "", _modelCode = "N7";
    private string? _selected;
    /// <summary>The one found printer is picked automatically once; after that only the person changes the selection.</summary>
    private bool _autoPicked;
    /// <summary>Where the access code came from ("Bambu Studio"…), null when typed.</summary>
    private string? _codeSource;
    private string _foundCode = "";
    private List<string> _alternatives = [];
    private string? _importNote;
    /// <summary>Reading the serial from the printer at a typed address.</summary>
    private bool _identifying;
    /// <summary>Why the last Connect didn't get anywhere (nothing at the address, not a Bambu printer…).</summary>
    private string? _problem;
    /// <summary>Connect was tried before the printer and code were both there.</summary>
    private bool _attempted;
    private bool _manual;
    private bool _openAtSignIn = true;
    /// <summary>Fields being filled in from a pick: their change handlers leave the selection alone.</summary>
    private bool _filling;
    private bool _disposed;
    private string _slicerKey = "";

    private readonly Region _notice = new(), _addAll = new(), _list = new(), _code = new(), _status = new();
    private TextField _hostField = null!, _serialField = null!, _nameField = null!, _codeField = null!;
    private FrameworkElement _manualBody = null!;
    private TextBlock _manualChevron = null!;
    private ChromeButton _manualHeader = null!;
    private Spinner _searching = null!;
    private ChromeButton _connect = null!;
    private TextBlock _connectLabel = null!;
    private Spinner _connectSpinner = null!;
    private Border _entryCard = null!;
    private TextBlock _importTitle = null!, _importText = null!, _importIcon = null!;

    private bool Adding => Store.IsConfigured;

    public override FrameworkElement Build()
    {
        Store.ReloadSlicerPrinters();
        var page = new Stack { Spacing = 22 };
        page.Children.Add(Adding
            ? W.PageHeader(Loc.T("Add a Printer"), Loc.T("Bambu Bar watches every printer you add; pick which one the tray icon shows on the Tray page."),
                W.Button(Loc.T("Cancel"), () => Ctx.Show(AppPage.Printer)))
            : Hero());

        // 1  Choose your printer
        var heading = Ui.Columns("*", "Auto", "Auto");
        heading.Children.Add(Ui.At(Ui.SectionLabel(Loc.T("1  Choose your printer"), null), 0));
        _searching = new Spinner(14, Ctx.Animate) { Margin = new Thickness(0, 0, 10, 0) };
        heading.Children.Add(Ui.At(_searching, 1));
        heading.Children.Add(Ui.At(W.Button(Loc.T("Search Again"), () =>
        {
            Firewall.Forget();
            Store.SearchForPrinters();
        }, small: true), 2));
        var choose = new Stack { Spacing = 10, Children = { heading, _notice.Host, _addAll.Host, _list.Host, Manual() } };
        page.Children.Add(choose);

        // 2  Access code
        _entryCard = CodeEntry();
        page.Children.Add(new Stack { Spacing = 10, Children = { Ui.SectionLabel(Loc.T("2  Access code"), null), _code.Host } });

        if (!Adding)
        {
            var signIn = W.Switch(Loc.T("Open at sign-in"), _openAtSignIn, on => _openAtSignIn = on);
            var note = W.Note(Loc.T("At sign-in Bambu Bar starts in the notification area only. Open it from the Start menu or the tray icon to see this window."));
            page.Children.Add(W.Card(new Stack { Spacing = 4, Children = { signIn, note } }, 14));
        }

        // Connect, and why it can't yet.
        _connectLabel = Ui.Text(Loc.T("Connect"), WRamp.Body, FontWeights.SemiBold, ThemeKeys.TextOnAccent);
        _connectSpinner = new Spinner(14, Ctx.Animate) { Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        _connect = new ChromeButton
        {
            Look = ButtonLook.Accent, Height = 32, Width = 160, IsDefault = true,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _connectSpinner, _connectLabel } },
        };
        AutomationProperties.SetName(_connect, Loc.T("Connect"));
        _connect.Click += (_, _) => Connect();
        var row = Ui.Columns("Auto", "*");
        row.Children.Add(Ui.At(_connect, 0));
        _status.Host.Margin = new Thickness(14, 0, 0, 0);
        _status.Host.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(Ui.At(_status.Host, 1));
        page.Children.Add(row);

        if (candidate is not null) Pick(candidate);
        return page;
    }

    public override void Dispose() => _disposed = true;

    // MARK: Refresh

    public override void Refresh()
    {
        var found = NewPrinters();
        var slicerOnly = SlicerOnlyPrinters();
        // A printer the slicer knows: fill in its code when it arrives (the folder is scanned in the background).
        var slicerKey = string.Join("#", Store.SlicerPrinters.Select(p => p.Serial + string.Join(",", p.AccessCodes) + p.Source));
        if (slicerKey != _slicerKey)
        {
            _slicerKey = slicerKey;
            FillCodeFromSlicer();
        }
        if (!_autoPicked && _host.Length == 0 && found.Count == 1)
        {
            _autoPicked = true;
            Pick(found[0]);
        }

        _searching.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var blocked = Store.DiscoveryBlocked;
        _notice.Show($"{blocked}|{found.Count == 0}|{_manual}", () =>
            blocked ? FirewallNotice()
            : found.Count == 0 && !_manual ? Looking()
            : null);

        var ready = Store.ReadySlicerPrinters;
        _addAll.Show(ready.Count >= 2 ? string.Join("#", ready.Select(p => p.Serial + p.Source)) + "|" + string.Join(",", found.Select(f => f.Serial + f.ModelCode)) : "",
            () => ready.Count >= 2 ? AddAllCard(ready) : null);

        var rows = found.Concat(slicerOnly.Select(p => new DiscoveredPrinter
        {
            Serial = p.Serial, Host = p.Host ?? "", Name = p.Serial, ModelCode = "", LastSeen = DateTimeOffset.MinValue,
        })).ToList();
        _list.Show(string.Join("#", rows.Select(p => $"{p.Serial}|{p.Name}|{p.Host}|{p.ModelCode}|{p.ConnectMode}|{p.LastSeen == DateTimeOffset.MinValue}|{Store.SlicerPrinter(p.Serial)?.Source}"))
            + "|" + _selected, () => rows.Count == 0 ? null : new Stack { Spacing = 8 }.With(rows.Select(Row)));

        _code.Show(_codeSource is null ? "entry" : $"found|{_codeSource}", () => _codeSource is { } source ? FoundCode(source) : _entryCard);
        FillImportText();
        FillConnect();
    }

    private void FillConnect()
    {
        var connecting = _serial.Length > 0 && Store.Session(_serial.Trim().ToUpperInvariant()) is { Phase: PrinterConnection.Phase.Connecting };
        _connectLabel.Text = _identifying ? Loc.T("Finding Printer…") : connecting ? Loc.T("Connecting…") : Loc.T("Connect");
        _connectSpinner.Visibility = _identifying || connecting ? Visibility.Visible : Visibility.Collapsed;
        var missing = MissingStep();
        _connect.IsEnabled = !_identifying && !connecting;
        AutomationProperties.SetHelpText(_connect, missing ?? "");

        var detail = _problem ?? (_serial.Length > 0 ? Store.Session(_serial.Trim().ToUpperInvariant())?.LinkDetail : null);
        var hint = detail is null && missing is not null && (_attempted || _codeField.Text.Length > 0) ? missing : null;
        _status.Show($"{detail}|{hint}", () =>
        {
            if (detail is not null) return Status(detail, Icons.Warning, Ctx.Ink(Palette.Paused), ThemeKeys.TextPrimary);
            if (hint is not null) return Status(hint, "", Ctx.Brush(ThemeKeys.TextSecondary), ThemeKeys.TextSecondary);
            return null;
        });
    }

    private static FrameworkElement Status(string text, string glyph, Brush iconBrush, string textKey)
    {
        var grid = Ui.Columns("Auto", "*");
        var icon = Icons.Glyph(glyph, 14, null, iconBrush);
        icon.Margin = new Thickness(0, 1, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(Ui.At(icon, 0));
        var label = Ui.Paragraph(text, WRamp.Detail, textKey);
        grid.Children.Add(Ui.At(label, 1));
        // Read out when it appears (an inline error, what's still missing), without moving the focus.
        return Announce.WhenShown(Ui.ReadAsOne(grid, text));
    }

    // MARK: Parts

    private FrameworkElement Hero()
    {
        var art = ArtImage.Element(150, 180);
        ArtImage.Show(art, ArtImage.Scene(DemoDriver.Sample(), "P2S", lightOn: true, dimmed: false));
        art.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.3 };
        var title = Ui.Text(Loc.T("Welcome to Bambu Bar"), WRamp.PageTitle, FontWeights.SemiBold, display: true);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var text = Ui.Paragraph(Loc.T("Your printer's progress, temperatures and filament, one click away in the tray. Bambu Bar talks to the printer directly over your network, without a Bambu Lab sign-in."), WRamp.Subtitle);
        var grid = Ui.Columns("Auto", "*");
        art.Margin = new Thickness(0, 0, 24, 0);
        grid.Children.Add(Ui.At(art, 0));
        grid.Children.Add(Ui.At(new Stack { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { title, text } }, 1));
        return grid;
    }

    /// <summary>The search can't get in: Windows Firewall keeps the printers' announcements out. Say so, and where to
    /// allow it (Settings has no firewall page; Control Panel's "Allowed apps" is where the switch is).</summary>
    private FrameworkElement FirewallNotice()
    {
        var text = "**" + Loc.T("No local network access") + "**  " +
            Loc.T("Windows Firewall keeps printers on this network from reaching Bambu Bar. If Windows asked, click **Allow**. Otherwise allow **Bambu Bar** for this network under **Allow an app through firewall** (it needs an administrator), or set this network to **Private** in Settings → Network & internet.");
        return W.InfoBar(Palette.Paused, Icons.Warning, text, Ctx, W.Button(Loc.T("Open Firewall Settings"), () =>
        {
            Firewall.Forget();
            if (!Ctx.Services.Offscreen) Firewall.OpenAllowedApps();
        }, small: true));
    }

    private FrameworkElement Looking()
    {
        var grid = Ui.Columns("Auto", "*");
        var icon = Icons.Glyph("", 16, ThemeKeys.TextSecondary);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(Ui.At(icon, 0));
        grid.Children.Add(Ui.At(Ui.Paragraph(Loc.T("Looking for printers on this network. Printers announce themselves every few seconds; make sure yours is on and on the same network as this PC."), WRamp.Detail), 1));
        return W.Card(grid, 12);
    }

    /// <summary>Several printers with saved codes: add them all in one go rather than one at a time.</summary>
    private FrameworkElement AddAllCard(IReadOnlyList<SlicerPrinter> printers)
    {
        var sources = string.Join(", ", printers.Select(p => p.Source).Distinct().Order(StringComparer.CurrentCulture));
        var models = string.Join(" · ", printers
            .GroupBy(p => PrinterModel.Find(ModelCode(p), p.Serial)?.Name ?? Loc.T("Other", "Printers of an unknown model"))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .Select(g => $"{g.Count()} {g.Key}"));
        var grid = Ui.Columns("Auto", "*", "Auto");
        grid.Children.Add(Ui.At(Badge(Icons.AllPrinters), 0));
        var text = new Stack
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text(Loc.P("%lld printers", printers.Count) + " · " + sources, WRamp.Body, FontWeights.SemiBold),
                Ui.Paragraph(models, WRamp.Note),
                Ui.Paragraph(Loc.T("Their access codes are saved there, so they can all be added at once."), WRamp.Note),
            },
        };
        grid.Children.Add(Ui.At(text, 1));
        var add = W.Button(Loc.P("Add All %lld", printers.Count), () =>
        {
            var first = !Adding;
            Store.AddAll(printers);
            if (first) TurnOnSignIn();
            Ctx.Show(AppPage.Printer);
        }, accent: true);
        add.MinWidth = 120;
        add.Margin = new Thickness(12, 0, 0, 0);
        add.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(Ui.At(add, 2));
        return W.Card(grid, 14, Palette.Accent);
    }

    private string ModelCode(SlicerPrinter printer) => Store.Discovered.FirstOrDefault(d => d.Serial == printer.Serial)?.ModelCode ?? "";

    /// <summary>The accent-tinted circle with a glyph that leads the "add all" and "found code" cards.</summary>
    private static Border Badge(string glyph)
    {
        var badge = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Palette.Brush(Palette.WithAlpha(Palette.Accent, 0.15)),
            Child = Icons.Glyph(glyph, 15, null, Palette.Brush(Palette.Accent)),
        };
        return badge;
    }

    /// <summary>A printer to pick: a radio mark, its name, model, address and serial, and badges for a code a slicer
    /// knows and for its connection mode.</summary>
    private FrameworkElement Row(DiscoveredPrinter printer)
    {
        var selected = _selected == printer.Serial;
        var grid = Ui.Columns("Auto", "*", "Auto", "Auto");
        var mark = new RadioMark(selected) { Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(Ui.At(mark, 0));
        grid.Children.Add(Ui.At(new Stack
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text(printer.Name, WRamp.Body, FontWeights.SemiBold),
                Ui.Text(Loc.F("Bambu Lab %@ · %@ · %@", printer.ModelName, printer.Host, printer.Serial), WRamp.Note, null, ThemeKeys.TextSecondary),
            },
        }, 1));
        if (Store.SlicerPrinter(printer.Serial) is { } known)
        {
            var text = printer.LastSeen == DateTimeOffset.MinValue ? Loc.F("From %@", known.Source) : Loc.F("Code in %@", known.Source);
            grid.Children.Add(Ui.At(Chip(text, "", Palette.Accent), 2));
        }
        if (printer.ConnectMode is { } mode)
            grid.Children.Add(Ui.At(Chip(mode.Equals("lan", StringComparison.OrdinalIgnoreCase) ? Loc.T("LAN Only") : Loc.T("Cloud"), null, null), 3));
        var button = new ChromeButton
        {
            Content = grid, Look = selected ? ButtonLook.Tinted(Palette.Accent) : ButtonLook.Card, CornerRadius = Metrics.Card,
            Padding = new Thickness(14, 12, 14, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // The Mac's radio list: one of the printers found, the picked one selected.
            Role = AutomationControlType.RadioButton, Selected = selected,
        };
        AutomationProperties.SetName(button, printer.Name);
        AutomationProperties.SetHelpText(button, Loc.F("Bambu Lab %@ · %@ · %@", printer.ModelName, printer.Host, printer.Serial));
        button.Click += (_, _) =>
        {
            Pick(printer);
            if (_codeSource is null) _connect.Dispatcher.BeginInvoke(() => _codeField.FocusInput());
        };
        return button;
    }

    /// <summary>"Code in Bambu Studio", "LAN Only": a small capsule.</summary>
    private FrameworkElement Chip(string text, string? glyph, Color? tint)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var brush = tint is { } t ? Ctx.Ink(t) : Ctx.Brush(ThemeKeys.TextSecondary);
        if (glyph is not null)
        {
            var icon = Icons.Glyph(glyph, 10, null, brush);
            icon.Margin = new Thickness(0, 0, 5, 0);
            row.Children.Add(icon);
        }
        var label = Ui.Text(text, 11, FontWeights.SemiBold);
        label.Foreground = brush;
        row.Children.Add(label);
        var chip = new Border
        {
            Child = row, CornerRadius = Metrics.Pill, Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Ui.Capsule(chip);
        if (tint is { } c) chip.Background = Palette.Brush(Palette.WithAlpha(c, 0.12));
        else chip.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ControlFill);
        return chip;
    }

    /// <summary>"Enter address manually": an expander with the address, an optional serial and an optional name.</summary>
    private FrameworkElement Manual()
    {
        _manualChevron = Icons.Glyph(Icons.ChevronRight, 10, ThemeKeys.TextSecondary);
        _manualChevron.Margin = new Thickness(0, 1, 8, 0);
        var header = new ChromeButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _manualChevron, Ui.Text(Loc.T("Enter address manually"), WRamp.Detail, FontWeights.SemiBold) } },
            Padding = new Thickness(6, 4, 8, 5), HorizontalAlignment = HorizontalAlignment.Left, Expanded = false,
        };
        _manualHeader = header;
        AutomationProperties.SetName(header, Loc.T("Enter address manually"));
        header.Click += (_, _) => SetManual(!_manual);

        _hostField = Field(Loc.T("IP address"), "192.168.1.50").OnChange(() => Edited(f => _host = f, _hostField));
        _serialField = Field(Loc.T("Serial number"), Loc.T("Optional")).OnChange(() => Edited(f => _serial = f, _serialField));
        _nameField = Field(Loc.T("Name"), Loc.T("Optional")).OnChange(() => Edited(f => _name = f, _nameField));
        _hostField.OnKeys(Connect);
        var body = new Stack
        {
            Spacing = 10, Margin = new Thickness(24, 8, 0, 0),
            Children =
            {
                Labeled(Loc.T("IP address"), _hostField), Labeled(Loc.T("Serial number"), _serialField), Labeled(Loc.T("Name"), _nameField),
                W.Note(Loc.T("Only the IP address is needed: it's on the printer's screen under Settings → Network. Bambu Bar reads the serial number from the printer.")),
            },
        };
        _manualBody = body;
        body.Visibility = Visibility.Collapsed;
        return new Stack { Spacing = 0, Children = { header, body } };
    }

    private void SetManual(bool open)
    {
        _manual = open;
        _manualBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _manualChevron.Text = open ? Icons.ChevronDown : Icons.ChevronRight;
        _manualHeader.Expanded = open;
        Refresh();
    }

    private static TextField Field(string name, string placeholder)
    {
        var field = new TextField(name, width: 280);
        Placeholder(field, placeholder);
        return field;
    }

    /// <summary>Grey text in an empty field (WPF's text boxes have none).</summary>
    private static void Placeholder(TextField field, string text)
    {
        if (field.Child is not Grid grid) return;
        var hint = Ui.Text(text, 14, null, ThemeKeys.TextTertiary);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(10, 0, 10, 1);
        grid.Children.Insert(0, hint);
        void Update() => hint.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        field.OnChange(Update);
        Update();
    }

    private static Grid Labeled(string title, FrameworkElement field)
    {
        var grid = Ui.Columns("130", "Auto", "*");
        grid.Children.Add(Ui.At(Ui.Text(title, WRamp.Detail, null, ThemeKeys.TextSecondary), 0));
        grid.Children.Add(Ui.At(field, 1));
        return grid;
    }

    /// <summary>A manual field changed by typing: what was picked no longer applies.</summary>
    private void Edited(Action<string> set, TextField field)
    {
        set(field.Text);
        if (_filling) return;
        _selected = null;
        _problem = null;
        Refresh();
    }

    /// <summary>The code is already known: say where from, and offer to type one instead.</summary>
    private Border FoundCode(string source)
    {
        var grid = Ui.Columns("Auto", "*", "Auto");
        grid.Children.Add(Ui.At(Badge(""), 0));
        grid.Children.Add(Ui.At(new Stack
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text(Loc.F("Found in %@", source), WRamp.Body, FontWeights.SemiBold),
                Ui.Paragraph(Loc.T("Bambu Bar uses the access code saved there for this printer. It's kept in Credential Manager and only ever sent to the printer."), WRamp.Note),
            },
        }, 1));
        var type = W.Button(Loc.T("Type a Code Instead"), () =>
        {
            _codeSource = null;
            _foundCode = "";
            _alternatives = [];
            _codeField.Text = "";
            Refresh();
            _codeField.FocusInput();
        }, small: true);
        type.Margin = new Thickness(12, 0, 0, 0);
        type.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(Ui.At(type, 2));
        return W.Card(grid, 14, Palette.Accent);
    }

    /// <summary>The typed code, and below it the way to bring the codes of a slicer on another computer: a picked or
    /// dropped <c>BambuStudio.conf</c> or BambuStudio folder.</summary>
    private Border CodeEntry()
    {
        _codeField = new TextField(Loc.T("8-character access code"), secret: true, width: 260);
        Placeholder(_codeField, Loc.T("8-character access code"));
        _codeField.Secret!.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
        _codeField.Secret.MaxLength = 32;
        _codeField.OnChange(() =>
        {
            _problem = null;
            Refresh();
        }).OnKeys(Connect);
        var help = W.Note(Loc.T("On the printer's screen open **Settings → Network** (the Wi-Fi/LAN page) and read the **Access Code**. It's stored in Credential Manager and only ever sent to the printer."));
        help.MaxWidth = 460;
        help.HorizontalAlignment = HorizontalAlignment.Left;

        _importIcon = Icons.Glyph("", 16, ThemeKeys.TextSecondary);
        _importIcon.Margin = new Thickness(0, 0, 12, 0);
        _importTitle = Ui.Text(Loc.T("Bambu Studio on another PC?"), WRamp.Detail, FontWeights.SemiBold);
        _importText = W.Note("");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(W.Button(Loc.T("Choose File…"), ChooseFile, small: true));
        var folder = W.Button(Loc.T("Choose Folder…"), ChooseFolder, small: true);
        folder.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(folder);
        var import = Ui.Columns("Auto", "*", "Auto");
        import.Children.Add(Ui.At(_importIcon, 0));
        import.Children.Add(Ui.At(new Stack { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _importTitle, _importText } }, 1));
        import.Children.Add(Ui.At(buttons, 2));

        var card = W.Card(new Stack { Spacing = 12, Children = { _codeField, help, W.Divider(), import } }, 14);
        _codeField.HorizontalAlignment = HorizontalAlignment.Left;
        // A settings file or folder dropped anywhere on the card.
        card.AllowDrop = true;
        card.DragEnter += (_, e) => Drag(card, e, true);
        card.DragOver += (_, e) => Drag(card, e, true);
        card.DragLeave += (_, e) => Drag(card, e, false);
        card.Drop += (_, e) =>
        {
            Drag(card, e, false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) ImportSettings(paths[0]);
        };
        return card;
    }

    private void Drag(Border card, DragEventArgs e, bool over)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        var lit = over && files;
        Ui.Tint(card, lit ? Palette.Accent : null);
        _importIcon.Foreground = lit ? Ctx.Ink(Palette.Accent) : Ctx.Brush(ThemeKeys.TextSecondary);
    }

    private void FillImportText()
    {
        W.Rich(_importText, _importNote ?? Loc.T("Drop its **BambuStudio.conf** here, or its BambuStudio folder. It's in **AppData → Roaming → BambuStudio** in that user's folder."));
    }

    private void ChooseFile()
    {
        if (Ctx.Services.Offscreen) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Choose Bambu Studio's settings"), Filter = "BambuStudio.conf, OrcaSlicer.conf|*.conf|*.*|*.*",
            CheckFileExists = true, Multiselect = false, InitialDirectory = SuggestedFolder(),
        };
        if (Show(dialog) == true) ImportSettings(dialog.FileName);
    }

    private void ChooseFolder()
    {
        if (Ctx.Services.Offscreen) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Choose the BambuStudio or OrcaSlicer folder. Bambu Bar only reads the printers' access codes from it."),
            Multiselect = false, InitialDirectory = SuggestedFolder(),
        };
        if (Show(dialog) == true) ImportSettings(dialog.FolderName);
    }

    private bool? Show(Microsoft.Win32.CommonDialog dialog) =>
        Window.GetWindow(_connect) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();

    private static string SuggestedFolder()
    {
        try { return Directory.Exists(SlicerImport.SuggestedFolder) ? SlicerImport.SuggestedFolder : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    // MARK: Logic (the Mac's SetupPage)

    /// <summary>Printers on the network that aren't set up yet.</summary>
    private List<DiscoveredPrinter> NewPrinters() => Store.Discovered.Where(d => Store.Session(d.Serial) is null).ToList();

    /// <summary>Printers a slicer knows (with an address) that haven't announced themselves on the network.</summary>
    private List<SlicerPrinter> SlicerOnlyPrinters() => Store.SlicerPrinters
        .Where(p => p.Host is not null && Store.Session(p.Serial) is null && !Store.Discovered.Any(d => d.Serial == p.Serial)).ToList();

    private void ImportSettings(string path)
    {
        var file = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int count;
        try { count = Store.ImportSlicerSettings(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { count = 0; }
        if (count == 0)
        {
            _importNote = Loc.F("No Bambu Studio or OrcaSlicer access codes found in %@.", file);
            Refresh();
            return;
        }
        _importNote = null;
        if (_serial.Trim().Length == 0 && Store.SlicerPrinters.FirstOrDefault() is { } first)
        {
            if (Store.Discovered.FirstOrDefault(d => d.Serial == first.Serial) is { } found) Pick(found);
            else if (first.Host is { } host) Pick(new DiscoveredPrinter { Serial = first.Serial, Host = host, Name = first.Serial, ModelCode = "" });
            else Fill(() => _serialField.Text = _serial = first.Serial);
        }
        FillCodeFromSlicer();
        if (_codeSource is null)
        {
            _importNote = _serial.Trim().Length == 0
                ? Loc.F("%@ doesn't know this printer. Pick another printer or type the code.", file)
                : Loc.F("%@ doesn't know %@. Pick another printer or type the code.", file, _serial.Trim());
        }
        Refresh();
    }

    /// <summary>Fills in the code a slicer has for the chosen printer, never over a typed one.</summary>
    private void FillCodeFromSlicer()
    {
        if (_serial.Trim().Length == 0 || Store.SlicerPrinter(_serial.Trim()) is not { } printer || printer.AccessCodes.Count == 0) return;
        if (_codeSource is null && _codeField.Text.Length > 0) return;
        _foundCode = printer.AccessCodes[0];
        _alternatives = printer.AccessCodes.Skip(1).ToList();
        _codeSource = printer.Source;
    }

    private void Pick(DiscoveredPrinter printer)
    {
        Fill(() =>
        {
            _hostField.Text = _host = printer.Host;
            _serialField.Text = _serial = printer.Serial;
            _nameField.Text = _name = printer.Name;
        });
        _modelCode = printer.ModelCode;
        if (_codeSource is not null)
        {
            // A code that came with another printer doesn't belong to this one.
            _codeSource = null;
            _foundCode = "";
            _alternatives = [];
        }
        FillCodeFromSlicer();
        _selected = printer.Serial;
        _problem = null;
        Refresh();
    }

    private void Fill(Action fill)
    {
        _filling = true;
        try { fill(); }
        finally { _filling = false; }
    }

    private string Code => (_codeSource is not null ? _foundCode : _codeField.Text).Trim();

    /// <summary>What Connect still needs. Not the serial: left blank, it's read from the printer.</summary>
    private string? MissingStep()
    {
        if (_host.Trim().Length == 0)
        {
            return NewPrinters().Count == 0 && SlicerOnlyPrinters().Count == 0
                ? Loc.T("Bambu Bar hasn't found your printer yet. Type its IP address under “Enter address manually”.")
                : Loc.T("Choose your printer above, or enter its address manually.");
        }
        return Code.Length < 4 ? Loc.T("Enter the printer's access code.") : null;
    }

    private void Connect()
    {
        var connecting = _serial.Length > 0 && Store.Session(_serial.Trim().ToUpperInvariant()) is { Phase: PrinterConnection.Phase.Connecting };
        if (MissingStep() is not null || _identifying || connecting)
        {
            _attempted = true;
            if (_host.Trim().Length == 0 && NewPrinters().Count == 0 && SlicerOnlyPrinters().Count == 0 && !_manual) SetManual(true);
            else Refresh();
            if (_host.Trim().Length == 0 && _manual) _hostField.FocusInput();
            else if (_host.Trim().Length > 0 && _codeSource is null) _codeField.FocusInput();
            return;
        }
        _problem = null;
        var address = _host.Trim();
        if (_serial.Trim().Length > 0)
        {
            Finish();
        }
        else if (Store.Discovered.FirstOrDefault(d => d.Host == address) is { } seen)
        {
            if (Adopt(seen.Serial)) Finish();
        }
        else
        {
            _ = Identify(address);
        }
    }

    private async Task Identify(string address)
    {
        _identifying = true;
        Refresh();
        var (serial, failure) = await Store.IdentifyPrinterAsync(address);
        _identifying = false;
        // The person may have moved on to another address meanwhile, or left the page.
        if (_disposed || _host.Trim() != address)
        {
            if (!_disposed) Refresh();
            return;
        }
        if (serial is not null)
        {
            if (Adopt(serial)) Finish();
            else Refresh();
        }
        else
        {
            _problem = failure?.Message ?? Loc.T("Nothing answered at that address. Check the IP address on the printer's screen (Settings → Network).");
            Refresh();
        }
    }

    /// <summary>Takes the serial found for a typed address. A code that came with the printer picked before belongs to
    /// that one: swap in this printer's code from the slicer, or ask for it. False when the code is now missing.</summary>
    private bool Adopt(string found)
    {
        Fill(() => _serialField.Text = _serial = found);
        if (_codeSource is not null && Store.SlicerPrinter(found)?.AccessCodes.FirstOrDefault() != _foundCode)
        {
            _codeSource = null;
            _foundCode = "";
            _alternatives = [];
            FillCodeFromSlicer();
        }
        if (Code.Length >= 4) return true;
        _attempted = true;
        Refresh();
        _codeField.FocusInput();
        return false;
    }

    private void Finish()
    {
        var serial = _serial.Trim().ToUpperInvariant();
        var seen = Store.Discovered.FirstOrDefault(d => d.Serial == serial);
        var typedName = _name.Trim();
        var config = new PrinterConfig
        {
            Host = _host.Trim(), Serial = serial,
            Name = typedName.Length == 0 ? Store.AnnouncedName(serial) ?? serial : typedName,
            // Typed in by hand, the serial's prefix tells the model better than the default.
            ModelCode = _selected == serial ? _modelCode : seen?.ModelCode ?? "",
        };
        var first = !Adding;
        Store.Connect(config, Code, _alternatives);
        if (first) TurnOnSignIn();
        Ctx.Show(AppPage.Printer);
    }

    /// <summary>The first printer set up with "Open at sign-in" ticked: Bambu Bar starts at sign-in from now on.</summary>
    private void TurnOnSignIn()
    {
        if (!_openAtSignIn || Ctx.Services.Offscreen || Store.IsDemo) return;
        try
        {
            if (!Registration.OpensAtSignIn) Registration.OpensAtSignIn = true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            // Settings → General shows the switch off; the person can try there.
        }
    }

    /// <summary>Windows 11's radio mark: a ring, or a thick accent ring around a white dot when picked.</summary>
    private sealed class RadioMark : FrameworkElement
    {
        private readonly bool _on;

        public RadioMark(bool on)
        {
            _on = on;
            Width = Height = 20;
            VerticalAlignment = VerticalAlignment.Center;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = new Point(10, 10);
            if (_on)
            {
                dc.DrawEllipse(Find(ThemeKeys.AccentFill), null, c, 10, 10);
                dc.DrawEllipse(Find(ThemeKeys.TextOnAccent), null, c, 4, 4);
            }
            else
            {
                dc.DrawEllipse(Find(WindowKeys.OffFill), new Pen(Find(WindowKeys.StrongStroke), 1), c, 9.5, 9.5);
            }
        }

        private Brush Find(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}

internal static class StackExtensions
{
    /// <summary>Adds the elements and returns the stack, for building lists inline.</summary>
    public static Stack With(this Stack stack, IEnumerable<UIElement> children)
    {
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
}
