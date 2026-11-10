using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.TrayPanel;

/// <summary>
/// Texts and orderings the Mac panel computes in its views (MenuPanel.swift's WiFi, FleetPage.swift's fleet order and
/// summary, Theme.swift's relativeTime). Core has no home for them yet; the window's All Printers page (P3.5) needs the
/// fleet ones too, so they belong in Core's PrinterStore/PrinterSession then.
/// </summary>
public static class PanelText
{
    /// <summary>The Mac's WiFi.describe: signal strength in words; −90 dBm is how the printers report Ethernet.</summary>
    public static string WiFi(int dbm) => dbm switch
    {
        >= -55 and <= 0 => Loc.T("Wi-Fi excellent"),
        >= -67 and < -55 => Loc.T("Wi-Fi good"),
        >= -75 and < -67 => Loc.T("Wi-Fi fair"),
        > -90 and < -75 => Loc.T("Wi-Fi weak"),
        -90 => Loc.T("Ethernet"),
        _ => Loc.T("Wi-Fi poor"),
    };

    /// <summary>"Just now", "5m ago", "3h 12m ago". The Mac uses RelativeDateTimeFormatter ("5 min. ago"); .NET has
    /// none, so this is the widgets' wording (WidgetState.Ago).</summary>
    public static string Ago(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var minutes = (int)((now ?? DateTimeOffset.Now) - date).TotalMinutes;
        return minutes < 1 ? Loc.T("Just now") : Loc.F("%@ ago", Format.Duration(minutes));
    }

    /// <summary>The panel's header line: model, then Wi-Fi or the link state.</summary>
    public static string LinkLine(PrinterStore store)
    {
        if (store.Current is not { } current) return Loc.T("No printer set up");
        var parts = new List<string> { current.Config.ModelName };
        if (current.Phase is PrinterConnection.Phase.Online)
            parts.Add(current.Status?.WifiSignal is { } wifi ? WiFi(wifi) : Loc.T("Connected"));
        else parts.Add(current.LinkSummary);
        return string.Join(" · ", parts);
    }

    // MARK: Fleet (FleetPage.swift)

    /// <summary>Where the printer sorts in the fleet: what needs a person first, then what finishes soonest.</summary>
    public static (int Group, int Minutes) AttentionRank(PrinterSession session)
    {
        if (session.Status is not { } s || !session.IsOnline)
            return session.Phase is PrinterConnection.Phase.Connecting ? (5, 0) : (6, 0);
        if (s.State == JobState.Failed && !s.Canceled) return (0, 0);
        if (s.State == JobState.Paused || s.MostSevereIssue is { Severity: <= IssueSeverity.Serious }) return (1, 0);
        return s.State switch
        {
            JobState.Printing or JobState.Preparing => (2, s.RemainingMinutes),
            JobState.Finished => (3, 0),
            _ => (4, 0),
        };
    }

    public static bool NeedsAttention(PrinterSession session) => AttentionRank(session).Group <= 1;

    /// <summary>Every printer, the ones that need someone first, then by soonest finish, then by name.</summary>
    public static List<PrinterSession> FleetOrder(PrinterStore store) =>
        [.. store.Sessions.OrderBy(AttentionRank).ThenBy(s => s.Config.Name, NaturalComparer.Instance)];

    /// <summary>"6 printing · 1 paused · 2 finished · 1 offline".</summary>
    public static string FleetSummary(PrinterStore store)
    {
        var sessions = store.Sessions;
        var parts = new List<string>();
        void Add(Func<int, string> title, Func<PrinterSession, bool> match)
        {
            var n = sessions.Count(match);
            if (n > 0) parts.Add(title(n));
        }
        bool Online(PrinterSession s, params JobState[] states) => s.IsOnline && s.Status is { } st && states.Contains(st.State);
        Add(n => Loc.P("%lld need attention", n), s => AttentionRank(s).Group == 0);
        Add(n => Loc.P("%lld printing", n), s => Online(s, JobState.Printing, JobState.Preparing));
        Add(n => Loc.P("%lld paused", n), s => Online(s, JobState.Paused));
        Add(n => Loc.P("%lld finished", n), s => Online(s, JobState.Finished));
        Add(n => Loc.P("%lld ready", n), s => Online(s, JobState.Idle));
        Add(n => Loc.F("%lld offline", n), s => !s.IsOnline);
        return string.Join(" · ", parts);
    }

    /// <summary>The line under a printer's name: what went wrong, else the job, else the model.</summary>
    public static string FleetDetail(PrinterSession session)
    {
        if (session.Status is not { } s || !session.IsOnline) return $"{session.Config.ModelName} · {session.LinkSummary}";
        if (s.MostSevereIssue is { Severity: <= IssueSeverity.Serious } issue) return issue.Message;
        if (s.JobName.Length > 0 && s.State != JobState.Idle) return s.JobName;
        return $"{session.Config.ModelName} · {s.StateTitle}";
    }

    /// <summary>The state chip's text: the state, or the link while there's no live status.</summary>
    public static string ChipText(PrinterSession session) =>
        session.Status is { } s && session.IsOnline ? s.StateTitle
        : session.Phase is PrinterConnection.Phase.Connecting ? Loc.T("Connecting") : Loc.T("Offline");

    /// <summary>Time left for a fleet row: "1h 02m · 14:32", "45m left · paused", "Done".</summary>
    public static string FleetTiming(PrinterStatus s)
    {
        switch (s.State)
        {
            case JobState.Finished:
                return Loc.T("Done", "A finished print");
            case JobState.Paused:
                return Loc.F("%@ left · paused", Format.Duration(s.RemainingMinutes));
            default:
                if (s.EstimatedEnd is not { } end) return s.StageText ?? "";
                var left = Format.Duration(s.RemainingMinutes);
                // The finish time only when it's today; "tomorrow 06:01" doesn't fit beside the percentage.
                return end.ToLocalTime().Date == DateTimeOffset.Now.Date ? $"{left} · {Format.Finish(end)}" : Loc.F("%@ left", left);
        }
    }
}
