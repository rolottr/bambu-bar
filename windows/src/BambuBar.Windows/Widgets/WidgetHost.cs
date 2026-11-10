using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BambuBar.Core.Widgets;

namespace BambuBar.Widgets;

/// <summary>
/// Runs the private Rainmeter 5.0 that draws the desktop widgets (ledger D3, D4, P2.6). Lays out
/// <c>%LOCALAPPDATA%\Bambu Bar\Widgets</c> (our own Rainmeter.ini, the skins copied from the install folder, the
/// state files), starts Rainmeter only while at least one widget exists (§4.14), sends it bangs by WM_COPYDATA to
/// the window <b>its own process</b> owns (never a Rainmeter the person runs themselves, §4.13), restarts it after a
/// crash with back-off, and quits it with the app (O5). Lives on the UI thread.
/// </summary>
public sealed partial class WidgetHost : IDisposable
{
    public enum Size { Small, Medium, Large }

    public sealed record Widget(string Config, Size Size, string? Serial);

    private readonly string _root;
    private readonly string _installedSkins;
    private readonly string _rainmeterExe;
    private readonly string _bambuBarExe;
    private Process? _process;
    private int _crashes;
    private bool _quitting;

    /// <param name="installFolder">Where the installer put BambuBar.exe, <c>Rainmeter\</c> and <c>Skins\</c>.</param>
    public WidgetHost(string installFolder, string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bambu Bar", "Widgets");
        _installedSkins = Path.Combine(installFolder, "Skins", "BambuBar");
        _rainmeterExe = Path.Combine(installFolder, "Rainmeter", "Rainmeter.exe");
        _bambuBarExe = Path.Combine(installFolder, "BambuBar.exe");
        State = new WidgetStateWriter(StateFolder) { Reload = () => Bang("!UpdateGroup BambuBar") };
    }

    /// <summary>The store's widget sink: writes the state files the skins read.</summary>
    public WidgetStateWriter State { get; }

    public string StateFolder => Path.Combine(_root, "State");
    private string SkinsFolder => Path.Combine(_root, "Skins");
    private string IniPath => Path.Combine(_root, "Rainmeter.ini");
    private string BambuBarSkins => Path.Combine(SkinsFolder, "BambuBar");

    /// <summary>The widgets on the desktop, read from their stubs.</summary>
    public IReadOnlyList<Widget> Widgets
    {
        get
        {
            if (!Directory.Exists(BambuBarSkins)) return [];
            var widgets = new List<Widget>();
            foreach (var folder in Directory.GetDirectories(BambuBarSkins, "W*").Order(StringComparer.Ordinal))
            {
                var stub = Path.Combine(folder, "Widget.ini");
                if (!File.Exists(stub)) continue;
                var text = File.ReadAllText(stub);
                var size = Enum.GetValues<Size>().FirstOrDefault(s => text.Contains($@"..\{s}\{s}.ini", StringComparison.Ordinal));
                var state = text.Split('\n').FirstOrDefault(l => l.StartsWith("StateFile=", StringComparison.Ordinal))?.Trim()[10..];
                var serial = state is null ? null : Path.GetFileNameWithoutExtension(state);
                widgets.Add(new($@"BambuBar\{Path.GetFileName(folder)}", size, serial is "default" ? null : serial));
            }
            return widgets;
        }
    }

    /// <summary>Copies the skins when the installed ones differ (an app update), writes App.inc and Rainmeter.ini,
    /// and starts Rainmeter if any widget exists.</summary>
    public void Start()
    {
        Directory.CreateDirectory(StateFolder);
        SyncSkins();
        WriteSettings();
        if (Widgets.Count > 0) Launch();
    }

    /// <summary>Puts a new widget on the desktop, top right of the work area, staggered (Rainmeter keeps positions after).</summary>
    public Widget Add(Size size, string? serial, int x, int y)
    {
        var n = 1;
        while (Directory.Exists(Path.Combine(BambuBarSkins, $"W{n}"))) n++;
        var widget = new Widget($@"BambuBar\W{n}", size, serial);
        WriteStub(widget);
        AppendConfigSection(widget.Config, x, y);
        if (_process is null || _process.HasExited) Launch();
        else
        {
            Bang("!RefreshApp");
            Bang($"!ActivateConfig \"{widget.Config}\" \"Widget.ini\"");
        }
        return widget;
    }

    /// <summary>Changes a widget's size or printer; its position stays.</summary>
    public void Change(Widget widget, Size size, string? serial)
    {
        WriteStub(widget with { Size = size, Serial = serial });
        Bang($"!Refresh \"{widget.Config}\"");
    }

    /// <summary>"Keep on Top" from the widget menu: 1 on top, -2 on the desktop (Rainmeter's AlwaysOnTop).</summary>
    public void KeepOnTop(Widget widget, bool onTop) => Bang($"!ZPos {(onTop ? 1 : -2)} \"{widget.Config}\"");

    public void Remove(Widget widget)
    {
        Bang($"!DeactivateConfig \"{widget.Config}\"");
        try { Directory.Delete(Path.Combine(SkinsFolder, widget.Config), recursive: true); } catch (IOException) { }
        // No widgets left: Rainmeter would show its own tray icon with nothing loaded (§4.14), so it goes.
        if (Widgets.Count == 0) Quit();
    }

