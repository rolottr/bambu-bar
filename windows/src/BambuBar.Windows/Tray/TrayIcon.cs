using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.AppWindow;
using BambuBar.Core.Model;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BambuBar.Tray;

/// <summary>
/// The notification-area icon (ledger D2): the glyph for the tray's printer, the full line in the tooltip
/// ("P2S · 64% · 1h 02m · done 14:32"), left click opens the panel, right click the menu (P3.4 fills it in).
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly PrinterStore _store;
    private readonly Forms.NotifyIcon _icon = new() { Visible = false };
    private System.Drawing.Icon? _current;
    private string _lastKey = "";
    private bool _disposed;

    public event Action? OpenPanel;
    public event Action? OpenWindow;
    /// <summary>"Show Camera" (the Mac's Printer menu, ⌘K): only while the selected printer's camera is on.</summary>
    public event Action<PrinterSession>? ShowCamera;
    public event Action? Quit;
    /// <summary>Opens the window at a page (Widgets, Settings).</summary>
    public event Action<AppPage>? OpenPage;
    /// <summary>Set while the updater can check (supporter license active).</summary>
    public Action? CheckForUpdates { get; set; }

    /// <summary>Draw the percentage instead of the printer while printing (Tray page, P3.5).</summary>
    public bool ShowPercent { get; set; }

    /// <param name="visible">False for the snapshot's picture of the menu: no icon in the tray.</param>
    public TrayIcon(PrinterStore store, bool visible = true)
    {
        _store = store;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenPanel?.Invoke();
        };
        // The menu (WinForms' first ContextMenuStrip: 200 ms on a slow machine) right after launch, not before the icon.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (!_disposed) EnsureMenu();
        });
        store.PropertyChanged += OnStoreChanged;
        foreach (var session in store.Sessions) session.PropertyChanged += OnStoreChanged;
        ((System.Collections.Specialized.INotifyCollectionChanged)store.Sessions).CollectionChanged += OnSessionsChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Update();
        _icon.Visible = visible;
    }

    /// <summary>The menu as it would open now, drawn to a bitmap (the snapshot's tray-menu-*.png).</summary>
    internal static System.Drawing.Bitmap MenuPicture(PrinterStore store, bool dark)
    {
        using var tray = new TrayIcon(store, visible: false);
        var menu = tray.EnsureMenu();
        tray.Fill(menu, dark);
        menu.CreateControl();
        menu.PerformLayout();
        var size = menu.GetPreferredSize(System.Drawing.Size.Empty);
        menu.Size = size;
        var bitmap = new System.Drawing.Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
        return bitmap;
    }

    /// <summary>The right-click menu (ledger P3.4), rebuilt each time it opens so it shows the printer as it is:
    /// Open Bambu Bar; the printers when there are several; Pause or Resume and Stop… while printing (with the Tray
    /// page's "Pause, stop and speed" on); the chamber light; Show Camera; Refresh, Widgets and Settings; Quit.
    /// Each item acts once the menu has closed (posted to the dispatcher): Quit disposes the icon and this menu, and
    /// doing that inside the item's own click left WinForms' menu code running on a disposed menu.</summary>
    private Forms.ContextMenuStrip EnsureMenu() => _icon.ContextMenuStrip ??= Menu();

    private Forms.ContextMenuStrip Menu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            Fill(menu);
            e.Cancel = false;
        };
        Fill(menu);
        return menu;
    }

    private void Fill(Forms.ContextMenuStrip menu, bool? darkMenu = null)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        EventHandler Later(Action action) => (_, _) => dispatcher.BeginInvoke(action);
        Forms.ToolStripMenuItem Item(string text, Action action) => new(text, null, Later(action));
        var items = menu.Items;
        items.Clear();

        var dark = darkMenu ?? !TaskbarIsLight();
        MenuRenderer.Apply(menu, dark);

        var open = Item(Loc.T("Open Bambu Bar"), () => OpenWindow?.Invoke());
        // The default action (a left click opens the panel; this opens the window), bold as Windows marks it.
        open.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        items.Add(open);

        var sessions = _store.Sessions;
        var current = _store.Current;
        if (sessions.Count > 1)
        {
            var printers = new Forms.ToolStripMenuItem(Loc.T("Printers"));
            foreach (var session in sessions)
            {
                var id = session.Id;
                var row = Item($"{session.Config.Name}  ·  {session.ShortState}", () => _store.Select(id));
                row.Checked = session.Id == current?.Id;
                printers.DropDownItems.Add(row);
            }
            items.Add(printers);
        }

        var status = current is { IsOnline: true } ? current.Status : null;
        var printItems = new List<Forms.ToolStripItem>();
        if (status is not null && _store.ShowPrinterActions && status.State.IsActive())
        {
            printItems.Add(status.State == JobState.Paused
                ? Item(Loc.T("Resume"), _store.Resume)
                : Item(Loc.T("Pause"), _store.Pause));
            printItems.Add(Item(Loc.T("Stop…"), _store.StopPrint));
        }
        if (status?.ChamberLightOn is { } lightOn)
            printItems.Add(new Forms.ToolStripMenuItem(Loc.T("Chamber light"), null, Later(_store.ToggleLight)) { Checked = lightOn });
        if (current is { ShowsCamera: true } withCamera)
            printItems.Add(new Forms.ToolStripMenuItem(Loc.T("Show Camera"), null, Later(() => ShowCamera?.Invoke(withCamera))) { ShortcutKeyDisplayString = "Ctrl+K" });
        if (printItems.Count > 0)
        {
            items.Add(new Forms.ToolStripSeparator());
            foreach (var item in printItems) items.Add(item);
        }

        items.Add(new Forms.ToolStripSeparator());
        if (_store.IsConfigured) items.Add(Item(Loc.T("Refresh"), _store.Refresh));
        items.Add(Item(Loc.T("Widgets"), () => OpenPage?.Invoke(AppPage.Widgets)));
        items.Add(Item(Loc.T("Settings…"), () => OpenPage?.Invoke(AppPage.Settings)));
        if (CheckForUpdates is not null) items.Add(Item(Loc.T("Check for Updates…"), () => CheckForUpdates?.Invoke()));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(Item(Loc.T("Quit"), () => Quit?.Invoke()));
        MenuRenderer.Dress(items, dark);
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void OnSessionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (PrinterSession s in e.NewItems ?? Array.Empty<PrinterSession>()) s.PropertyChanged += OnStoreChanged;
        foreach (PrinterSession s in e.OldItems ?? Array.Empty<PrinterSession>()) s.PropertyChanged -= OnStoreChanged;
        Update();
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        _lastKey = "";
        Update();
    }

    private void Update()
    {
        // Gone: quitting stops the printers after the icon, and their last changes still come in.
        if (_disposed) return;
        var session = _store.MenuBarSession;
        var look = TrayGlyph.For(session, _store.IsConfigured, ShowPercent);
        var light = TaskbarIsLight();
        var pixels = Forms.SystemInformation.SmallIconSize.Width;
        var tooltip = Text(session);
        var key = $"{look}|{light}|{pixels}|{tooltip}";
        if (key == _lastKey) return;
        _lastKey = key;
        var previous = _current;
        _current = ToIcon(TrayGlyph.Render(look, pixels, light));
        _icon.Icon = _current;
        _icon.Text = tooltip;
        previous?.Dispose();
    }

    /// <summary>The icon's text: "Bambu Bar" over the tooltip line, as other tray icons say whose they are (OneDrive's).
    /// It's also the icon's name for screen readers, which read the tray by its tooltips: "Bambu Bar, P2S Workshop ·
    /// 64% · 1h 02m", like the Mac's VoiceOver label. NotifyIcon allows 127 characters.</summary>
    public static string Text(PrinterSession? session)
    {
        var line = Tooltip(session);
        var text = line.StartsWith("Bambu Bar", StringComparison.Ordinal) ? line : "Bambu Bar\n" + line;
        return text.Length > 127 ? text[..126] + "…" : text;
    }

    /// <summary>"P2S Workshop · 64% · 1h 02m · done 14:32", or the link state.</summary>
    public static string Tooltip(PrinterSession? session)
    {
        if (session is null) return "Bambu Bar · " + Loc.T("No printer set up");
        if (session.Status is not { } s || !session.IsOnline) return $"{session.Config.Name} · {session.LinkSummary}";
        var parts = new List<string> { session.Config.Name };
        if (s.State.IsActive())
        {
            parts.Add(s.State == JobState.Paused ? s.State.Title() : $"{s.Progress}%");
            if (s.RemainingMinutes > 0 && s.State != JobState.Paused) parts.Add(Format.Duration(s.RemainingMinutes));
            if (s.State == JobState.Printing && s.EstimatedEnd is { } end) parts.Add(Loc.F("Done %@", Format.Finish(end)));
        }
        else parts.Add(s.StateTitle);
        return string.Join(" · ", parts);
    }

    /// <summary>The taskbar follows "Choose your default Windows mode", not the apps' mode.</summary>
    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    private static System.Drawing.Icon ToIcon(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(png);
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.PropertyChanged -= OnStoreChanged;
        foreach (var session in _store.Sessions) session.PropertyChanged -= OnStoreChanged;
        ((System.Collections.Specialized.INotifyCollectionChanged)_store.Sessions).CollectionChanged -= OnSessionsChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _icon.Visible = false;
        var menu = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = null;
        _icon.Dispose();
        menu?.Dispose();
        _current?.Dispose();
    }
}
