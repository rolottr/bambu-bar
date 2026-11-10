using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Camera;
using BambuBar.Core.Model;
using BambuBar.Theme;

namespace BambuBar.AppWindow;

/// <summary>
/// Printer management and connection settings (the Mac's PrintersPage): every printer with its state, then the
/// selected one's name (with the announced name to fall back on), model, serial, status, address and access code,
/// camera, and Forget; and the stop confirmation switch.
/// </summary>
internal sealed class PrintersPage(WindowContext ctx) : Page(ctx)
{
    private readonly Stack _rows = new() { Spacing = 12 };
    private readonly Dictionary<string, PrinterRow> _rowFor = [];
    private TextBlock _listNote = null!;
    private Detail? _detail;

    public override FrameworkElement Build()
    {
        var list = W.Group(Loc.T("Printers"), "", out var listBody);
        listBody.Children.Add(_rows);
        _listNote = W.Note("");
        _listNote.VerticalAlignment = VerticalAlignment.Center;
        var footer = Ui.Columns("*", "Auto");
        footer.Children.Add(Ui.At(_listNote, 0));
        var add = W.Button(Loc.T("Add Printer…"), () => Ctx.AddPrinter(null));
        add.Margin = new Thickness(12, 0, 0, 0);
        footer.Children.Add(Ui.At(add, 1));
        listBody.Children.Add(footer);

        var page = new Stack { Spacing = 22, Children = { W.PageHeader(Loc.T("Printers"), null), list } };
        if (Store.Current is { } current)
        {
            _detail = new Detail(Ctx, current);
            page.Children.Add(_detail.Root);
        }
        var controls = W.Group(Loc.T("Controls"), "", out var controlsBody);
        controlsBody.Children.Add(W.Switch(Loc.T("Ask before stopping a print"), Store.ConfirmStop, on => Store.ConfirmStop = on));
        controlsBody.Children.Add(W.Note(Loc.T("Pause, stop and speed changes need **LAN Only** mode with **Developer Mode** on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.")));
        page.Children.Add(controls);
        return page;
    }

