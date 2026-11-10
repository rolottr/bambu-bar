using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using BambuBar.AppWindow;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Platform;
using BambuBar.Theme;
using BambuBar.Tray;
using BambuBar.TrayPanel;
using BambuBar.Widgets;
using Microsoft.Win32;

namespace BambuBar;

/// <summary>
/// Bambu Bar for Windows: a tray icon whose click opens the panel, a main window, desktop widgets drawn by a private
/// Rainmeter. Starts in the tray at sign-in (<c>--background</c>), shows its window when opened by hand. One copy per
/// session; later launches (Start menu, widget clicks, <c>bambubar://</c> links) hand their arguments over.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private PrinterStore? _store;
    private AppSettings? _settings;
    private LicenseStore? _license;
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private PanelView? _panelView;
    private WidgetHost? _widgets;
    private MainWindow? _window;
    private string[] _arguments = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        _arguments = args;
        var ephemeral = PrinterStore.IsEphemeralRun(args);

        // Demo and developer runs live beside the real app; everything else is one copy per session.
        if (!ephemeral)
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirst)
            {
                SingleInstance.HandOver(args);
                Shutdown();
                return;
            }
            _instance.Listen(more => Dispatcher.BeginInvoke(() => Handle(more)));
        }

        var folder = AppSettings.DefaultFolder;
        _settings = new AppSettings(ephemeral ? null : Path.Combine(folder, "settings.json"));
        // The language picked in Settings, else the first of Windows' languages the catalog has.
        var strings = Strings.Load();
        strings.Use(_settings.Get<string>(WindowSettings.Language) ?? strings.Match(CultureInfo.CurrentUICulture));

        var install = AppContext.BaseDirectory;
        if (!ephemeral)
        {
            _widgets = new WidgetHost(install);
            _widgets.Start();
            Registration.RegisterUriScheme();
        }
        _store = new PrinterStore(new PrinterStore.Services
        {
            Settings = _settings,
            History = new HistoryStore(ephemeral ? null : Path.Combine(folder, "history.json")),
            Secrets = ephemeral ? new MemorySecretStore() : new CredentialStore(),
            Widgets = _widgets?.State,
            Confirmer = new StopConfirmer(() => _panel),
            Arguments = args,
        });
        _store.Start();

        // The supporter license (P5.3): validated now and every 15 minutes while a key is saved; demo runs have none.
        _license = new LicenseStore(new PolarClient(), ephemeral ? new MemoryLicenseVault() : new CredentialLicenseVault());
        _license.PropertyChanged += (_, _) => { if (_panelView is not null) _panelView.ShowsSupport = !_license.HasAccess; };
        _license.Start();

        _tray = new TrayIcon(_store) { ShowPercent = _settings.Get(WindowSettings.TrayShowsPercent, false) };
        _tray.OpenWindow += () => ShowWindow();
        _tray.OpenPanel += TogglePanel;
        _tray.Quit += Quit;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // A first launch from a link (a widget, bambubar://printer/…) goes where the link says.
        if (args.Any(IsLink)) Handle(args);
        else if (!args.Contains("--background")) ShowWindow();
        // Demo runs open the panel too, so CI's desktop screenshot shows it in place over the taskbar.
        if (args.Contains("--demo") || args.Contains("--panel"))
            Dispatcher.InvokeAsync(TogglePanel, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static bool IsLink(string arg) => arg.StartsWith("bambubar://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A left click on the tray icon: the flyout opens, or closes if it's open.</summary>
    private void TogglePanel()
    {
        if (_store is null || _settings is null) return;
        if (_panel is null)
        {
            var view = new PanelView(_store, _settings, AppTheme.Current, animate: true) { ShowsSupport = !(_license?.HasAccess ?? false) };
            _panelView = view;
            _panel = new PanelWindow(view);
            // Every button that leads out of the panel opens the window at its page.
            view.Navigate += (destination, printer) =>
            {
                _panel.HidePanel();
                ShowWindow()?.View.Go(destination, printer);
            };
            view.QuitRequested += Quit;
        }
        _panel.Toggle();
    }

    /// <summary>A launch's arguments: <c>bambubar://printer/&lt;serial&gt;</c> (select it, show its page),
    /// <c>bambubar://open</c>, or just "show yourself" unless <c>--background</c>.</summary>
    private void Handle(string[] args)
    {
        var link = args.FirstOrDefault(IsLink);
        if (link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Equals("printer", StringComparison.OrdinalIgnoreCase))
            {
                var serial = uri.AbsolutePath.Trim('/');
                if (serial.Length > 0) _store?.Select(serial.ToUpperInvariant());
                ShowWindow(AppPage.Printer);
                return;
            }
            if (uri.Host.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                ShowWindow();
                return;
            }
        }
        if (!args.Contains("--background")) ShowWindow();
    }

    /// <summary>Opens (or brings forward) the window, at <paramref name="page"/> when given.</summary>
    private MainWindow? ShowWindow(AppPage? page = null)
    {
        if (_store is null || _settings is null) return null;
        if (_window is null)
        {
            var window = new MainWindow(_store, _settings, new WindowServices
            {
                License = _license,
                Restart = Restart,
                TrayChanged = () => { if (_tray is not null && _settings is not null) _tray.ShowPercent = _settings.Get(WindowSettings.TrayShowsPercent, false); },
            });
            // Closed, it's let go and built again on the next open, so a hidden window doesn't follow every report.
            window.Closed += (_, _) => { if (_window == window) _window = null; };
            _window = window;
        }
        if (page is { } p) _window.Show(p);
        _window.Present();
        return _window;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _store?.OnSleep();
        else if (e.Mode == PowerModes.Resume) _store?.OnWake();
    }

    private void Quit()
    {
        Release();
        Shutdown();
    }

    /// <summary>Starts a fresh copy and quits this one, so a new language takes effect (the Mac's
    /// AppLanguage.relaunch). The new copy opens its window, whatever this one was started with.</summary>
    private void Restart()
    {
        var exe = Environment.ProcessPath;
        Release();
        if (exe is not null)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in _arguments.Where(a => a != "--background" && !IsLink(a))) start.ArgumentList.Add(arg);
            try { Process.Start(start)?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { /* Couldn't start: the person opens it again. */ }
        }
        Shutdown();
    }

    /// <summary>Lets go of everything: the window, panel, tray icon, printers, widgets and the single-instance lock.</summary>
    private void Release()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _window?.Close();
        _panel?.Quit();
        _tray?.Dispose();
        _license?.Stop();
        _store?.Dispose();
        // Widgets close with the app (O5).
        _widgets?.Dispose();
        _instance?.Dispose();
        _instance = null;
    }
}