    public void Dispose() => Quit();

    // MARK: Files

    private void SyncSkins()
    {
        var installedVersion = Path.Combine(_installedSkins, "..", "version.txt");
        var copiedVersion = Path.Combine(SkinsFolder, "version.txt");
        var version = File.Exists(installedVersion) ? File.ReadAllText(installedVersion) : "dev";
        if (File.Exists(copiedVersion) && File.ReadAllText(copiedVersion) == version && Directory.Exists(BambuBarSkins)) return;
        // Our files are replaced wholesale (users can't edit the skins, D3); the W<n> instance stubs stay.
        foreach (var source in Directory.EnumerateFiles(_installedSkins, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(BambuBarSkins, Path.GetRelativePath(_installedSkins, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        File.WriteAllText(copiedVersion, version);
    }

    private void WriteSettings()
    {
        File.WriteAllText(Path.Combine(BambuBarSkins, "@Resources", "App.inc"),
            $"; Written by Bambu Bar.\r\n[Variables]\r\nBambuBarExe={_bambuBarExe}\r\n", new UTF8Encoding(true));
        if (File.Exists(IniPath)) return;
        File.WriteAllText(IniPath, string.Join("\r\n",
            "[Rainmeter]",
            $"SkinPath={SkinsFolder}\\",
            // No tray icon, no update prompts, no log: this Rainmeter is part of Bambu Bar (§7).
            "TrayIcon=0", "DisableVersionCheck=1", "DisableAutoUpdate=1", "Logging=0", "HardwareAcceleration=1",
            "") + "\r\n", new UnicodeEncoding(false, true));
    }

    private void WriteStub(Widget widget)
    {
        var folder = Path.Combine(SkinsFolder, widget.Config);
        Directory.CreateDirectory(folder);
        var state = Path.Combine(StateFolder, (widget.Serial ?? "default") + ".json");
        File.WriteAllText(Path.Combine(folder, "Widget.ini"),
            $"[Variables]\r\n@Include=#@#..\\{widget.Size}\\{widget.Size}.ini\r\nStateFile={state}\r\n", new UTF8Encoding(true));
    }

    /// <summary>The config's section in Rainmeter.ini: where it sits and how it behaves, before Rainmeter loads it.</summary>
    private void AppendConfigSection(string config, int x, int y) =>
        File.AppendAllText(IniPath, string.Join("\r\n",
            $"[{config}]", "Active=1", $"WindowX={x}", $"WindowY={y}", "AlwaysOnTop=-2", "Draggable=1", "SnapEdges=1",
            "KeepOnScreen=1", "SavePosition=1", "") + "\r\n", new UnicodeEncoding(false, false));

    // MARK: Rainmeter

    private void Launch()
    {
        if (!File.Exists(_rainmeterExe)) return;
        _quitting = false;
        // The settings path as the first argument: its own Rainmeter.ini, and with it its own single-instance mutex
        // (Rainmeter + MD5 of the path), so it runs beside a Rainmeter the person may have (§7).
        var process = Process.Start(new ProcessStartInfo(_rainmeterExe, $"\"{IniPath}\"") { UseShellExecute = false });
        if (process is null) return;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(process);
        _process = process;
    }

    private void OnExited(Process process)
    {
        if (_quitting || !ReferenceEquals(process, _process)) return;
        // A crash: back off 2, 4, 8… seconds, at most a minute, and give up after ten in a row.
        if (++_crashes > 10) return;
        var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, _crashes)));
        _ = Task.Delay(delay).ContinueWith(_ => { if (!_quitting && Widgets.Count > 0) Launch(); }, TaskScheduler.Default);
    }

    private void Quit()
    {
        _quitting = true;
        if (_process is not { HasExited: false } process) return;
        Bang("!Quit");
        if (!process.WaitForExit(3000)) process.Kill();
        _process = null;
    }

    /// <summary>Sends a bang to our Rainmeter: WM_COPYDATA with dwData 1 and the UTF-16 command, to the
    /// "Rainmeter control window" its process owns. Not <c>Rainmeter.exe !Bang</c>, which goes to the first one found.</summary>
    public bool Bang(string command)
    {
        if (_process is not { HasExited: false } process) return false;
        var window = ControlWindow(process.Id);
        if (window == 0) return false;
        var text = Marshal.StringToHGlobalUni(command);
        try
        {
            var data = new CopyData { dwData = 1, cbData = (command.Length + 1) * 2, lpData = text };
            SendMessage(window, WmCopyData, 0, ref data);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>The control window of the Rainmeter with this process id (class and title from Rainmeter.h).</summary>
    private static nint ControlWindow(int processId)
    {
        nint found = 0;
        for (var attempt = 0; attempt < 20 && found == 0; attempt++)
        {
            nint after = 0;
            while ((after = FindWindowEx(0, after, "DummyRainWClass", "Rainmeter control window")) != 0)
            {
                GetWindowThreadProcessId(after, out var pid);
                if (pid == processId) { found = after; break; }
            }
            if (found == 0) Thread.Sleep(100); // just started: its window comes up in a moment
        }
        return found;
    }

    private const int WmCopyData = 0x004A;

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nint dwData;
        public int cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint childAfter, string className, string windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, int message, nint wParam, ref CopyData lParam);
}
