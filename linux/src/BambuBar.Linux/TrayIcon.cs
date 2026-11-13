using BambuBar.Core.App;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

/// <summary>The same look selection as macOS MenuBarLabel; artwork is generated from MenuBarIcon's geometry.</summary>
internal static class TrayIcon
{
    public const string Prefix = "dev.rolottr.BambuBar-tray-";

    public static string Key(PrinterSession? session, bool configured)
        => Key(session?.Status, session?.IsOnline == true, configured);

    internal static string Key(PrinterStatus? status, bool online, bool configured)
    {
        if (configured && !online) return "offline";
        if (!online) status = null;
        var state = status?.Canceled == true ? JobState.Idle : status?.State;
        var step = (int)Math.Round(Math.Clamp(status?.Progress ?? 0, 0, 100) / 5.0, MidpointRounding.AwayFromZero);
        var key = state switch
        {
            JobState.Printing or JobState.Preparing => "printing-" + step,
            JobState.Paused => "paused",
            JobState.Failed => "failed",
            JobState.Finished => "finished",
            _ => "idle",
        };
        if (state != JobState.Failed && status?.MostSevereIssue?.Severity <= IssueSeverity.Serious) key += "-alert";
        return key;
    }

    public static string Name(string key) => Prefix + key + "-symbolic";
}
