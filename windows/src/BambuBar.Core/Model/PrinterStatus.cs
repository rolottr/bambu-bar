using System.Text.Json.Serialization;

namespace BambuBar.Core.Model;

/// <summary>What the printer is doing, folded from <c>print.gcode_state</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { Idle, Preparing, Printing, Paused, Finished, Failed }

/// <summary>The four motion profiles (<c>spd_lvl</c>).</summary>
public enum SpeedLevel { Silent = 1, Standard, Sport, Ludicrous }

[JsonConverter(typeof(JsonStringEnumConverter<AmsKind>))]
public enum AmsKind
{
    Ams, Lite, Pro2, Ht,
    /// <summary>The H3C's 9-slot AMS, wired to its hotend rack (Bambu Studio's ZMULTI_A).</summary>
    Ams3H,
    /// <summary>The other one, for rack B (ZMULTI_B), without a feeder motor; known only from Bambu Studio's source.</summary>
    AmsB,
}

public enum IssueSeverity { Fatal = 1, Serious, Common, Info }

public static class StatusText
{
    public static JobState FromGcodeState(string gcodeState) => gcodeState.ToUpperInvariant() switch
    {
        "PREPARE" or "SLICING" => JobState.Preparing,
        "RUNNING" => JobState.Printing,
        "PAUSE" => JobState.Paused,
        "FINISH" => JobState.Finished,
        "FAILED" => JobState.Failed,
        _ => JobState.Idle,
    };

    /// <summary>A job is on the plate and not over yet.</summary>
    public static bool IsActive(this JobState s) => s is JobState.Preparing or JobState.Printing or JobState.Paused;

    public static string Title(this JobState s) => s switch
    {
        JobState.Idle => Loc.T("Ready", "Printer state"),
        JobState.Preparing => Loc.T("Preparing", "Printer state"),
        JobState.Printing => Loc.T("Printing", "Printer state"),
        JobState.Paused => Loc.T("Paused", "Printer state"),
        JobState.Finished => Loc.T("Finished", "Printer state"),
        _ => Loc.T("Failed", "Printer state"),
    };

    public static string Title(this SpeedLevel s) => s switch
    {
        SpeedLevel.Silent => Loc.T("Silent", "Print speed profile"),
        SpeedLevel.Standard => Loc.T("Standard", "Print speed profile"),
        SpeedLevel.Sport => Loc.T("Sport", "Print speed profile"),
        _ => Loc.T("Ludicrous", "Print speed profile"),
    };

    /// <summary>Nominal speed relative to Standard, as Bambu Studio shows it.</summary>
    public static int Percent(this SpeedLevel s) => s switch
    {
        SpeedLevel.Silent => 50,
        SpeedLevel.Standard => 100,
        SpeedLevel.Sport => 124,
        _ => 166,
    };

    public static string Title(this AmsKind k) => k switch
    {
        AmsKind.Ams => "AMS",
        AmsKind.Lite => "AMS Lite",
        AmsKind.Pro2 => "AMS 2 Pro",
        AmsKind.Ht => "AMS HT",
        AmsKind.Ams3H => "AMS 3H",
        _ => "AMS-B",
    };

    public static string Title(this IssueSeverity s) => s switch
    {
        IssueSeverity.Fatal => Loc.T("Error", "Issue severity"),
        IssueSeverity.Serious => Loc.T("Warning", "Issue severity"),
        IssueSeverity.Common => Loc.T("Notice", "Issue severity"),
        _ => Loc.T("Info", "Issue severity"),
    };
}

public readonly record struct Temperature(double Current, double Target)
{
    public bool IsHeating => Target > 0;
    /// <summary>Within a couple of degrees of the target.</summary>
    public bool IsAtTarget => Target > 0 && Math.Abs(Current - Target) <= 2;
}

/// <summary>An inclusive range of whole numbers (Swift's <c>ClosedRange&lt;Int&gt;</c>).</summary>
public readonly record struct IntRange(int Low, int High);

/// <summary>One filament slot: an AMS tray or the external spool holder.</summary>
public sealed class Spool
{
    public const int ExternalId = 254;
    /// <summary>The AMS 3H's slots are 1024 + slot, past the four-slot units (unit * 4 + slot) and the AMS HTs
    /// (128-135); the AMS-B's follow.</summary>
    public const int Ams3HBase = 1024;
    public const int AmsBBase = 1056;

