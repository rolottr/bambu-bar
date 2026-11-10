using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Core.Protocol;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>The window's pages, in the sidebar's order (the Mac's AppSection).</summary>
public enum AppPage { Fleet, Printer, Filament, Tray, Notifications, Printers, Widgets, Settings, About }

public static class AppPages
{
    public static readonly AppPage[] Order =
        [AppPage.Fleet, AppPage.Printer, AppPage.Filament, AppPage.Tray, AppPage.Notifications, AppPage.Printers, AppPage.Widgets, AppPage.Settings, AppPage.About];

    public static string Title(this AppPage page) => page switch
    {
        AppPage.Fleet => Loc.T("All Printers"),
        AppPage.Printer => Loc.T("Printer"),
        AppPage.Filament => Loc.T("Filament"),
        AppPage.Tray => Loc.T("Tray", "Sidebar page: the tray icon and its panel (the Mac's Menu Bar page)"),
        AppPage.Notifications => Loc.T("Notifications"),
        AppPage.Printers => Loc.T("Printers"),
        AppPage.Widgets => Loc.T("Widgets"),
        AppPage.Settings => Loc.T("Settings"),
        _ => Loc.T("About"),
    };

    /// <summary>Ctrl + this digit opens the page; the same digits as the Mac's ⌘ shortcuts, which stay stable as pages
    /// are added.</summary>
    public static char Shortcut(this AppPage page) => page switch
    {
        AppPage.Fleet => '0',
        AppPage.Printer => '1',
        AppPage.Filament => '2',
        AppPage.Widgets => '3',
        AppPage.Settings => '4',
        AppPage.About => '5',
        AppPage.Printers => '6',
        AppPage.Tray => '7',
        _ => '8',
    };

    /// <summary>Segoe Fluent Icons for the Mac's SF Symbols.</summary>
    public static string Glyph(this AppPage page) => page switch
    {
        AppPage.Fleet => "",          // ViewAll (square.grid.2x2)
        AppPage.Printer => "",        // Print (printer.fill)
        AppPage.Filament => "",       // CircleRing (circle.circle)
        AppPage.Tray => "",           // TVMonitor: the screen with its taskbar (menubar.rectangle)
        AppPage.Notifications => "",  // Ringer (bell.badge)
        AppPage.Printers => "",       // BulletedList (printer.dotmatrix)
        AppPage.Widgets => "",        // Tiles (widget.medium)
        AppPage.Settings => "",       // Setting (gearshape)
        _ => "",                      // Info (info.circle)
    };
}

/// <summary>What the app hands the window beyond the store: the license, and the few things only the app can do.</summary>
public sealed class WindowServices
{
    /// <summary>The supporter license; null in runs without one (snapshots), which then read as "free".</summary>
    public LicenseStore? License { get; init; }

    /// <summary>Quits and starts again (a new language).</summary>
    public Action? Restart { get; init; }

    /// <summary>A tray setting changed (the percentage in the icon): the tray icon redraws.</summary>
    public Action? TrayChanged { get; init; }

    /// <summary>Snapshot runs: nothing reads the registry or opens anything.</summary>
    public bool Offscreen { get; init; }

    /// <summary>The desktop widgets (Rainmeter); null in demo, developer and snapshot runs, which must not touch them.</summary>
    public Widgets.WidgetHost? Widgets { get; init; }

    /// <summary>Whether Windows lets Bambu Bar's toasts through (the Notifications page's banner); null when unknown.</summary>
    public Func<bool?>? ToastsEnabled { get; init; }
}

/// <summary>Keys this app adds to the Mac's settings (same file, Windows-only).</summary>
public static class WindowSettings
{
    /// <summary>The window's last frame, restored on the next open (the Mac's frame autosave "BambuBarMain").</summary>
    public const string Frame = "windowFrame";
    /// <summary>The tray icon shows the percentage in place of the printer while printing.</summary>
    public const string TrayShowsPercent = "trayShowsPercent";
    /// <summary>The language picked in Settings (a catalog code); missing follows Windows.</summary>
    public const string Language = "appLanguage";
    /// <summary>The camera card on the Printer page is open (the Mac's "cameraOnPrinterPage").</summary>
    public const string CameraOnPrinterPage = "cameraOnPrinterPage";
}

/// <summary>What every page shares: the store, settings, theme, and the way to other pages.</summary>
internal sealed class WindowContext
{
    public required PrinterStore Store { get; init; }
    public required AppSettings Settings { get; init; }
    public required WindowServices Services { get; init; }
    public required ResourceDictionary Theme { get; init; }
    public required bool Dark { get; init; }
    public required bool Animate { get; init; }
    public required Action<AppPage> Show { get; init; }
    /// <summary>Opens setup for another printer (the Mac's MainWindow.addPrinter).</summary>
    public required Action<DiscoveredPrinter?> AddPrinter { get; init; }
    /// <summary>The panel's context, so the window can host the panel's sections (job card, tiles, filament).</summary>
    public required PanelContext Panel { get; init; }

    public Brush Brush(string key) => (Brush?)Theme[key] ?? Brushes.Gray;
    public SolidColorBrush Ink(Color color) => Palette.Brush(Palette.Ink(color, Dark));
}

/// <summary>
/// A page of the window. <see cref="Build"/> makes its elements once; <see cref="Refresh"/> runs after every change
/// of the store (at most once per dispatcher turn) and updates them in place, or rebuilds the parts whose
/// <see cref="Region"/> key changed, so typing, focus and hover survive the printer's report every second.
/// </summary>
internal abstract class Page(WindowContext ctx) : IDisposable
{
    protected WindowContext Ctx { get; } = ctx;
    protected PrinterStore Store => Ctx.Store;

    public abstract FrameworkElement Build();
    public virtual void Refresh() { }
    public virtual void Dispose() { }
}

/// <summary>A part of a page rebuilt only when its key changes.</summary>
internal sealed class Region
{
    private string? _key;

    public Border Host { get; } = new();

    /// <summary>Builds the content when <paramref name="key"/> differs from last time; null content hides it.</summary>
    public void Show(string key, Func<UIElement?> build)
    {
        if (key == _key) return;
        _key = key;
        var content = build();
        Host.Child = content;
        Host.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Hide() => Show("\0hidden", () => null);
}
