using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using BambuBar.AppWindow;
using BambuBar.CameraViews;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Core.Updates;
using BambuBar.Notifications;
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
    private const int AsfwAny = -1;

    private SingleInstance? _instance;
    private PrinterStore? _store;
    private AppSettings? _settings;
    private LicenseStore? _license;
    private UpdateChecker? _updates;
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private PanelView? _panelView;
    private WidgetHost? _widgets;
    private INotifier _notifier = new NullNotifier();
    private MainWindow? _window;
    private string[] _arguments = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);
        var args = e.Args;
        _arguments = args;
        var ephemeral = PrinterStore.IsEphemeralRun(args);

        // Demo and developer runs live beside the real app; everything else is one copy per session.
        if (!ephemeral)
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirst)
            {
                // The running copy may bring its window or a widget's menu forward: this launch came from a click, so
                // it may hand that right on.
                AllowSetForegroundWindow(AsfwAny);
                SingleInstance.HandOver(args);
                Shutdown();
                return;
            }
            _instance.Listen(more => Dispatcher.BeginInvoke(() => Handle(more)));
        }

        var folder = AppSettings.DefaultFolder;
        _settings = new AppSettings(ephemeral ? null : Path.Combine(folder, "settings.json"));
        // The language picked in Settings, else the first of Windows' languages the catalog has.
        // Only the language shown (and English): a language change restarts the app.
        string? language = null;
        var settings = _settings;
        var strings = Strings.LoadFor(catalog => language = settings.Get<string>(WindowSettings.Language) ?? catalog.Match(CultureInfo.CurrentUICulture));
        strings.Use(language ?? "en");

        var install = AppContext.BaseDirectory;
        INotifier notifier = _notifier;
        if (!ephemeral)
        {
            _widgets = new WidgetHost(install);
            _widgets.Start();
            Registration.RegisterUriScheme();
            // Toasts need the app's AppUserModelID registered and taken before the first one (P3.1, P3.7).
            AppIdentity.Register();
            notifier = _notifier = new ToastNotifier();
        }
        _store = new PrinterStore(new PrinterStore.Services
        {
            Settings = _settings,
            History = new HistoryStore(ephemeral ? null : Path.Combine(folder, "history.json")),
            Secrets = ephemeral ? new MemorySecretStore() : new CredentialStore(),
            // Written on a worker: the art's PNG and Rainmeter's reload would freeze the UI (QueuedWidgetSink).
            Widgets = _widgets is null ? null : new QueuedWidgetSink(_widgets.State),
            StartDiscoveryInBackground = true,
            Confirmer = new StopConfirmer(() => _panel),
            Notifier = notifier,
            Arguments = args,
        });
        // Windows Firewall keeping the printers' announcements out (ledger §4 trap 5): the search then says so.
        if (!ephemeral)
        {
            Firewall.Prime();
            _store.InboundBlocked = Firewall.InboundBlocked;
        }
        // The camera windows keep their frames in the settings; a camera turned off or a printer forgotten closes its window.
        CameraWindow.Settings = _settings;
        _store.CameraSettingsChanged = () => CameraWindow.CloseWhereHidden(_store);
        ((System.Collections.Specialized.INotifyCollectionChanged)_store.Sessions).CollectionChanged += (_, _) => CameraWindow.CloseWhereHidden(_store);
        _store.Start();

        // The supporter license (P5.3): validated now and every 15 minutes while a key is saved; demo runs have none.
        _license = new LicenseStore(new PolarClient(), ephemeral ? new MemoryLicenseVault() : new CredentialLicenseVault());
        _license.PropertyChanged += (_, _) => { if (_panelView is not null) _panelView.ShowsSupport = !_license.HasAccess; };
        _license.Start();
        if (!ephemeral)
        {
            // Updates (ledger P5.4): the shared appcast's Windows item for this PC, checked daily while the supporter
            // license is active, Ed25519-verified before the installer runs.
            var license = _license;
            _updates = new UpdateChecker(() => license.HasAccess, _settings, new UpdateFeed(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bambu Bar", "Updates"));
            _updates.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(ShowUpdateOffer);
            license.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(() => _updates?.SupporterStatusChanged());
            _updates.Start();
        }

        _tray = new TrayIcon(_store) { ShowPercent = _settings.Get(WindowSettings.TrayShowsPercent, false) };
        _tray.OpenWindow += () => ShowWindow();
        _tray.OpenPage += page => ShowWindow(page);
        // "Check for Updates…": Settings → Updates shows the check and its result.
        if (_updates is { } updates)
            _tray.CheckForUpdates = () =>
            {
                ShowWindow(AppPage.Settings);
                if (updates.CanCheckForUpdates) _ = updates.CheckAsync();
            };
        _tray.OpenPanel += TogglePanel;
        _tray.Quit += Quit;
        _tray.ShowCamera += CameraWindow.Show;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        // A first launch from a link (a widget, bambubar://printer/…, a toast) goes where the link says; one from a
        // widget's right-click shows that widget's menu.
        if (args.Any(IsLink)) Handle(args);
        else if (WidgetMenu.Requested(args) is not null)
            Dispatcher.InvokeAsync(() => Handle(args), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        else if (!args.Contains("--background")) ShowWindow();
        // Demo runs open the panel too, so CI's desktop screenshot shows it in place over the taskbar.
        if (args.Contains("--demo") || args.Contains("--panel"))
            Dispatcher.InvokeAsync(TogglePanel, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // The first click on the tray icon shouldn't wait for files, Direct3D and the panel's views to be made.
        _ = PanelWarmup.RunAsync(_store, () => EnsurePanel()?.PrimeAsync() ?? Task.CompletedTask, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// After the window closes, its pages, pictures and 3D view are garbage, but .NET keeps the memory until a later
    /// collection, and keeps the freed pages after it. Two seconds later, with nothing of the app on screen, one
    /// compacting collection gives them back to Windows (tens of milliseconds, while nobody is looking; skipped if the
    /// window or the panel is open again by then).
    /// </summary>
    private void ReleaseMemorySoon()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_window is not null || _panel?.IsVisible == true) return;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
        timer.Start();
    }

    private static bool IsLink(string arg) => arg.StartsWith("bambubar://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A left click on the tray icon: the flyout opens, or closes if it's open.</summary>
    private void TogglePanel()
    {
        if (EnsurePanel() is { } panel) panel.Toggle();
    }

    /// <summary>The tray panel, built on first use (or ahead of it by <see cref="PanelWarmup"/>).</summary>
    private PanelWindow? EnsurePanel()
    {
        if (_store is null || _settings is null) return null;
        if (_panel is null)
        {
            var view = new PanelView(_store, _settings, AppTheme.Current, animate: true) { ShowsSupport = !(_license?.HasAccess ?? false) };
            _panelView = view;
            ShowUpdateOffer();
            var panel = _panel = new PanelWindow(view);
            // Every button that leads out of the panel opens the window at its page.
            view.Navigate += (destination, printer) =>
            {
                panel.HidePanel();
                ShowWindow()?.View.Go(destination, printer);
            };
            view.QuitRequested += Quit;
        }
        return _panel;
    }

    /// <summary>A launch's arguments: <c>bambubar://printer/&lt;serial&gt;</c> (select it, show its page),
    /// <c>bambubar://open</c>, or just "show yourself" unless <c>--background</c>.</summary>
    private void Handle(string[] args)
    {
        // A widget's right-click: its menu at the cursor, and nothing else (P2.5).
        if (WidgetMenu.Requested(args) is { } config)
        {
            if (_widgets is not null && _store is not null) WidgetMenu.Show(_widgets, _store, config);
            return;
        }
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
                Widgets = _widgets,
                ToastsEnabled = _widgets is null ? null : ToastNotifier.Enabled,
                Updates = _updates,
                InstallUpdate = InstallUpdate,
                TestNotification = () => _notifier.Post(Loc.T("Notifications are on"),
                    Loc.T("This is how Bambu Bar tells you a print finished, failed or needs you."), "bambubar-test"),
                Restart = Restart,
                TrayChanged = () => { if (_tray is not null && _settings is not null) _tray.ShowPercent = _settings.Get(WindowSettings.TrayShowsPercent, false); },
            });
            // Closed, it's let go and built again on the next open, so a hidden window doesn't follow every report.
            window.Closed += (_, _) =>
            {
                if (_window == window) _window = null;
                ReleaseMemorySoon();
            };
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

    /// <summary>A locked screen shows nobody the camera: its streams stop 4 s later, and come back on unlocking.</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.BeginInvoke(() => CameraPlayer.SessionLocked(true));
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            Dispatcher.BeginInvoke(() => CameraPlayer.SessionLocked(false));
    }

    /// <summary>The tray panel's "Bambu Bar X is available" row, while there's a release to offer.</summary>
    private void ShowUpdateOffer()
    {
        if (_panelView is null || _updates is null) return;
        var offered = _updates.Offered;
        var offer = offered is null ? null : new UpdateOffer(offered.DisplayVersion ?? "", InstallUpdate, _updates.RemindLater);
        if (offer?.Version == _panelView.UpdateOffer?.Version && (offer is null) == (_panelView.UpdateOffer is null)) return;
        _panelView.UpdateOffer = offer;
        _panelView.Refresh();
    }

    /// <summary>"Update…": downloads and verifies the release (unless it's already here), runs its installer silently
    /// and quits; the installer replaces the files and starts Bambu Bar again (/RELAUNCH=1). A failure shows in
    /// Settings → Updates.</summary>
    private async void InstallUpdate()
    {
        if (_updates is null) return;
        var installer = _updates.Installer is { } ready && File.Exists(ready.Path) ? ready : await _updates.DownloadAsync();
        if (installer is null)
        {
            ShowWindow(AppPage.Settings);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(installer.Path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            CrashLog.Write("starting the update installer", e);
            ShowWindow(AppPage.Settings);
            return;
        }
        Quit();
    }

    private void Quit()
    {
        var waiting = _updates is { DownloadsAutomatically: true, State: UpdateState.Ready, Installer: { } ready } && File.Exists(ready.Path)
            ? _updates.Installer!.Path : null;
        Release();
        // "Download and install updates automatically": one downloaded and verified in the background installs as the
        // app quits (Sparkle's install on quit), silently and without starting it again.
        if (waiting is not null)
        {
            try { Process.Start(new ProcessStartInfo(waiting, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception e) { CrashLog.Write("installing the downloaded update", e); }
        }
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
            var menu = _arguments.ToList().IndexOf("--widget-menu");
            var kept = _arguments.Where((a, i) => a != "--background" && !IsLink(a) && i != menu && i != menu + 1);
            foreach (var arg in kept) start.ArgumentList.Add(arg);
            try { Process.Start(start)?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { /* Couldn't start: the person opens it again. */ }
        }
        Shutdown();
    }

    /// <summary>Lets go of everything: the window, panel, tray icon, printers, widgets and the single-instance lock.</summary>
    private void Release()
    {
        // Each step on its own: one that fails is logged and the rest still let go (a crash on Quit helps nobody).
        CrashLog.Quitting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        CrashLog.Step("cameras", CameraWindow.CloseAll);
        CrashLog.Step("window", () => _window?.Close());
        CrashLog.Step("panel", () => _panel?.Quit());
        CrashLog.Step("updates", () => _updates?.Stop());
        CrashLog.Step("license", () => _license?.Stop());
        // The printers before the tray icon, which shows their state to the end.
        CrashLog.Step("printers", () => _store?.Dispose());
        CrashLog.Step("tray", () => _tray?.Dispose());
        // Widgets close with the app (O5).
        CrashLog.Step("widgets", () => _widgets?.Dispose());
        CrashLog.Step("single instance", () => _instance?.Dispose());
        _instance = null;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