    /// <summary>Global slot number: <c>unit * 4 + slot</c> for AMS units 0-3, <c>128 + n</c> for AMS HT, 254 for the
    /// external spool.</summary>
    public int Id { get; set; }
    public int Unit { get; set; }
    public int Slot { get; set; }
    /// <summary>"PLA", "PETG"…; empty when the slot is empty or unknown.</summary>
    public string Material { get; set; } = "";
    /// <summary>"PLA Matte"…; empty for third-party spools.</summary>
    public string Name { get; set; } = "";
    /// <summary><c>RRGGBBAA</c>.</summary>
    public string Color { get; set; } = "00000000";
    /// <summary>More than one entry for multicolour spools.</summary>
    public List<string> Colors { get; set; } = [];
    /// <summary>Percent remaining, null when the printer can't tell (non-RFID spools).</summary>
    public int? Remain { get; set; }
    /// <summary>Net filament weight of a full spool in grams (<c>tray_weight</c>), from the Bambu RFID tag.</summary>
    public int? Weight { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsBambu { get; set; }
    /// <summary>The Bambu Studio filament preset set for the slot (<c>tray_info_idx</c>, "GFA01").</summary>
    public string? Preset { get; set; }
    /// <summary>"Bambu Lab" for RFID spools, else the maker of the preset the slot is set to ("SUNLU").</summary>
    public string? Brand { get; set; }
    /// <summary>Bambu Lab's colour name ("Grass Green"), for their spools and colours picked from their list.</summary>
    public string? ColorName { get; set; }
    /// <summary>Nozzle temperatures the filament prints at, from the tag or the preset.</summary>
    public IntRange? NozzleRange { get; set; }

    [JsonIgnore] public bool IsExternal => Id == ExternalId;

    [JsonIgnore]
    public string RemainText => !IsLoaded ? Loc.T("Empty", "Filament slot") : Remain is { } r ? $"{r}%" : "—";

    [JsonIgnore] public string DisplayName => Name.Length == 0 ? Material : Name;

    /// <summary>Filament left in grams: the full spool's weight from its tag times the share the AMS measured left.</summary>
    [JsonIgnore]
    public int? GramsLeft => IsLoaded && Remain is { } remain && Weight is { } weight && weight > 0
        ? (int)Math.Round(weight * (double)remain / 100, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>"≈ 810 g" when the weight is known, else the percentage.</summary>
    [JsonIgnore] public string AmountText => GramsLeft is { } g ? Format.Grams(g) : RemainText;

    /// <summary>"81% · ≈ 810 g", for tooltips.</summary>
    [JsonIgnore]
    public string RemainDetail
    {
        get
        {
            if (!IsLoaded) return Loc.T("Empty", "Filament slot");
            if (Remain is not { } remain) return Loc.T("Not measured", "Filament left on a spool without RFID");
            return GramsLeft is { } g ? $"{remain}% · {Format.Grams(g)}" : $"{remain}%";
        }
    }

    /// <summary>"A1", "A2"… like the printer's screen; "Ext" for the external spool; 1, 2… for the H3C's rack AMS
    /// (AMS 3H or AMS-B), as Bambu Studio numbers its slots.</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            if (IsExternal) return Loc.T("Ext", "Short label for the external spool, beside AMS slots like A1");
            if (Id >= Ams3HBase) return $"{Slot + 1}";
            if (Unit >= 128) return $"HT{Unit - 127}";
            return $"{(char)('A' + Math.Clamp(Unit, 0, 25))}{Slot + 1}";
        }
    }

    /// <summary>The label in a sentence: "A1", "HT1" and "Ext" read on their own, a bare number doesn't, so the rack
    /// AMS's slots read "Slot 3".</summary>
    [JsonIgnore]
    public string SlotName => Id >= Ams3HBase ? Loc.F("Slot %lld", Slot + 1) : Label;

    public Spool Clone()
    {
        var copy = (Spool)MemberwiseClone();
        copy.Colors = [.. Colors];
        return copy;
    }
}

public sealed class AmsUnit
{
    public int Id { get; set; }
    public AmsKind Kind { get; set; }
    /// <summary>Relative humidity in percent (<c>humidity_raw</c>), when the unit reports it.</summary>
    public int? Humidity { get; set; }
    /// <summary>1 (dry) to 5 (wet), Bambu Studio's scale.</summary>
    public int? HumidityLevel { get; set; }
    public double? Temperature { get; set; }
    public int DryingMinutesLeft { get; set; }
    public List<Spool> Spools { get; set; } = [];

    /// <summary>"AMS A", "AMS 2 Pro B"…</summary>
    [JsonIgnore]
    public string Title => Kind is AmsKind.Ams3H or AmsKind.AmsB ? Kind.Title()
        : Id >= 128 ? $"{Kind.Title()} {Id - 127}"
        : $"{Kind.Title()} {(char)('A' + Math.Clamp(Id, 0, 25))}";

