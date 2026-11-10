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

    public event Action? OpenPanel;
    public event Action? OpenWindow;
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
        ((System.Collections.Specialized.INotifyCollectionChanged)store.Sessions).CollectionChanged += (_, e) =>
        {
            foreach (PrinterSession s in e.NewItems ?? Array.Empty<PrinterSession>()) s.PropertyChanged += OnStoreChanged;
            Update();
        };
        SystemEvents.UserPreferenceChanged += (_, _) => { _lastKey = ""; Update(); };
        Update();
        _icon.Visible = true;
    }

    private Forms.ContextMenuStrip Menu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Open Bambu Bar"), null, (_, _) => OpenWindow?.Invoke());
        menu.Items.Add(Loc.T("Refresh"), null, (_, _) => _store.Refresh());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Quit"), null, (_, _) => Quit?.Invoke());
        return menu;
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var session = _store.MenuBarSession;
        var look = TrayGlyph.For(session, _store.IsConfigured, ShowPercent);
        var light = TaskbarIsLight();
        var pixels = Forms.SystemInformation.SmallIconSize.Width;
        var tooltip = Tooltip(session);
        var key = $"{look}|{light}|{pixels}|{tooltip}";
        if (key == _lastKey) return;
        _lastKey = key;
        var previous = _current;
        _current = ToIcon(TrayGlyph.Render(look, pixels, light));
        _icon.Icon = _current;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
        previous?.Dispose();
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
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
