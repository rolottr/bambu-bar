using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.App;

/// <summary>
/// <c>--probe &lt;host&gt; &lt;serial&gt; &lt;access-code&gt; [seconds]</c>: connects like the app does, prints what happens and
/// a summary of the first full report (and every tenth), then exits. Add <c>--insecure-tls</c> for the emulators,
/// <c>--raw-filament</c> for the AMS and external spool fields as sent, <c>--raw-all</c> for the whole first report.
/// The summary is the Mac's, line for line, so the two can be compared on the same printer.
/// </summary>
public static class Probe
{
    public sealed record Arguments(string Host, string Serial, string Code, double Seconds, bool Insecure, bool RawAll, bool RawFilament);

    /// <param name="codeFor">Looks up a saved code when the code given is "keychain" (as on the Mac).</param>
    public static Arguments? Parse(IReadOnlyList<string> args, Func<string, string?>? codeFor = null)
    {
        var i = args.ToList().IndexOf("--probe");
        if (i < 0 || i + 3 >= args.Count) return null;
        var seconds = i + 4 < args.Count && double.TryParse(args[i + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 20;
        var code = args[i + 3] == "keychain" ? codeFor?.Invoke(args[i + 2]) ?? "" : args[i + 3];
        return new(args[i + 1], args[i + 2], code, seconds, args.Contains("--insecure-tls"), args.Contains("--raw-all"), args.Contains("--raw-filament"));
    }

    /// <summary>Runs the probe; returns the exit code (0 when at least one report arrived).</summary>
    public static async Task<int> RunAsync(Arguments a, TextWriter output)
    {
        using var context = new SerialContext("Probe");
        var clock = Stopwatch.StartNew();
        void Log(string text)
        {
            lock (output)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds,5:0.0}s] ") + text);
                output.Flush();
            }
        }
        var reports = 0;
        var connection = context.Invoke(() => new PrinterConnection());
        context.Send(_ =>
        {
            connection.OnPhase = phase =>
            {
                Log($"phase: {Describe(phase)}");
                if (phase is not PrinterConnection.Phase.Connecting && BambuTrust.LastVerdict is { } verdict)
                    Log($"certificate: {(verdict is BambuTrust.Verdict.Rejected r ? $"rejected(\"{r.Reason}\")" : "trusted")}");
            };
            connection.OnAck = (command, ok, reason) => Log($"ack: {command} {(ok ? "ok" : "failed")} {reason ?? ""}");
            connection.OnStatus = (status, doc) =>
            {
                reports++;
                if (reports == 1 || reports % 10 == 0) Log($"status #{reports}: {Summary(status)}");
                if (reports == 1 && a.RawAll) Log("raw report:\n" + Pretty(doc.Print));
                if (reports == 1 && a.RawFilament)
                {
                    var raw = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var key in new[] { "ams", "vt_tray", "vir_slot" }) raw[key] = doc.Print.GetValueOrDefault(key);
                    Log("raw filament:\n" + Pretty(raw));
                }
            };
            Log($"connecting to {a.Host} as {a.Serial}");
            connection.Start(new PrinterConfig { Host = a.Host, Serial = a.Serial, Name = a.Serial }, a.Code, a.Insecure);
        }, null);
        await Task.Delay(TimeSpan.FromSeconds(a.Seconds)).ConfigureAwait(false);
        Log($"done: {reports} status updates");
        context.Send(_ => connection.Dispose(), null);
        return reports > 0 ? 0 : 1;
    }

    private static string Pretty(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>A phase as Swift prints the enum: <c>online</c>, <c>offline("reason")</c>.</summary>
    public static string Describe(PrinterConnection.Phase phase) => phase switch
    {
        PrinterConnection.Phase.Idle => "idle",
        PrinterConnection.Phase.Connecting => "connecting",
        PrinterConnection.Phase.Online => "online",
        PrinterConnection.Phase.Offline o => $"offline(\"{o.Reason}\")",
        PrinterConnection.Phase.Unauthorized => "unauthorized",
        PrinterConnection.Phase.Certificate c => $"certificate(\"{c.Reason}\")",
        _ => phase.ToString(),
    };

    /// <summary>The Mac probe's summary of a status, character for character.</summary>
    public static string Summary(PrinterStatus s)
    {
        static string D(double v) => SwiftDouble(v);
        var lines = new List<string>
        {
            $"{s.State.Title()}, stage {(s.StageId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "-")} ({s.StageText ?? "-"})",
            $"job \"{s.JobName}\" {s.Progress}% layer {s.Layer}/{s.TotalLayers}, {s.RemainingMinutes} min left",
            $"nozzle {D(s.Nozzle.Current)}/{D(s.Nozzle.Target)}"
                + (s.SecondNozzle is { } left ? $" left {D(left.Current)}/{D(left.Target)}" : "")
                + (s.LeftNozzleInUse is { } inUse ? inUse ? " (left in use)" : " (right in use)" : "")
                + $" bed {D(s.Bed.Current)}/{D(s.Bed.Target)} chamber {(s.Chamber is { } c ? D(c) : "-")}",
            $"fans part {s.PartFan ?? -1}% aux {s.AuxFan ?? -1}% exhaust {s.ExhaustFan ?? -1}%  speed {s.Speed.Title()}",
            $"light {(s.ChamberLightOn is { } l ? l ? "on" : "off" : "-")} door {(s.DoorOpen is { } d ? d ? "open" : "closed" : "-")} wifi {s.WifiSignal ?? 0} dBm",
            $"controls locked: {(s.ControlsLocked ? "true" : "false")}  firmware {s.Firmware ?? "-"}  nozzle {D(s.NozzleDiameter ?? 0)} {s.NozzleType ?? "-"}",
            $"LAN Only Liveview: {(s.LiveViewEnabled is { } lv ? lv ? "on" : "off" : "not reported")}",
        };
        foreach (var unit in s.Ams)
        {
            var spools = unit.Spools.Select(sp => $"{sp.Label} " + (sp.IsLoaded
                ? $"{sp.Brand ?? "?"} {sp.DisplayName} {sp.ColorName ?? "#" + sp.Color[..Math.Min(6, sp.Color.Length)]} {sp.RemainText}"
                : "empty"));
            lines.Add($"{unit.Title} {(unit.Humidity is { } h ? $"{h}%RH" : "")} {(unit.Temperature is { } t ? $"{D(t)}°" : "")}: " + string.Join(", ", spools));
        }
        if (s.ExternalSpool is { } ext)
        {
            lines.Add("external: " + (ext.IsLoaded
                ? $"{ext.Brand ?? "?"} {ext.DisplayName} {ext.ColorName ?? "#" + ext.Color[..Math.Min(6, ext.Color.Length)]}"
                : "empty"));
        }
        lines.Add($"active spool: {(s.ActiveSpoolId is { } a ? a.ToString(CultureInfo.InvariantCulture) : "-")}");
        foreach (var issue in s.Issues) lines.Add($"issue {issue.Id} [{issue.Severity.Title()}]: {issue.Message}");
        return string.Join("\n         ", lines);
    }

    /// <summary>A Double as Swift interpolates it: "220.0", "38.4", "0.1".</summary>
    public static string SwiftDouble(double v) =>
        double.IsFinite(v) && Math.Floor(v) == v && Math.Abs(v) < 1e16
            ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("R", CultureInfo.InvariantCulture);
}