    public AmsUnit Clone()
    {
        var copy = (AmsUnit)MemberwiseClone();
        copy.Spools = Spools.ConvertAll(s => s.Clone());
        return copy;
    }
}

/// <summary>A Health Management System message or the current print error.</summary>
public sealed record PrinterIssue
{
    /// <summary><c>HMS_0300_0100_0001_0007</c> or <c>0700_8006</c>.</summary>
    public required string Id { get; init; }
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }
    public Uri? WikiUrl { get; init; }
}

/// <summary>Everything Bambu Bar shows about the printer, derived from the merged MQTT report.</summary>
public sealed class PrinterStatus
{
    public JobState State { get; set; } = JobState.Idle;
    public int? StageId { get; set; }
    public string JobName { get; set; } = "";
    public int Progress { get; set; }
    public int RemainingMinutes { get; set; }
    public int Layer { get; set; }
    public int TotalLayers { get; set; }
    public Temperature Nozzle { get; set; }
    public Temperature Bed { get; set; }
    public double? Chamber { get; set; }
    /// <summary>The chamber heater's target on models that heat the chamber (H2, X1E, X2D); zero when off.</summary>
    public double? ChamberTarget { get; set; }
    /// <summary>The left nozzle on two-nozzle models (<see cref="Nozzle"/> is the right one, as Bambu Studio numbers them).</summary>
    public Temperature? SecondNozzle { get; set; }
    /// <summary>On two-nozzle models, whether the left nozzle is the one printing.</summary>
    public bool? LeftNozzleInUse { get; set; }
    /// <summary>Fan speeds in percent.</summary>
    public int? PartFan { get; set; }
    public int? AuxFan { get; set; }
    public int? ExhaustFan { get; set; }
    public int? HeatbreakFan { get; set; }
    /// <summary>The H3C's second aux fan (<c>airduct</c> part 10, "Left(Aux)" in Bambu Studio); <see cref="AuxFan"/> is then the right one.</summary>
    public int? LeftAuxFan { get; set; }
    /// <summary>The H3C's chamber fan (<c>airduct</c> part 15, "Chamber" in Bambu Studio), besides the exhaust.</summary>
    public int? CirculationFan { get; set; }
    public SpeedLevel Speed { get; set; } = SpeedLevel.Standard;
    public bool? ChamberLightOn { get; set; }
    public bool? DoorOpen { get; set; }
    public int? WifiSignal { get; set; }
    public List<AmsUnit> Ams { get; set; } = [];
    public Spool? ExternalSpool { get; set; }
    public int? ActiveSpoolId { get; set; }
    public List<PrinterIssue> Issues { get; set; } = [];
    /// <summary>The printer refuses unsigned print commands (Developer Mode off).</summary>
    public bool ControlsLocked { get; set; }
    public string? Firmware { get; set; }
    public double? NozzleDiameter { get; set; }
    public string? NozzleType { get; set; }
    public bool? LiveViewEnabled { get; set; }
    public bool? HasSdCard { get; set; }
    public int? PlateIndex { get; set; }
    public bool UpdateAvailable { get; set; }
    /// <summary>The printer's id for the current job (<c>task_id</c>, else <c>subtask_id</c>).</summary>
    public string? TaskId { get; set; }
    /// <summary>The job ended because someone stopped it (print_error 0300_400C), not because of a fault.</summary>
    public bool Canceled { get; set; }
    /// <summary>When this status was last refreshed from the printer.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public PrinterStatus Clone()
    {
        var copy = (PrinterStatus)MemberwiseClone();
        copy.Ams = Ams.ConvertAll(u => u.Clone());
        copy.ExternalSpool = ExternalSpool?.Clone();
        copy.Issues = [.. Issues];
        return copy;
    }

    /// <summary>Drops readings the model has no sensor or fan for. Some firmware reports placeholders there (a P1S
    /// sends a chamber temperature without a sensor, an A1 sends fan fields it doesn't have).</summary>
    public void FitTo(PrinterModel? model)
    {
        if (model is null) return;
        if (!model.HasChamberSensor) { Chamber = null; ChamberTarget = null; }
        if (model.ChamberHeaterMax is null) ChamberTarget = null;
        if (!model.HasAuxFan) { AuxFan = null; LeftAuxFan = null; }
        if (!model.HasChamberFan) { ExhaustFan = null; CirculationFan = null; }
        if (!model.IsEnclosed) DoorOpen = null;
        if (!model.HasChamberLight) ChamberLightOn = null;
        if (model.NozzleCount < 2) { SecondNozzle = null; LeftNozzleInUse = null; }
        if (!model.HasAmsHumidity)
        {
            foreach (var unit in Ams.Where(u => u.Kind == AmsKind.Lite)) { unit.Humidity = null; unit.HumidityLevel = null; }
        }
    }

