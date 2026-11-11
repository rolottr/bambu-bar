using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using HelixToolkit.SharpDX;
using Microsoft.Extensions.Logging;

namespace BambuBar.Model3D;

/// <summary>
/// Whether this run draws the printer in 3D, and the one Direct3D 11 device (HelixToolkit's effects manager) every
/// model view shares. Tries the graphics card, then WARP (Direct3D's software rasterizer: VMs, CI runners, broken
/// drivers); when neither starts, or a view reports that its picture never came or rendering threw, 3D is off for
/// the rest of the run and every view shows the drawing (Core's PrinterArt) instead. <c>--no-3d</c> or
/// <c>BAMBUBAR_3D=off</c> turns it off from the start; <c>BAMBUBAR_3D=warp</c> skips the graphics card. Every step is
/// written to <c>%LOCALAPPDATA%\Bambu Bar\model3d.log</c> (and the debugger's output), so a "no 3D" report can say why.
/// </summary>
public static class Renderer3D
{
    private static EffectsManager? _effects;
    private static bool _tried;
    private static string? _offReason;
    private static readonly List<string> Lines = [];
    private static readonly object Gate = new();

    /// <summary>Raised once, on the UI thread, when 3D turns off for the run.</summary>
    public static event Action? TurnedOff;

    /// <summary>Snapshots (CI) log to their own folder instead of the user's.</summary>
    public static string? LogPath { get; set; } = DefaultLogPath();

    /// <summary>What happened: "Direct3D 11 on NVIDIA …", "WARP (software)", or why 3D is off.</summary>
    public static string Status { get; private set; } = "not started";

    /// <summary>Drawing with WARP, Direct3D's software rasterizer (no graphics card: VMs, basic display drivers): the
    /// views then skip multisampling, which costs a CPU core there.</summary>
    public static bool IsSoftware => _effects?.DriverType == SharpDX.Direct3D.DriverType.Warp;

    /// <summary>Off by request or after a failure; the views then show the drawing.</summary>
    public static bool IsOff => _offReason is not null || Requested() == Mode.Off;

    public static string? OffReason => _offReason ?? (Requested() == Mode.Off ? "turned off (--no-3d or BAMBUBAR_3D=off)" : null);

    /// <summary>The log so far (the snapshot writes it beside its images).</summary>
    public static IReadOnlyList<string> Log
    {
        get { lock (Gate) return Lines.ToList(); }
    }

    private enum Mode { Auto, Warp, Off }

    private static Mode? _requested;

    private static Mode Requested() => _requested ??= ReadRequest();

    private static Mode ReadRequest()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--no-3d", StringComparer.OrdinalIgnoreCase)) return Mode.Off;
        return Environment.GetEnvironmentVariable("BAMBUBAR_3D")?.Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "no" or "false" => Mode.Off,
            "warp" or "software" => Mode.Warp,
            _ => Mode.Auto,
        };
    }

    /// <summary>The shared device, created on first use; null when 3D is off.</summary>
    public static IEffectsManager? Effects
    {
        get
        {
            if (_tried) return _offReason is null ? _effects : null;
            _tried = true;
            var mode = Requested();
            Write($"Bambu Bar {typeof(Renderer3D).Assembly.GetName().Version} · {Environment.OSVersion.VersionString} · " +
                  $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · WPF render tier {RenderCapability.Tier >> 16}");
            if (mode == Mode.Off)
            {
                TurnOff("turned off (--no-3d or BAMBUBAR_3D=off)");
                return null;
            }
            // WPF composing in software (Remote Desktop, a basic display driver) copies the Direct3D picture without
            // its alpha: the printer would sit on a white box. The drawing is transparent, so it stays. BAMBUBAR_3D=warp
            // still forces 3D.
            if (mode == Mode.Auto && (RenderCapability.Tier >> 16 == 0 || SystemParameters.IsRemoteSession))
            {
                TurnOff(SystemParameters.IsRemoteSession ? "a Remote Desktop session (WPF draws in software)" : "WPF draws in software (render tier 0)");
                return null;
            }
            // Helix's own warnings (a D3D9 interop surface that failed, a lost device) go to the same log.
            HelixToolkit.Logger.LogManager.Factory = new LogFactory();
            if (mode == Mode.Auto) _effects = Create(software: false);
            _effects ??= Create(software: true);
            if (_effects is null)
            {
                TurnOff("neither the graphics card nor WARP could start Direct3D 11");
                return null;
            }
            Status = Describe(_effects);
            Write($"3D on: {Status}");
            return _effects;
        }
    }

    private static EffectsManager? Create(bool software)
    {
        try
        {
            return software
                ? new DefaultEffectsManager(new EffectsManagerConfiguration { EnableSoftwareRendering = true })
                : new DefaultEffectsManager();
        }
        catch (Exception e)
        {
            Write($"Direct3D 11 {(software ? "WARP" : "hardware")} failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static string Describe(EffectsManager effects)
    {
        var kind = effects.DriverType == SharpDX.Direct3D.DriverType.Warp ? "WARP (software)" : "graphics card";
        try
        {
            using var dxgi = effects.Device!.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgi.Adapter;
            return $"Direct3D 11, {kind}: {adapter.Description.Description.Trim()} (feature level {effects.Device.FeatureLevel})";
        }
        catch (Exception e) when (e is SharpDX.SharpDXException or NullReferenceException or InvalidCastException)
        {
            return $"Direct3D 11, {kind}";
        }
    }

    /// <summary>Turns 3D off for the rest of the run (logged); the views fall back to the drawing.</summary>
    public static void TurnOff(string reason)
    {
        if (_offReason is not null) return;
        _offReason = reason;
        Status = "off: " + reason;
        Write($"3D off, showing the drawing: {reason}");
        TurnedOff?.Invoke();
    }

    public static void Write(string line)
    {
        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
        Debug.WriteLine("[3D] " + line);
        lock (Gate)
        {
            if (Lines.Count < 400) Lines.Add(stamped);
            if (LogPath is not { } path) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // A fresh log each run; a runaway one stops growing at about 64 KB.
                if (Lines.Count == 1) File.WriteAllText(path, stamped + Environment.NewLine);
                else if (new FileInfo(path) is { Exists: false } or { Length: < 65536 }) File.AppendAllText(path, stamped + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging is best effort.
            }
        }
    }

    private static string? DefaultLogPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length == 0 ? null : Path.Combine(local, "Bambu Bar", "model3d.log");
    }

    /// <summary>HelixToolkit's logging, warnings and errors only, into <see cref="Write"/>.</summary>
    private sealed class LogFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var name = category[(category.LastIndexOf('.') + 1)..];
                Write($"Helix {logLevel} ({name}): {formatter(state, exception)}");
            }
        }
    }
}
