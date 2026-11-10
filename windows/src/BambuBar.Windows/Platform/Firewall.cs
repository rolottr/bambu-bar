using System.IO;
using System.Runtime.InteropServices;

namespace BambuBar.Platform;

/// <summary>
/// Whether Windows Firewall keeps the printers' announcements (inbound UDP 2021) away from this copy of Bambu Bar:
/// the Windows counterpart of macOS's Local Network permission (ledger §4 trap 5). Sends still succeed when inbound
/// traffic is blocked, so Core's own check can't see it; the store asks this through
/// <see cref="Core.App.PrinterStore.InboundBlocked"/>.
/// </summary>
/// <remarks>
/// Read through the <c>HNetCfg.FwPolicy2</c> COM object, late-bound. In each profile that's active now (domain, private,
/// public): the firewall is on, and either all inbound traffic is blocked, a block rule names this exe, or no enabled
/// allow rule lets UDP in for it (by program, or for port 2021). A third-party firewall, a stopped service or anything
/// else that fails reads as "not blocked": the notice only shows when Windows Firewall clearly says so.
/// </remarks>
public static class Firewall
{
    private const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;
    private const int DirectionIn = 1;
    private const int ActionBlock = 0, ActionAllow = 1;
    private const int ProtocolUdp = 17, ProtocolAny = 256;
    private const int DiscoveryPort = 2021;

    private static readonly object Gate = new();
    private static (DateTime At, bool Blocked)? _cached;
    private static bool _reading;

    /// <summary>The answer. Reading the rules takes a moment and the search asks from the UI thread too, so after the
    /// first read this answers from the last one and reads again in the background once it is 5 seconds old.</summary>
    public static bool InboundBlocked()
    {
        lock (Gate)
        {
            if (_cached is { } c)
            {
                if (DateTime.UtcNow - c.At >= TimeSpan.FromSeconds(5) && !_reading)
                {
                    _reading = true;
                    _ = Task.Run(Update);
                }
                return c.Blocked;
            }
        }
        return Update();
    }

    /// <summary>Reads the firewall now, in the background (at start, so the first search needn't wait).</summary>
    public static void Prime() => _ = Task.Run(Update);

    /// <summary>Forgets the last answer, so the next check reads the firewall again (after the person changed it).</summary>
    public static void Forget()
    {
        lock (Gate) _cached = null;
    }

    private static bool Update()
    {
        var blocked = Read();
        lock (Gate)
        {
            _cached = (DateTime.UtcNow, blocked);
            _reading = false;
        }
        return blocked;
    }

    private static bool Read()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return false;
        object? policy = null;
        try
        {
            if (Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false) is not { } type) return false;
            policy = Activator.CreateInstance(type);
            if (policy is null) return false;
            dynamic fw = policy;
            int current = fw.CurrentProfileTypes;
            var profiles = new[] { ProfileDomain, ProfilePrivate, ProfilePublic }.Where(p => (current & p) != 0).ToList();
            if (profiles.Count == 0) return false;
            var guarded = profiles.Where(p => (bool)fw.FirewallEnabled[p]).ToList();
            if (guarded.Count == 0) return false;
            if (guarded.Any(p => (bool)fw.BlockAllInboundTraffic[p])) return true;

            var self = Normalize(exe);
            var allowed = 0;
            List<Rule> rules = Rules(fw.Rules);
            foreach (var rule in rules)
            {
                if (rule.Direction != DirectionIn || !rule.Enabled) continue;
                if (rule.Protocol is not (ProtocolUdp or ProtocolAny)) continue;
                var mine = rule.Application is { } app && Normalize(app) == self;
                var forPort = rule.Application is null && rule.Ports.Split(',').Any(p => Covers(p.Trim(), DiscoveryPort));
                if (!mine && !forPort) continue;
                if (mine && rule.Action == ActionBlock && guarded.Any(p => (rule.Profiles & p) != 0)) return true;
                if (rule.Action == ActionAllow) allowed |= rule.Profiles;
            }
            // Every active profile needs an allow rule, unless that profile lets inbound traffic in by default.
            return guarded.Any(p => (allowed & p) == 0 && (int)fw.DefaultInboundAction[p] == ActionBlock);
        }
        catch (Exception)
        {
            // COM missing, access denied, a rule with an odd shape, a third-party firewall in charge: not knowable.
            return false;
        }
        finally
        {
            if (policy is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    private sealed record Rule(int Direction, bool Enabled, int Protocol, string? Application, string Ports, int Action, int Profiles);

    /// <summary>The rules as plain values; a rule that can't be read is skipped.</summary>
    private static List<Rule> Rules(dynamic rules)
    {
        var list = new List<Rule>();
        foreach (dynamic rule in rules)
        {
            try
            {
                string? app = rule.ApplicationName;
                string? ports = rule.LocalPorts;
                list.Add(new Rule((int)rule.Direction, (bool)rule.Enabled, (int)rule.Protocol, string.IsNullOrWhiteSpace(app) ? null : app,
                    ports ?? "*", (int)rule.Action, (int)rule.Profiles));
            }
            catch (Exception)
            {
                // Skip it.
            }
        }
        return list;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).ToUpperInvariant(); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path.ToUpperInvariant(); }
    }

    /// <summary>"*", "2021" or "2000-2100".</summary>
    private static bool Covers(string ports, int port)
    {
        if (ports == "*") return true;
        var dash = ports.IndexOf('-');
        if (dash < 0) return int.TryParse(ports, out var single) && single == port;
        return int.TryParse(ports[..dash], out var low) && int.TryParse(ports[(dash + 1)..], out var high) && port >= low && port <= high;
    }

    /// <summary>Where the person allows an app: Control Panel's "Allowed apps" list (Windows Security's firewall
    /// page as a fallback; Settings has no firewall page of its own).</summary>
    public static void OpenAllowedApps()
    {
        foreach (var (file, args) in new[]
        {
            ("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps"),
            ("windowsdefender://network", ""),
        })
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose();
                return;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                // Try the next.
            }
        }
    }
}