    /// <summary>The nozzle that's printing: the only one, or whichever of the two is in use.</summary>
    [JsonIgnore] public Temperature ActiveNozzle => LeftNozzleInUse == true ? SecondNozzle ?? Nozzle : Nozzle;

    /// <summary>The other nozzle on two-nozzle models.</summary>
    [JsonIgnore]
    public Temperature? IdleNozzle => SecondNozzle is { } second ? (LeftNozzleInUse == true ? Nozzle : second) : null;

    /// <summary>"Nozzle", or which of the two is printing.</summary>
    [JsonIgnore]
    public string ActiveNozzleTitle => SecondNozzle is null ? Loc.T("Nozzle")
        : LeftNozzleInUse == true ? Loc.T("Left nozzle") : Loc.T("Right nozzle");

    [JsonIgnore] public string IdleNozzleTitle => LeftNozzleInUse == true ? Loc.T("Right nozzle") : Loc.T("Left nozzle");

    /// <summary>"Canceled" for a job someone stopped, otherwise the state's own title.</summary>
    [JsonIgnore]
    public string StateTitle => State == JobState.Failed && Canceled ? Loc.T("Canceled", "Printer state") : State.Title();

    [JsonIgnore]
    public string? StageText => StageId is { } id && id != 255 && id != -1 ? StageName(id) : null;

    /// <summary>Why the printer paused, short for the small widget ("Filament ran out", "By user").</summary>
    [JsonIgnore] public string? PauseReason => StageId is { } id ? PauseReasonText(id) : null;

    /// <summary>The line under the job name: "Heating hotend", "Paused: filament ran out", "Layer 170 of 305"…</summary>
    [JsonIgnore]
    public string DetailText
    {
        get
        {
            switch (State)
            {
                case JobState.Printing:
                    if (StageText is { } stage && StageId != 0) return stage;
                    if (TotalLayers > 0) return Loc.F("Layer %lld of %lld", Layer, TotalLayers);
                    return State.Title();
                case JobState.Preparing:
                    return StageText ?? State.Title();
                case JobState.Paused:
                    if (StageText is { } paused && StageId is { } id && PauseReasonKeys.ContainsKey(id)) return paused;
                    return State.Title();
                case JobState.Finished:
                    return State.Title();
                case JobState.Failed:
                    return Canceled ? StateTitle : Issues.FirstOrDefault()?.Message ?? Loc.T("Print failed");
                default:
                    return StageText ?? Loc.T("Ready to print");
            }
        }
    }

    [JsonIgnore]
    public Spool? ActiveSpool
    {
        get
        {
            if (ActiveSpoolId is not { } id) return null;
            if (id == Spool.ExternalId) return ExternalSpool;
            return Ams.SelectMany(u => u.Spools).FirstOrDefault(s => s.Id == id);
        }
    }

    [JsonIgnore]
    public IEnumerable<Spool> AllSpools => Ams.SelectMany(u => u.Spools).Concat(ExternalSpool is { } e ? [e] : []);

    /// <summary>The most severe issue; the first of equals, as Swift's <c>min</c> keeps.</summary>
    [JsonIgnore]
    public PrinterIssue? MostSevereIssue => Issues.Count == 0 ? null : Issues.Aggregate((a, b) => b.Severity < a.Severity ? b : a);

    /// <summary>Estimated completion, from the remaining minutes at <see cref="UpdatedAt"/>.</summary>
    [JsonIgnore]
    public DateTimeOffset? EstimatedEnd =>
        State.IsActive() && RemainingMinutes > 0 ? UpdatedAt.AddMinutes(RemainingMinutes) : null;

    /// <summary>Progress expected at <paramref name="date"/>, extrapolated linearly over the remaining time. Lets
    /// widgets advance between updates.</summary>
    public double ProjectedProgress(DateTimeOffset date)
    {
        var now = Progress / 100.0;
        if (State != JobState.Printing || EstimatedEnd is not { } end || end <= UpdatedAt) return now;
        var fraction = Math.Min(1, Math.Max(0, (date - UpdatedAt).TotalSeconds / (end - UpdatedAt).TotalSeconds));
        return Math.Min(0.99, now + (1 - now) * fraction);
    }

