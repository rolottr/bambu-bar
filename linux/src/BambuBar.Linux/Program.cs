using System.Diagnostics;
using System.Globalization;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

internal static class Program
{
    private static Shell? _shell;
    public static int Main(string[] args)
    {
        if (args.Contains("--version")) { Console.WriteLine($"Bambu Bar {AppVersion.Marketing} ({Platform.Commit})"); return 0; }
        var codecFolder = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(codecFolder)) BambuBar.Core.Camera.Decoding.FFmpeg.Folder = codecFolder;
        var probeIndex = Array.IndexOf(args, "--probe");
        var cameraIndex = Array.IndexOf(args, "--probe-camera");
        var keyIndex = (probeIndex >= 0 ? probeIndex : cameraIndex) + 3;
        if ((probeIndex >= 0 || cameraIndex >= 0) && keyIndex < args.Length && args[keyIndex] == "keyring")
        {
            var secret = Platform.Run("secret-tool", ["lookup", "application", Platform.AppId, "serial", args[keyIndex - 1]]).GetAwaiter().GetResult();
            if (secret.Exit != 0) { Console.Error.WriteLine("Access code unavailable in the keyring."); return 1; }
            args[keyIndex] = secret.Output.TrimEnd('\r', '\n');
        }
        if (Probe.Parse(args) is { } probe) return Probe.RunAsync(probe, Console.Out).GetAwaiter().GetResult();
        if (CameraProbe.Parse(args) is { } camera) return CameraProbe.RunAsync(camera, Console.Out).GetAwaiter().GetResult();
        var ephemeral = PrinterStore.IsEphemeralRun(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (!ephemeral && e.ExceptionObject is Exception error) Platform.Log(error.GetType().Name + "\n" + error.StackTrace); };
        var timer = Stopwatch.StartNew();
        TrayPlacement.LoadBeforeGtk();
        var application = Adw.Application.New(Platform.AppId, Gio.ApplicationFlags.HandlesCommandLine | (ephemeral ? Gio.ApplicationFlags.NonUnique : Gio.ApplicationFlags.DefaultFlags));
        var launchArgs = args;
        application.OnCommandLine += (_, e) =>
        {
            Platform.Trace("command-line handler");
            var commandLine = e.CommandLine;
            var forwarded = commandLine.GetArguments(out var count);
            if (forwarded.Contains("--quit")) { GLib.Functions.TimeoutAdd(200, 100, () => { application.Quit(); return false; }); commandLine.SetExitStatus(0); commandLine.Done(); commandLine.Dispose(); return 0; }
            launchArgs = forwarded;
            if (_shell is null) application.Activate();
            else if (!forwarded.Contains("--hide") && !forwarded.Contains("--background") && !forwarded.Contains("--panel")) _shell.Present();
            _shell?.HandleCommand(forwarded, () => { commandLine.SetExitStatus(0); commandLine.Done(); commandLine.Dispose(); });
            return 0;
        };
        application.OnActivate += async (_, _) =>
        {
            Platform.Trace("activate handler");
            if (_shell is not null) { _shell.Present(); return; }
            application.Hold();
            var settings = new AppSettings(ephemeral ? null : Path.Combine(Platform.Config, "settings.json"));
            var languageIndex = Array.IndexOf(args, "--lang");
            var language = languageIndex >= 0 && languageIndex + 1 < args.Length ? args[languageIndex + 1] : settings.Get<string>("appLanguage");
            var strings = Strings.LoadFor(s => language ?? s.Match(CultureInfo.CurrentUICulture));
            strings.Use(language ?? strings.Match(CultureInfo.CurrentUICulture));
            var secrets = new SecretStore();
            var notifier = new Notifier(application);
            var store = new PrinterStore(new PrinterStore.Services
            {
                Settings = settings,
                History = new HistoryStore(ephemeral ? null : Path.Combine(Platform.State, "history.json")),
                Secrets = ephemeral ? new MemorySecretStore() : secrets,
                Notifier = notifier,
                Arguments = args,
                StartDiscoveryInBackground = true,
            });
            _shell = new Shell(application, store, settings, secrets, notifier, args);
            if (!launchArgs.Contains("--background") && !launchArgs.Contains("--panel")) _shell.Present();
            try
            {
                if (!ephemeral) await secrets.Load((settings.Get<List<PrinterConfig>>(AppSettings.Keys.Printers) ?? []).Select(p => p.Serial));
                store.Start();
                _shell.Started();
                if (!ephemeral) Platform.Log($"ready {timer.ElapsedMilliseconds} ms; {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}; {AppVersion.Marketing} ({Platform.Commit})");
            }
            catch (OperationCanceledException) when (secrets.IsDisposed) { }
            catch (Exception e) { _shell.Error(e.Message); store.Start(); _shell.Started(); }
        };
        application.OnShutdown += (_, _) => _shell?.Dispose();
        return application.RunWithSynchronizationContext(["bambu-bar", .. args]);
    }
}