    public override void Refresh()
    {
        _listNote.Text = Store.IsConfigured ? Loc.T("Bambu Bar watches all of them at once.") : Loc.T("No printer set up yet.");
        var sessions = Store.Sessions.ToList();
        foreach (var gone in _rowFor.Keys.Except(sessions.Select(s => s.Id)).ToList()) _rowFor.Remove(gone);
        var changed = _rows.Children.Count != sessions.Count * 2;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (!_rowFor.TryGetValue(sessions[i].Id, out var row))
            {
                row = new PrinterRow(Ctx, sessions[i]);
                _rowFor[sessions[i].Id] = row;
                changed = true;
            }
            row.Update(sessions[i]);
        }
        if (changed)
        {
            _rows.Children.Clear();
            foreach (var session in sessions)
            {
                _rows.Children.Add(_rowFor[session.Id]);
                _rows.Children.Add(W.Divider());
            }
        }
        _detail?.Refresh();
    }

    /// <summary>One printer in the list: drawing, name (with the tray mark), model and address, state, Show.</summary>
    private sealed class PrinterRow : Grid
    {
        private readonly WindowContext _ctx;
        private readonly Image _art = ArtImage.Element(28, 34);
        private readonly TextBlock _name = Ui.Text("", WRamp.Body, FontWeights.SemiBold);
        private readonly FrameworkElement _trayMark;
        private readonly TextBlock _detail = Ui.Text("", WRamp.Note, null, ThemeKeys.TextSecondary);
        private readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
        private readonly TextBlock _state = Ui.Text("", WRamp.Note, null, ThemeKeys.TextSecondary);
        private readonly ChromeButton _show;
        private readonly TextBlock _showText;

        public PrinterRow(WindowContext ctx, PrinterSession session)
        {
            _ctx = ctx;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _art.Margin = new Thickness(0, 0, 12, 0);
            Children.Add(Ui.At(_art, 0));
            var mark = Icons.Glyph(AppPage.Tray.Glyph(), 11);
            mark.Margin = new Thickness(6, 1, 0, 0);
            Ui.Tip(mark, Loc.T("Shown in the tray"));
            _trayMark = mark;
            var title = new StackPanel { Orientation = Orientation.Horizontal, Children = { _name, mark } };
            Children.Add(Ui.At(new Stack { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, _detail } }, 1));
            var state = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { _dot, _state } };
            Children.Add(Ui.At(state, 2));
            _show = W.Button(Loc.T("Show"), () => ctx.Store.Select(session.Id), small: true);
            _showText = (TextBlock)_show.Content;
            _show.Margin = new Thickness(12, 0, 0, 0);
            _show.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(Ui.At(_show, 3));
        }

        public void Update(PrinterSession session)
        {
            var store = _ctx.Store;
            ArtImage.Show(_art, ArtImage.Scene(session.Status, session.Config.ModelName, session.ChamberLightOn, dimmed: !session.IsOnline));
            _name.Text = session.Config.Name;
            _trayMark.Visibility = store.HasMultiplePrinters && session.Id == store.MenuBarSession?.Id ? Visibility.Visible : Visibility.Collapsed;
            _detail.Text = Loc.F("Bambu Lab %@ · %@", session.Config.ModelName, session.Config.Host);
            _dot.Fill = Palette.Brush(WindowView.Dot(session));
            _state.Text = session.ShortState;
            var current = session.Id == store.Current?.Id;
            _show.Visibility = store.HasMultiplePrinters ? Visibility.Visible : Visibility.Collapsed;
            _showText.Text = current ? Loc.T("Showing") : Loc.T("Show");
            _show.IsEnabled = !current;
            AutomationProperties.SetName(_show, $"{_showText.Text} {session.Config.Name}");
            AutomationProperties.SetName(this, session.Config.Name);
            AutomationProperties.SetItemStatus(this, session.ShortState);
        }

        /// <summary>A group named after the printer, its Show button inside.</summary>
        protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
    }

    /// <summary>The selected printer's settings. Built once per printer, so what's being typed survives the reports.</summary>
    private sealed class Detail
    {
        private readonly WindowContext _ctx;
        private readonly string _serial;
        private readonly Stack _body;
        private readonly TextBlock _groupTitle;
        private readonly TextField _name = new(Loc.T("Name"), width: 220);
        private readonly ChromeButton _revert;
        private readonly ChromeButton _saveName;
        private readonly Region _announced = new();
        private readonly Ellipse _dot = new() { Width = 7, Height = 7, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = Ui.Text("", WRamp.Body, null, ThemeKeys.TextSecondary);
        private readonly TextBlock _host = Ui.Text("", WRamp.Body, null, ThemeKeys.TextSecondary);
        private readonly FrameworkElement _hostRow;
        private readonly FrameworkElement _connectionEditor;
        private readonly TextField _hostField = new(Loc.T("IP address"), width: 220);
        private readonly TextField _codeField = new(Loc.T("Access code"), secret: true, width: 220);
        private readonly ChromeButton _reconnect;
        private readonly ToggleSwitch _camera;
        private readonly Region _cameraWarning = new();
        private string _savedName = "";

        public FrameworkElement Root { get; }

        public Detail(WindowContext ctx, PrinterSession session)
        {
            _ctx = ctx;
            _serial = session.Id;
            var config = session.Config;
            var group = W.Group("", AppPage.Printer.Glyph(), out _body);
            _groupTitle = (TextBlock)((StackPanel)group.Children[0]).Children[1];
            Root = group;

            _revert = W.Button(Loc.T("Revert"), () => _name.Text = Config.Name, small: true);
            _saveName = W.Button(Loc.T("Save"), SaveName, accent: true, small: true);
            _name.OnChange(UpdateName).OnKeys(SaveName, () => _name.Text = Config.Name);
            var nameControls = new Stack { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _revert, _saveName, _name } };
            _body.Children.Add(W.Row(Loc.T("Name"), nameControls));
            _body.Children.Add(_announced.Host);
            _body.Children.Add(W.Row(Loc.T("Model"), Ui.Text(Loc.F("Bambu Lab %@", config.ModelName), WRamp.Body, null, ThemeKeys.TextSecondary)));
            _body.Children.Add(W.Row(Loc.T("Serial number"), Selectable(config.Serial, Loc.T("Serial number"))));
            _body.Children.Add(W.Row(Loc.T("Status"), new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, _status } }));

            var change = W.Button(Loc.T("Change…"), EditConnection, small: true);
            change.Margin = new Thickness(10, 0, 0, 0);
            _hostRow = W.Row(Loc.T("IP address"), new StackPanel { Orientation = Orientation.Horizontal, Children = { _host, change } });
            _host.VerticalAlignment = VerticalAlignment.Center;
            _body.Children.Add(_hostRow);
            _hostField.OnChange(UpdateReconnect).OnKeys(ApplyConnection, CancelConnection);
            _codeField.OnChange(UpdateReconnect).OnKeys(ApplyConnection, CancelConnection);
            _reconnect = W.Button(Loc.T("Reconnect"), ApplyConnection, accent: true);
            var buttons = new Stack { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { W.Button(Loc.T("Cancel"), CancelConnection), _reconnect } };
            _connectionEditor = new Stack
            {
                Spacing = 14, Visibility = Visibility.Collapsed,
                Children = { W.Row(Loc.T("IP address"), _hostField), W.Row(Loc.T("Access code"), _codeField), buttons },
            };
            _body.Children.Add(_connectionEditor);

            _body.Children.Add(W.Switch(Loc.T("Camera"), config.CameraEnabled == true, on => _ctx.Store.SetCameraEnabled(on, _serial), out _camera));
            _body.Children.Add(W.Note(CameraTransports.ForModel(config.ModelName) == CameraTransport.Rtsps
                ? Loc.T("Shows the printer's live view. It needs **LAN Only Liveview** turned on on the printer (Settings → Network on its screen).")
                : Loc.T("Shows the printer's live view.")));
            _body.Children.Add(_cameraWarning.Host);
            _body.Children.Add(W.Divider());
            var forgetRow = Ui.Columns("*", "Auto");
            var follows = W.Note(Loc.T("Bambu Bar follows the printer if its IP address changes."));
            follows.VerticalAlignment = VerticalAlignment.Center;
            forgetRow.Children.Add(Ui.At(follows, 0));
            var forget = W.Button(Loc.T("Forget Printer…"), Forget);
            forget.Look = ButtonLook.Tinted(Palette.Failed);
            ((TextBlock)forget.Content).Foreground = _ctx.Ink(Palette.Failed);
            forget.Margin = new Thickness(12, 0, 0, 0);
            forgetRow.Children.Add(Ui.At(forget, 1));
            _body.Children.Add(forgetRow);
            _savedName = config.Name;
            _name.Text = config.Name;
        }

        private PrinterConfig Config => _ctx.Store.Session(_serial)?.Config ?? new PrinterConfig { Host = "", Serial = _serial, Name = _serial };

        private static TextBox Selectable(string text, string name)
        {
            var box = new TextBox
            {
                Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0), FontSize = WRamp.Body,
            };
            box.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextSecondary);
            box.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontText);
            AutomationProperties.SetName(box, name);
            return box;
        }

        public void Refresh()
        {
            if (_ctx.Store.Session(_serial) is not { } session) return;
            var config = session.Config;
            _groupTitle.Text = _ctx.Store.HasMultiplePrinters ? config.Name : Loc.T("Printer");
            // A rename from elsewhere (the Printer page) replaces what's in the field.
            if (config.Name != _savedName)
            {
                _savedName = config.Name;
                _name.Text = config.Name;
            }
            UpdateName();
            _dot.Fill = Palette.Brush(session.IsOnline ? Palette.Accent : Palette.Paused);
            _status.Text = session.LinkSummary;
            _host.Text = config.Host;
            if (_camera.IsChecked != (config.CameraEnabled == true)) _camera.IsChecked = config.CameraEnabled == true;
            var warn = config.CameraEnabled == true && !_ctx.Store.CameraEnabled;
            _cameraWarning.Show(warn.ToString(), () =>
            {
                if (!warn) return null;
                var row = Ui.Columns("*", "Auto");
                var note = W.Note(Loc.T("The camera is off for every printer in Settings."));
                note.Foreground = _ctx.Ink(Palette.Paused);
                note.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(Ui.At(note, 0));
                var open = W.Button(Loc.T("Open Settings"), () => _ctx.Show(AppPage.Settings), small: true);
                open.Margin = new Thickness(12, 0, 0, 0);
                row.Children.Add(Ui.At(open, 1));
                return row;
            });
        }

        private void UpdateName()
        {
            var config = Config;
            var dirty = _name.Text != config.Name;
            _revert.Visibility = _saveName.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            _saveName.IsEnabled = _name.Text.Trim().Length > 0;
            var announced = _ctx.Store.AnnouncedName(_serial);
            _announced.Show($"{announced}|{config.Name}", () =>
            {
                if (announced is null || announced == config.Name) return null;
                var row = Ui.Columns("*", "Auto");
                var note = W.Note(Loc.F("The printer calls itself “%@”. This name is only used in Bambu Bar.", announced));
                note.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(Ui.At(note, 0));
                var use = W.Button(Loc.F("Use “%@”", announced), () => _ctx.Store.Rename(_serial, announced), small: true);
                use.Margin = new Thickness(12, 0, 0, 0);
                row.Children.Add(Ui.At(use, 1));
                return row;
            });
        }

        private void SaveName()
        {
            if (_name.Text.Trim().Length == 0) return;
            _ctx.Store.Rename(_serial, _name.Text);
        }

        private void EditConnection()
        {
            _hostField.Text = Config.Host;
            _codeField.Text = _ctx.Store.Session(_serial)?.AccessCode ?? "";
            _hostRow.Visibility = Visibility.Collapsed;
            _connectionEditor.Visibility = Visibility.Visible;
            UpdateReconnect();
            _hostField.FocusInput();
        }

        private void CancelConnection()
        {
            _connectionEditor.Visibility = Visibility.Collapsed;
            _hostRow.Visibility = Visibility.Visible;
        }

        private void UpdateReconnect() => _reconnect.IsEnabled = _hostField.Text.Trim().Length > 0 && _codeField.Text.Length >= 4;

        private void ApplyConnection()
        {
            var host = _hostField.Text.Trim();
            var code = _codeField.Text;
            if (host.Length == 0 || code.Length < 4 || _ctx.Store.Session(_serial) is null) return;
            _ctx.Store.Connect(Config with { Host = host }, code);
            CancelConnection();
        }

        private void Forget()
        {
            if (_ctx.Store.Session(_serial) is not { } session) return;
            if (!Dialogs.Confirm(Root, _ctx, Loc.F("Forget %@?", session.Config.Name),
                Loc.T("Bambu Bar disconnects and removes the access code from Credential Manager. Print history stays."),
                Loc.T("Forget"), Loc.T("Cancel"))) return;
            _ctx.Store.Forget(_serial);
        }
    }
}
