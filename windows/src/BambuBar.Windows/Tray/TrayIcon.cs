using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using BambuBar.Core;
using BambuBar.Core.App;
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

    /// <summary>Draw the percentage instead of the printer while printing (Tray page, P3.5).</summary>
    public bool ShowPercent { get; set; }

    public TrayIcon(PrinterStore store)
    {
        _store = store;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenPanel?.Invoke();
        };
        _icon.ContextMenuStrip = Menu();
        store.PropertyChanged += OnStoreChanged;
        foreach (var session in store.Sessions) session.PropertyChanged += OnStoreChanged;
        ((System.Collections.Specialized.INotifyCollectionChanged)store.Sessions).CollectionChanged += OnSessionsChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Update();
        _icon.Visible = true;
    }

    /// <summary>The right-click menu. Each item acts once the menu has closed (posted to the dispatcher): Quit
    /// disposes the icon and this menu, and doing that inside the item's own click left WinForms' menu code running on
    /// a disposed menu (a NullReferenceException in WinForms' error dialog).</summary>
    private Forms.ContextMenuStrip Menu()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        EventHandler Later(Action action) => (_, _) => dispatcher.BeginInvoke(action);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Open Bambu Bar"), null, Later(() => OpenWindow?.Invoke()));
        menu.Items.Add(Loc.T("Refresh"), null, Later(() => _store.Refresh()));
        var camera = new Forms.ToolStripMenuItem(Loc.T("Show Camera"), null, Later(() =>
        {
            if (_store.Current is { ShowsCamera: true } session) ShowCamera?.Invoke(session);
        })) { ShortcutKeyDisplayString = "Ctrl+K" };
        menu.Items.Add(camera);
        menu.Opening += (_, _) => camera.Visible = _store.Current?.ShowsCamera == true;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Quit"), null, Later(() => Quit?.Invoke()));
        return menu;
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
