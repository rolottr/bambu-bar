using System.Text.Json;
using System.Text.Json.Nodes;
using BambuBar.Core.Model;

namespace BambuBar.Core.App;

/// <summary>
/// The app's settings, one JSON file (%APPDATA%\Bambu Bar\settings.json), under the Mac's UserDefaults key names.
/// Without a path nothing is written: demo, emulator and developer runs, and tests.
/// </summary>
public sealed class AppSettings
{
    private readonly string? _path;
    private readonly JsonObject _values;
    private readonly object _gate = new();

    public AppSettings(string? path)
    {
        _path = path;
        _values = Load(path);
    }

    /// <summary>The file couldn't be written (an antivirus that guards folders, a locked or read-only file). The values
    /// stay in memory and every later change tries again, so a click that saves never ends the app. Raised on the
    /// thread that made the change.</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>%APPDATA%\Bambu Bar (or Application Support on a Mac, for development).</summary>
    public static string DefaultFolder => Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support"),
        "Bambu Bar");

    private static JsonObject Load(string? path)
    {
        if (path is null || !File.Exists(path)) return [];
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Primitives (bool, string, numbers, dates) straight from the parsed value; other types through the
    /// serializer. The serializer builds its metadata for a type by reflection on first use: 300 ms of the app's launch
    /// on a slow machine, for settings that are mostly a bool or a string.</summary>
    private static T? Read<T>(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<T>(out var direct) ? direct
        : CoreJson.Default.GetTypeInfo(typeof(T)) is System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> known ? node.Deserialize(known)
        : node.Deserialize<T>();

    public T? Get<T>(string key)
    {
        lock (_gate)
        {
            try { return _values[key] is { } node ? Read<T>(node) : default; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return default; }
        }
    }

    public T Get<T>(string key, T fallback) where T : struct
    {
        lock (_gate)
        {
            try { return _values[key] is { } node ? Read<T>(node) : fallback; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return fallback; }
        }
    }

    public void Set<T>(string key, T? value)
    {
        lock (_gate)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            FileWrite.Atomic(_path, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke(e);
        }
    }

    /// <summary>The Mac's key names, kept so the two read alike.</summary>
    public static class Keys
    {
        public const string Printers = "printers";
        public const string Selected = "selectedPrinter";
        public const string MenuBarPrinter = "menuBarPrinter";
        public const string MenuBarStyle = "menuBarStyle";
        public const string NotifyFinished = "notifyFinished";
        public const string NotifyFailed = "notifyFailed";
        public const string NotifyPaused = "notifyPaused";
        public const string NotifyStarted = "notifyStarted";
        public const string ConfirmStop = "confirmStop";
        public const string ShowPrinterActions = "showPrinterActions";
        public const string CameraStaysOnWhilePrinting = "cameraStaysOnWhilePrinting";
        public const string CameraEnabled = "cameraEnabled";
        public const string PanelTemperatures = "panelShowsTemperatures";
        public const string PanelChamber = "panelShowsChamber";
        public const string PanelFans = "panelShowsFans";
        public const string PanelFilament = "panelShowsFilament";
        public static string DismissedIssues(string serial) => $"dismissedIssues.{serial}";
    }
}

/// <summary>Writes a file so that readers never see half of it (the widgets' Lua reads every second).</summary>
public static class FileWrite
{
    public static void Atomic(string path, string text)
    {
        var temp = path + ".tmp-" + Environment.ProcessId;
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Don't leave the half-step behind when the file itself can't be replaced.
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}

/// <summary>What the tray icon shows next to its glyph during a print (the Mac's menu bar style).</summary>
public enum TrayStyle { Icon, Percent, Remaining, Both }

public static class TrayStyles
{
    public static string RawValue(this TrayStyle s) => s switch
    {
        TrayStyle.Icon => "icon", TrayStyle.Percent => "percent", TrayStyle.Remaining => "remaining", _ => "both",
    };

    public static TrayStyle Parse(string? raw) => raw switch
    {
        "icon" => TrayStyle.Icon, "percent" => TrayStyle.Percent, "remaining" => TrayStyle.Remaining, _ => TrayStyle.Both,
    };

    public static string Title(this TrayStyle s) => s switch
    {
        TrayStyle.Icon => Loc.T("Icon only", "Menu bar style"),
        TrayStyle.Percent => Loc.T("Progress", "Menu bar style"),
        TrayStyle.Remaining => Loc.T("Time left", "Menu bar style"),
        _ => Loc.T("Progress and time left", "Menu bar style"),
    };

    /// <summary>The text beside the icon during a print, in this style; null for the icon alone.</summary>
    public static string? Text(this TrayStyle style, PrinterStatus s)
    {
        var pct = $"{s.Progress}%";
        var left = s.RemainingMinutes > 0 ? Format.Duration(s.RemainingMinutes) : null;
        return style switch
        {
            TrayStyle.Icon => null,
            TrayStyle.Percent => s.State == JobState.Paused ? s.State.Title() : pct,
            TrayStyle.Remaining => s.State == JobState.Paused ? s.State.Title() : left ?? pct,
            _ when s.State == JobState.Paused => $"{pct} · {s.State.Title()}",
            _ when s.State == JobState.Preparing => s.Progress > 0 ? pct : s.State.Title(),
            _ => left is null ? pct : $"{pct} · {left}",
        };
    }
}