    private static readonly Dictionary<int, string> PauseReasonKeys = new()
    {
        [6] = "Filament ran out", [16] = "By user", [17] = "Front cover fell off", [20] = "Nozzle temperature fault",
        [21] = "Heatbed temperature fault", [23] = "Skipped steps", [26] = "AMS offline", [27] = "Heatbreak fan too slow",
        [28] = "Chamber temperature fault", [30] = "By G-code", [32] = "Nozzle clumping", [33] = "Cutter error",
        [34] = "First layer error", [35] = "Nozzle clog",
    };

    public static string? PauseReasonText(int stage) =>
        PauseReasonKeys.TryGetValue(stage, out var key) ? Loc.T(key, "Pause reason") : null;

    /// <summary>The printer's stage names (<c>stg_cur</c>), as Bambu Studio words them.</summary>
    private static readonly string[] StageKeys =
    [
        "Printing", "Auto bed leveling", "Preheating heatbed", "Vibration compensation", "Changing filament",
        "M400 pause", "Paused: filament ran out", "Heating hotend", "Calibrating extrusion", "Scanning bed surface",
        "Inspecting first layer", "Identifying build plate", "Calibrating Micro Lidar", "Homing toolhead",
        "Cleaning nozzle tip", "Checking extruder temperature", "Paused by user", "Paused: front cover fell off",
        "Calibrating Micro Lidar", "Calibrating flow ratio", "Paused: nozzle temperature fault",
        "Paused: heatbed temperature fault", "Unloading filament", "Paused: skipped steps", "Loading filament",
        "Calibrating motor noise", "Paused: AMS offline", "Paused: heatbreak fan too slow",
        "Paused: chamber temperature fault", "Cooling chamber", "Paused by G-code", "Motor noise showoff",
        "Paused: nozzle clumping", "Paused: cutter error", "Paused: first layer error", "Paused: nozzle clog",
        "Measuring motion precision", "Enhancing motion precision", "Measuring motion accuracy",
        "Calibrating nozzle offset", "High-temperature bed leveling", "Checking quick-release lever",
        "Checking door and top cover", "Calibrating laser", "Checking platform", "Checking BirdsEye camera",
        "Calibrating BirdsEye camera", "Bed leveling, phase 1", "Bed leveling, phase 2", "Heating chamber",
        "Cooling heatbed", "Printing calibration lines", "Checking material", "Calibrating Live View camera",
        "Waiting for heatbed temperature", "Checking material position", "Calibrating cutter offset",
        "Measuring surface", "Thermal preconditioning", "Homing blade holder", "Calibrating camera offset",
        "Calibrating blade holder", "Hotend pick-and-place test", "Waiting for chamber temperature",
        "Preparing hotend", "Calibrating clumping detection", "Purifying chamber air", "Measuring rotary attachment",
        "Moving to purge chute", "Cooling nozzle", "Centering toolhead", "Arc fitting", "Detecting hotend type",
        "Detecting build plate alignment", "Checking heatbed for objects", "Checking heatbed underside",
        "Pre-extruding", "Preparing AMS",
    ];

    public static int StageCount => StageKeys.Length;

    public static string? StageName(int stage) =>
        stage >= 0 && stage < StageKeys.Length ? Loc.T(StageKeys[stage], "Printer stage") : null;
}

/// <summary>The last job the printer ran, kept so an idle printer can still say how its last print went.</summary>
public sealed record JobRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required JobOutcome Outcome { get; init; }
    public int TotalLayers { get; init; }
    public string? FilamentColor { get; init; }
    public string? FilamentMaterial { get; init; }
    /// <summary>The printer's task id, to tell two jobs with the same name apart.</summary>
    public string? TaskId { get; init; }
    /// <summary>Which printer ran it.</summary>
    public string? PrinterSerial { get; init; }

    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
}

[JsonConverter(typeof(JsonStringEnumConverter<JobOutcome>))]
public enum JobOutcome
{
    Running, Finished, Failed, Canceled,
    /// <summary>The app wasn't running when it ended, so how it ended isn't known.</summary>
    Ended,
}

public static class JobOutcomeText
{
    public static string Title(this JobOutcome o) => o switch
    {
        JobOutcome.Running => Loc.T("In progress", "How a print ended"),
        JobOutcome.Finished => Loc.T("Finished", "How a print ended"),
        JobOutcome.Failed => Loc.T("Failed", "How a print ended"),
        JobOutcome.Canceled => Loc.T("Canceled", "How a print ended"),
        _ => Loc.T("Ended", "How a print ended; the app wasn't running to see how"),
    };
}
