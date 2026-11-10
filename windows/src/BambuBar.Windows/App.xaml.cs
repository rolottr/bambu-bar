using System.Globalization;
using System.IO;
using System.Windows;
using BambuBar.Core;
using BambuBar.Core.App;
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
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private WidgetHost? _widgets;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
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

        var strings = Strings.Load();
        strings.Use(strings.Match(CultureInfo.CurrentUICulture));

        var folder = AppSettings.DefaultFolder;
        var install = AppContext.BaseDirectory;
        if (!ephemeral)
        {
            _widgets = new WidgetHost(install);
            _widgets.Start();
            Registration.RegisterUriScheme();
        }
        _settings = new AppSettings(ephemeral ? null : Path.Combine(folder, "settings.json"));
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

        _tray = new TrayIcon(_store);
        _tray.OpenWindow += ShowWindow;
        _tray.OpenPanel += TogglePanel;
        _tray.Quit += Quit;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (!args.Contains("--background")) ShowWindow();
        // Demo runs open the panel too, so CI's desktop screenshot shows it in place over the taskbar.
        if (args.Contains("--demo") || args.Contains("--panel"))
            Dispatcher.InvokeAsync(TogglePanel, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>A left click on the tray icon: the flyout opens, or closes if it's open.</summary>
    private void TogglePanel()
    {
        if (_store is null || _settings is null) return;
        if (_panel is null)
        {
            var view = new PanelView(_store, _settings, AppTheme.Current, animate: true);
            _panel = new PanelWindow(view);
            // The window's pages (P3.5) will open at the destination; for now every button opens the window.
            view.Navigate += (_, _) =>
            {
                _panel.HidePanel();
                ShowWindow();
            };
            view.QuitRequested += Quit;
        }
        _panel.Toggle();
    }

    /// <summary>A later launch's arguments: a link to a printer, or just "show yourself".</summary>
    private void Handle(string[] args)
    {
        var link = args.FirstOrDefault(a => a.StartsWith("bambubar://", StringComparison.OrdinalIgnoreCase));
        if (link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Host == "printer")
        {
            var serial = uri.AbsolutePath.Trim('/');
            if (serial.Length > 0) _store?.Select(serial.ToUpperInvariant());
        }
        if (!args.Contains("--background")) ShowWindow();
    }

    private void ShowWindow()
    {
        _window ??= new MainWindow();
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _store?.OnSleep();
        else if (e.Mode == PowerModes.Resume) _store?.OnWake();
    }

    private void Quit()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _panel?.Quit();
        _tray?.Dispose();
        _store?.Dispose();
        // Widgets close with the app (O5).
        _widgets?.Dispose();
        _instance?.Dispose();
        Shutdown();
    }
}
