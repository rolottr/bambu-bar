using System.Text.Json;
using System.Text.Json.Serialization;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Core.Widgets;

/// <summary>
/// What one widget shows, ready to draw: the state file the Rainmeter skins read (contract v1,
/// windows/docs/widget-state.md). Skins are only the view: every text is translated and every rule (which line, which
/// colour) applied here, by the Mac's WidgetState rules. Lua only moves the countdown and progress between writes.
/// </summary>
public sealed record WidgetState
{
    public int V { get; init; } = 1;
    /// <summary>Unix seconds when written; online but older than 30 minutes, the skin shows the printer offline.</summary>
    public long SavedAt { get; init; }
    public string Serial { get; init; } = "";
    public string Name { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>What a click opens: the printer's page, or the app when no printer is set up.</summary>
    public string Url { get; init; } = "bambubar://open";
    /// <summary>"online", "connecting", "offline", "unauthorized", "unconfigured".</summary>
    public string Link { get; init; } = "unconfigured";
    /// <summary>A live status is on hand (online, or reconnecting with one).</summary>
    public bool Live { get; init; }
    /// <summary>"idle", "preparing", "printing", "paused", "finished", "failed" (canceled is failed + canceled).</summary>
    public string State { get; init; } = "idle";
    public bool Canceled { get; init; }
    /// <summary>The caption: "Printing", "Canceled", "Offline"…</summary>
    public string Caption { get; init; } = "";
    public string StateColor { get; init; } = "";
    /// <summary>The job, or what's going on when there isn't one.</summary>
    public string Headline { get; init; } = "";
    /// <summary>The line under it at write time ("1h 02m left", "Last: Benchy"); Lua rewrites it while printing.</summary>
    public string StateLine { get; init; } = "";
    /// <summary>The small widget's second line ("Done 14:32 · 170/305", "Nozzle 172° → 220°").</summary>
    public string Detail { get; init; } = "";
    /// <summary>Why it paused, short ("Filament ran out"), or the stage while preparing.</summary>
    public string? Reason { get; init; }
    /// <summary>The Large widget's info box when not live ("Enter the access code again in the app").</summary>
    public string? Info { get; init; }
    /// <summary>When the print ends, as a time ("5:15 PM", "tomorrow 08:10"); <c>strings.done</c> wraps it.</summary>
    public string? Finish { get; init; }
    /// <summary>What to show when the file is older than 30 minutes while online (the app has stopped writing).</summary>
    public OfflineView Offline { get; init; } = new("", "", "", "", "");
    /// <summary>Progress bar and ring colour: the active filament when it reads on dark glass, else state colours.</summary>
    public string Tint { get; init; } = "";

    // Raw numbers for Lua (the Mac's projectedProgress and minutesLeft).
    public int Progress { get; init; }
    public long? UpdatedAt { get; init; }
    public long? EndsAt { get; init; }
    public int RemainingMinutes { get; init; }
    public string? Layers { get; init; }
    public int Layer { get; init; }
    public int TotalLayers { get; init; }

    public IReadOnlyList<Temp> Temps { get; init; } = [];
    public AmsHeader? Ams { get; init; }
    public IReadOnlyList<SpoolView> Spools { get; init; } = [];
    public string? ActiveSpoolLine { get; init; }
    public IssueView? Issue { get; init; }
    public LastJobView? LastJob { get; init; }
    /// <summary>The printer art for this state, a file in the same folder (content-hashed name).</summary>
    public string? Art { get; init; }
    /// <summary>The same scene with the chamber light off, so the skin can crossfade the light switching (null when the
    /// light is already off or the printer has none).</summary>
    public string? ArtUnlit { get; init; }
    /// <summary>False when Windows' animation effects are off: skins skip every tween.</summary>
    public bool Motion { get; init; } = true;
    public StringsView Strings { get; init; } = new();

    public sealed record Temp(string Label, string Value, string? Target, string Color, string Kind);
    public sealed record AmsHeader(string Title, string? Humidity);
    public sealed record SpoolView(string Label, string Color, IReadOnlyList<string> Colors, string Name, string Amount,
        double? Fraction, bool Active, bool Empty, string Tooltip);
    public sealed record IssueView(string Text, string Severity);
    public sealed record OfflineView(string Caption, string Headline, string StateLine, string Detail, string Info);
    public sealed record LastJobView(string Name, string Outcome, string Ago, string? Took);

    /// <summary>Patterns for the countdown in the app's language. <c>{0}</c> and <c>{1}</c> are filled by Lua.</summary>
    public sealed record StringsView
    {
        public string LessThanMinute { get; init; } = "";
        public string Minutes { get; init; } = "";
        public string HoursMinutes { get; init; } = "";
        public string DaysHours { get; init; } = "";
        public string Left { get; init; } = "";
        public string Done { get; init; } = "";
        /// <summary>The word under the Small widget's countdown ("left"); empty where the language has none.</summary>
        public string LeftWord { get; init; } = "";
        public string Last { get; init; } = "";
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}

/// <summary>The Mac's widget palette (Palette in Formatting.swift) as hex.</summary>
public static class WidgetPalette
{
    public const string Accent = "#21CC6B";
    public const string Preparing = "#5CA8FF";
    public const string Paused = "#FFB83D";
    public const string Failed = "#FF5C5C";
    public const string Finished = "#6BDB9E";
    public const string Idle = "#9EA8B3";
    public const string Secondary = "#8E8E93";
    public const string Nozzle = "#FF7A4D";
    public const string Bed = "#FFB84D";
    public const string Chamber = "#8CB3FF";

    public static string For(JobState state) => state switch
    {
        JobState.Preparing => Preparing,
        JobState.Printing => Accent,
        JobState.Paused => Paused,
        JobState.Finished => Finished,
        JobState.Failed => Failed,
        _ => Idle,
    };
}

/// <summary>Builds <see cref="WidgetState"/>s from the store's snapshots and writes them for the skins.</summary>
public sealed class WidgetStateWriter(string folder, Func<bool>? motion = null) : IWidgetSink
{
    /// <summary>The printer art's size: twice the Large widget's art slot (98 × 116), for 200 % displays.</summary>
    public const int ArtWidth = 196, ArtHeight = 232;

    public string Folder { get; } = folder;

    /// <summary>Called after each write that should redraw the widgets now (the app sends Rainmeter a !Refresh).</summary>
    public Action? Reload { get; set; }

    public void Write(FleetSnapshot fleet, bool reload)
    {
        Directory.CreateDirectory(Folder);
        var arts = new List<string>();
        var now = DateTimeOffset.Now;
        var motionOn = motion?.Invoke() ?? true;
        void Save(string file, WidgetState state)
        {
            if (state.Art is { } art) arts.Add(art);
            if (state.ArtUnlit is { } unlit) arts.Add(unlit);
            FileWrite.Atomic(Path.Combine(Folder, file), JsonSerializer.Serialize(state, WidgetState.Json));
        }
        foreach (var printer in fleet.Printers) Save($"{printer.Serial}.json", Build(printer, now, Folder, motionOn));
        // Widgets without a printer of their own follow the tray's.
        Save("default.json", Build(fleet.For(null), now, Folder, motionOn));
        PrinterArt.Prune(Folder, arts);
        foreach (var stale in Directory.GetFiles(Folder, "*.json"))
        {
            var serial = Path.GetFileNameWithoutExtension(stale);
            if (serial != "default" && fleet.Printers.All(p => p.Serial != serial)) File.Delete(stale);
        }
        if (reload) Reload?.Invoke();
    }

    /// <summary>One printer's state at <paramref name="now"/>. <paramref name="artFolder"/> null skips the art.</summary>
    public static WidgetState Build(PrinterSnapshot snapshot, DateTimeOffset now, string? artFolder = null, bool motion = true)
    {
        // The Mac's WidgetState: a status counts only while online or reconnecting.
        var status = snapshot.Link is PrinterSnapshot.LinkState.Online or PrinterSnapshot.LinkState.Connecting ? snapshot.Status : null;
        var state = status?.State ?? JobState.Idle;
        var link = snapshot.Link switch
        {
            PrinterSnapshot.LinkState.Online => "online",
            PrinterSnapshot.LinkState.Connecting => "connecting",
            PrinterSnapshot.LinkState.Offline => "offline",
            PrinterSnapshot.LinkState.Unauthorized => "unauthorized",
            _ => "unconfigured",
        };
        int? minutesLeft = status is { } st && st.EstimatedEnd is { } end && st.State.IsActive()
            ? st.State == JobState.Paused ? st.RemainingMinutes : Math.Max(0, (int)Math.Ceiling((end - now).TotalMinutes))
            : null;
        string? finish = minutesLeft is { } left && state == JobState.Printing ? Format.Finish(now.AddMinutes(left), now) : null;
        string? lastJobLine = snapshot.LastJob is { } last ? Loc.F("Last: %@", last.Name) : null;

        var caption = status is not null ? status.StateTitle
            : snapshot.Link switch
            {
                PrinterSnapshot.LinkState.Unconfigured => Loc.T("Set up Bambu Bar"),
                PrinterSnapshot.LinkState.Unauthorized => Loc.T("Access code rejected"),
                PrinterSnapshot.LinkState.Connecting => Loc.T("Connecting…"),
                _ => Loc.T("Offline"),
            };
        var headline = snapshot.Link switch
        {
            PrinterSnapshot.LinkState.Unconfigured => Loc.T("Set up Bambu Bar"),
            PrinterSnapshot.LinkState.Offline => Loc.T("Offline"),
            PrinterSnapshot.LinkState.Unauthorized => Loc.T("Access code rejected"),
            PrinterSnapshot.LinkState.Connecting when status is null => Loc.T("Connecting…"),
            _ when status is null => Loc.T("Offline"),
            _ when status!.State == JobState.Idle => status.State.Title(),
            _ => status!.JobName.Length == 0 ? status.State.Title() : status.JobName,
        };
        var stateLine = status is null
            ? snapshot.Link switch
            {
                PrinterSnapshot.LinkState.Unconfigured => Loc.T("Open the app to connect your printer"),
                PrinterSnapshot.LinkState.Unauthorized => Loc.T("Enter the access code again in the app"),
                _ => lastJobLine ?? Loc.T("Waiting for the printer"),
            }
            : status.State == JobState.Printing && minutesLeft is { } l ? Loc.F("%@ left", Format.Duration(l))
            : status.State == JobState.Idle ? lastJobLine ?? Loc.T("Ready to print")
            : status.DetailText;

        string? layers = status is { TotalLayers: > 0 } s0 ? $"{s0.Layer}/{s0.TotalLayers}" : null;
        var notLiveDetail = snapshot.Link switch
        {
            PrinterSnapshot.LinkState.Unconfigured => Loc.T("Open the app"),
            PrinterSnapshot.LinkState.Unauthorized => Loc.T("Access code rejected"),
            _ => Loc.T("No connection"),
        };
        var info = status is not null ? null : snapshot.Link switch
        {
            PrinterSnapshot.LinkState.Unconfigured => Loc.T("Open the app to connect your printer"),
            PrinterSnapshot.LinkState.Unauthorized => Loc.T("Enter the access code again in the app"),
            _ => Loc.T("Waiting for the printer"),
        };
        var detail = status is null ? notLiveDetail : status.State switch
        {
            JobState.Printing => string.Join(" · ", new[] { finish is null ? null : Loc.F("Done %@", finish), layers }.OfType<string>()),
            JobState.Preparing => status.ActiveNozzle.IsHeating
                ? $"{status.ActiveNozzleTitle} {(int)status.ActiveNozzle.Current}° → {(int)status.ActiveNozzle.Target}°"
                : Loc.T("Starting soon"),
            JobState.Paused => string.Join(" · ", new[] { layers is null ? null : Loc.F("Layer %@", layers), $"{status.Progress}%" }.OfType<string>()),
            JobState.Finished => Loc.T("Ready to remove") + (layers is null ? "" : $" · {layers}"),
            JobState.Failed => status.Canceled ? status.StateTitle : status.Issues.FirstOrDefault()?.Message ?? Loc.T("Print failed"),
            _ => "",
        };
        var reason = status?.State switch
        {
            JobState.Paused => status.PauseReason ?? Loc.T("Waiting", "A paused print, reason unknown"),
            JobState.Preparing => status.StageText ?? Loc.T("Getting ready"),
            _ => null,
        };

        var temps = new List<WidgetState.Temp>();
        if (status is not null)
        {
            static string Deg(double v) => Format.Temperature(v);
            static string? Target(double t) => t > 0 ? Deg(t) : null;
            temps.Add(new(status.ActiveNozzleTitle, Deg(status.ActiveNozzle.Current), Target(status.ActiveNozzle.Target), WidgetPalette.Nozzle, "nozzle"));
            temps.Add(new(Loc.T("Bed"), Deg(status.Bed.Current), Target(status.Bed.Target), WidgetPalette.Bed, "bed"));
            if (status.Chamber is { } chamber)
                temps.Add(new(Loc.T("Chamber"), Deg(chamber), status.ChamberTarget is { } ct ? Target(ct) : null, WidgetPalette.Chamber, "chamber"));
            else if (status.IdleNozzle is { } other)
                temps.Add(new(status.IdleNozzleTitle, Deg(other.Current), Target(other.Target), WidgetPalette.Nozzle, "nozzle"));
            else if (status.PartFan is { } fan)
                temps.Add(new(Loc.T("Part fan"), $"{fan}%", null, WidgetPalette.Chamber, "fan"));
        }

        var unit = status?.Ams.FirstOrDefault();
        var spools = (unit?.Spools ?? []).Select(sp => new WidgetState.SpoolView(
            sp.Label, Hex(sp.Color), sp.Colors.Select(Hex).ToList(),
            sp.IsLoaded ? sp.Material : Loc.T("Empty", "Filament slot"),
            sp.IsLoaded ? sp.AmountText : sp.Label,
            sp.IsLoaded && sp.Remain is { } r ? r / 100.0 : null,
            status!.ActiveSpoolId == sp.Id, !sp.IsLoaded,
            sp.IsLoaded ? $"{sp.Brand ?? ""} {sp.DisplayName} {sp.ColorName ?? ""} · {sp.RemainDetail}".Trim() : sp.RemainText)).ToList();

        var active = status?.ActiveSpool;
        var issue = status?.MostSevereIssue;
        var lastJob = snapshot.LastJob is { } job
            ? new WidgetState.LastJobView(job.Name, job.Outcome.ToString().ToLowerInvariant(), Ago(job.EndedAt ?? job.StartedAt, now),
                job.Duration is { } d ? Loc.F("took %@", Format.Duration(d)) : null)
            : null;

        string? art = null, artUnlit = null;
        if (artFolder is not null)
        {
            var scene = new PrinterArt.Scene
            {
                Shape = PrinterShapes.FromModel(snapshot.Model),
                State = state,
                Progress = status?.ProjectedProgress(now) ?? 0,
                Filament = active?.Color,
                Spools = (unit?.Spools ?? []).Select(sp => sp.IsLoaded ? sp.Color : null).ToList(),
                LightOn = status?.ChamberLightOn ?? false,
                Dimmed = status is null,
            };
            art = PrinterArt.WritePng(scene, ArtWidth, ArtHeight, artFolder);
            if (scene.LightOn && !scene.Dimmed) artUnlit = PrinterArt.WritePng(scene with { LightOn = false }, ArtWidth, ArtHeight, artFolder);
        }

        return new WidgetState
        {
            SavedAt = now.ToUnixTimeSeconds(), Serial = snapshot.Serial, Name = snapshot.PrinterName, Model = snapshot.Model,
            Url = snapshot.Serial.Length == 0 ? "bambubar://open" : $"bambubar://printer/{snapshot.Serial}",
            Info = info, Finish = finish,
            Offline = new(Loc.T("Offline"), Loc.T("Offline"), lastJobLine ?? Loc.T("Waiting for the printer"), Loc.T("No connection"),
                Loc.T("Waiting for the printer")),
            Link = link, Live = status is not null, State = state.ToString().ToLowerInvariant(), Canceled = status?.Canceled ?? false,
            Caption = caption,
            StateColor = snapshot.Link == PrinterSnapshot.LinkState.Unauthorized ? WidgetPalette.Paused
                : status is { State: JobState.Failed, Canceled: true } ? WidgetPalette.Idle : WidgetPalette.For(state),
            Headline = headline, StateLine = stateLine, Detail = detail, Reason = reason, Tint = Tint(status),
            Progress = status?.Progress ?? 0, UpdatedAt = status?.UpdatedAt.ToUnixTimeSeconds(),
            EndsAt = status?.EstimatedEnd?.ToUnixTimeSeconds(), RemainingMinutes = status?.RemainingMinutes ?? 0,
            Layers = layers, Layer = status?.Layer ?? 0, TotalLayers = status?.TotalLayers ?? 0,
            Temps = temps,
            Ams = unit is null ? null : new(unit.Title, unit.Humidity is { } h ? $"{h}%" : null),
            Spools = spools,
            ActiveSpoolLine = active is { IsLoaded: true } ? $"{active.DisplayName} · {active.SlotName}" : null,
            Issue = issue is null ? null : new(issue.Message, issue.Severity <= IssueSeverity.Serious ? "serious" : "common"),
            LastJob = lastJob, Art = art, ArtUnlit = artUnlit, Motion = motion,
            Strings = new()
            {
                LessThanMinute = Loc.T("<1m", "Less than a minute, short"),
                Minutes = Loc.Apply(Loc.T("%lldm"), "{0}"),
                HoursMinutes = Loc.Apply(Loc.T("%lldh %@m"), "{0}", "{1}"),
                DaysHours = Loc.Apply(Loc.T("%lldd %lldh"), "{0}", "{1}"),
                Left = Loc.Apply(Loc.T("%@ left"), "{0}"),
                Done = Loc.Apply(Loc.T("Done %@"), "{0}"),
                LeftWord = Loc.T("left"),
                Last = Loc.Apply(Loc.T("Last: %@"), "{0}"),
            },
        };
    }

    /// <summary>The bar's colour (the Mac's WidgetState.tint).</summary>
    private static string Tint(PrinterStatus? status)
    {
        if (status?.State == JobState.Paused) return WidgetPalette.Paused;
        if (status?.State == JobState.Failed) return status.Canceled ? WidgetPalette.Secondary : WidgetPalette.Failed;
        if (status?.ActiveSpool is { } spool && PrinterArt.Color(spool.Color) is { } c)
        {
            var l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (l is < 0.88 and > 0.12) return Hex(spool.Color);
        }
        return WidgetPalette.Accent;
    }

    /// <summary>"Just now", "2h 05m ago".</summary>
    public static string Ago(DateTimeOffset date, DateTimeOffset now)
    {
        var minutes = (int)((now - date).TotalMinutes);
        return minutes < 1 ? Loc.T("Just now") : Loc.F("%@ ago", Format.Duration(minutes));
    }

    /// <summary><c>RRGGBBAA</c> → <c>#RRGGBB</c> (Rainmeter takes the alpha separately).</summary>
    private static string Hex(string bambu) => bambu.Length >= 6 ? "#" + bambu[..6].ToUpperInvariant() : "#000000";
}
