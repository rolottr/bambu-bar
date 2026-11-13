using System.Collections.Concurrent;
using System.Diagnostics;
using BambuBar.Core.App;

namespace BambuBar.Linux;

internal static class Platform
{
    public const string AppId = "dev.rolottr.BambuBar";
    // Only presentation/install guidance depends on the desktop; IPC still probes services.
    public static bool IsPlasma => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);
    public static bool IsGnome => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Any(s => s.Contains("GNOME", StringComparison.OrdinalIgnoreCase));
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Config => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config"), "bambu-bar");
    public static string State => Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(Home, ".local", "state"), "bambu-bar");
    public static string Commit => typeof(Platform).Assembly.GetCustomAttributes(false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+').ElementAtOrDefault(1) is { Length: > 0 } revision ? revision[..Math.Min(7, revision.Length)] : "development";

    public static async Task<(int Exit, string Output)> Run(string file, IEnumerable<string> args, string? input = null,
        bool waitForUser = false, CancellationToken cancellationToken = default)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "libexec", file);
        if (File.Exists(bundled)) file = bundled;
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException($"Unable to start {file}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!waitForUser) timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // Shutdown stops GTK's synchronization context. Kill synchronously in the
        // cancellation callback rather than relying on an async continuation there.
        using var cancelProcess = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Already exited. */ }
        });
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"{file} did not respond");
        }
        await error; // Consume errors without printing: services can include private data.
        return (process.ExitCode, await output);
    }

    public static void Trace(string message) { if (Environment.GetEnvironmentVariable("BAMBUBAR_DEBUG_UI") == "1") Log(message); }

    public static void Log(string message)
    {
        Directory.CreateDirectory(State);
        File.AppendAllText(Path.Combine(State, "app.log"), $"{DateTimeOffset.Now:O} {message}\n");
    }

    public static bool Autostart => File.Exists(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config"), "autostart", AppId + ".desktop"));
    public static void SetAutostart(bool enabled)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config"), "autostart", AppId + ".desktop");
        if (!enabled) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var exe = Environment.ProcessPath ?? throw new IOException("Application path unavailable");
        if (Path.GetFileName(exe) == "dotnet") throw new IOException("Install Bambu Bar before enabling startup at login.");
        var escaped = exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        File.WriteAllText(path, $"[Desktop Entry]\nType=Application\nName=Bambu Bar\nExec=\"{escaped}\" --background\nIcon={AppId}\nX-GNOME-Autostart-enabled=true\n");
    }
}

/// <summary>Secret Service through libsecret's supported CLI. Values travel over stdin, never command arguments.
/// All IPC is awaited before connecting; the core reads this in-memory cache without blocking GTK.</summary>
internal sealed class SecretStore : ISecretStore, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    public bool IsDisposed { get; private set; }
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.Ordinal);
    public string? AccessCode(string serial) => _codes.GetValueOrDefault(serial);
    public async Task Load(IEnumerable<string> serials)
    {
        foreach (var serial in serials)
        {
            // Wallet unlock/authorization is interactive, especially with desktop autologin.
            var result = await Platform.Run("secret-tool", ["lookup", "application", Platform.AppId, "serial", serial],
                waitForUser: true, cancellationToken: _lifetime.Token);
            if (result.Exit == 0) _codes[serial] = result.Output.TrimEnd('\r', '\n');
        }
    }
    public async Task Save(string serial, string code)
    {
        var result = await Platform.Run("secret-tool", ["store", "--label=Bambu Bar printer", "application", Platform.AppId, "serial", serial], code,
            waitForUser: true, cancellationToken: _lifetime.Token);
        if (result.Exit != 0) throw new IOException("Unlock your keyring or enable a Secret Service provider, then try again.");
        _codes[serial] = code;
    }
    public void SetAccessCode(string code, string serial)
    {
        if (AccessCode(serial) != code) throw new InvalidOperationException("Save the access code securely before connecting.");
    }
    public void RemoveAccessCode(string serial)
    {
        _codes.TryRemove(serial, out _);
        _ = Clear(serial);
    }
    private async Task Clear(string serial)
    {
        try { await Platform.Run("secret-tool", ["clear", "application", Platform.AppId, "serial", serial],
            waitForUser: true, cancellationToken: _lifetime.Token); }
        catch (Exception) { Platform.Log("Secret removal unavailable; keyring entry retained."); }
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

internal sealed class Notifier(Gio.Application application) : INotifier
{
    public void RequestAuthorization() { }
    public void Post(string title, string body, string id)
    {
        using var note = Gio.Notification.New(title);
        note.SetBody(body);
        note.SetDefaultActionAndTarget("app.printer", GLib.Variant.NewString(id.Split('.')[^1]));
        note.SetIcon(Gio.ThemedIcon.New(Platform.AppId));
        application.SendNotification(id, note);
    }
}
