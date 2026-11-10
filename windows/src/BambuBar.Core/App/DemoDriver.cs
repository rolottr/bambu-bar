using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.App;

/// <summary><c>--demo [scenario] [--fleet]</c>: a simulated P2S (plus five more printers with <c>--fleet</c>), for
/// screenshots and for trying the app without a printer. Nothing is saved and no network is touched. Runs on the
/// context it's started on, like a connection.</summary>
public sealed class DemoDriver : IDisposable
{
    public enum Scenario { Printing, Preparing, Paused, Finished, Failed, Idle, Offline, Unconfigured }

    public static Scenario? RequestedScenario(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--demo");
        if (i < 0) return null;
        if (i + 1 < args.Count && Enum.TryParse<Scenario>(args[i + 1], ignoreCase: true, out var s)
            && args[i + 1].All(char.IsAsciiLetterLower)) return s;
        return Scenario.Printing;
    }

    public Action<PrinterStatus, bool>? OnStatus { get; set; }
    public Action<PrinterConnection.Phase>? OnPhase { get; set; }

    private readonly Scenario _scenario;
    private readonly PrinterStatus _status = Sample();
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private Timer? _timer;
    private int _tick;

    /// <summary><paramref name="variant"/> gives each simulated printer its own job and spools.</summary>
    public DemoDriver(Scenario scenario, int variant = 0)
    {
        _scenario = scenario;
        var s = _status;
        var spools = s.Ams[0].Spools;
        switch (variant)
        {
            case 1:
                s.JobName = "Gridfinity Baseplate 4x4";
                Recolor(spools[0], "DE4343", "Scarlet Red", "PLA Matte", "GFA01");
                s.Ams[0].Kind = AmsKind.Ams;
                s.ActiveSpoolId = 0;
                s.ExternalSpool = null;
                break;
            case 2:
                s.JobName = "Articulated Dragon";
                s.Ams[0].Kind = AmsKind.Lite;
                Recolor(spools[1], "8671CB", "Purple", "PLA Silk+", "GFA06");
                s.ActiveSpoolId = 1;
                s.Progress = 31; s.Layer = 88; s.TotalLayers = 284; s.RemainingMinutes = 147;
                s.Chamber = null;
                s.ExternalSpool = null;
                break;
            case 3:
                s.JobName = "Two-tone Vase";
                Recolor(spools[0], "FFFFFF", "Jade White", "PLA Basic", "GFA00");
                Recolor(spools[1], "00AE42", "Bambu Green", "PLA Basic", "GFA00");
                s.ActiveSpoolId = 1;
                s.Progress = 44; s.Layer = 132; s.TotalLayers = 300; s.RemainingMinutes = 188;
                s.SecondNozzle = new(170, 170);
                s.LeftNozzleInUse = false;
                s.Chamber = 41;
                break;
            case 4:
                s.JobName = "Keychain Batch";
                s.Ams[0].Kind = AmsKind.Lite;
                Recolor(spools[3], "E8AFCF", "Sakura Pink", "PLA Matte", "GFA01");
                s.ActiveSpoolId = 3;
                s.Chamber = null;
                s.ExternalSpool = null;
                break;
            case 5:
                s.JobName = "Cable Clips x24";
                s.Ams[0].Kind = AmsKind.Ams;
                Recolor(spools[2], "0056B8", "Cobalt Blue", "PLA Basic", "GFA00");
                s.ActiveSpoolId = 2;
                s.Progress = 83; s.Layer = 166; s.TotalLayers = 200; s.RemainingMinutes = 18;
                s.ExternalSpool = null;
                break;
        }
    }

    /// <summary>A mid-print P2S with an AMS 2 Pro, for previews, placeholders and the demo.</summary>
    public static PrinterStatus Sample()
    {
        static Spool S(int id, string material, string name, string color, int? remain, bool bambu, string preset, string brand,
            string? colorName, int low, int high, int? weight = 1000) => new()
        {
            Id = id, Unit = id == Spool.ExternalId ? 255 : 0, Slot = id == Spool.ExternalId ? 0 : id, Material = material, Name = name,
            Color = color, Colors = [color], Remain = remain, Weight = weight, IsLoaded = true, IsBambu = bambu, Preset = preset,
            Brand = brand, ColorName = colorName, NozzleRange = new(low, high),
        };
        return new PrinterStatus
        {
            State = JobState.Printing, StageId = 0, JobName = "Rear Dry Pod", Progress = 64, RemainingMinutes = 62,
            Layer = 170, TotalLayers = 305, Nozzle = new(220, 220), Bed = new(55, 55), Chamber = 38, PartFan = 90,
            AuxFan = 40, Speed = SpeedLevel.Standard, ChamberLightOn = true, DoorOpen = false, WifiSignal = -52,
            Ams =
            [
                new AmsUnit
                {
                    Id = 0, Kind = AmsKind.Pro2, Humidity = 16, HumidityLevel = 1, Temperature = 28.4,
                    Spools =
                    [
                        S(0, "PLA", "PLA Matte", "E8DBB7FF", 81, true, "GFA01", "Bambu Lab", "Desert Tan", 190, 230),
                        S(1, "PLA", "PLA Basic", "000000FF", 39, true, "GFA00", "Bambu Lab", "Black", 190, 230),
                        S(2, "PLA", "PLA+", "3F8E43FF", null, false, "GFSNL03", "SUNLU", null, 200, 230, weight: null),
                        S(3, "PLA", "PLA Silk+", "F4A925FF", 22, true, "GFA06", "Bambu Lab", "Gold", 200, 240),
                    ],
                },
            ],
            ActiveSpoolId = 3,
            ExternalSpool = S(Spool.ExternalId, "PETG", "PETG HF", "1F79E5FF", null, true, "GFG02", "Bambu Lab", "Lake Blue", 230, 270),
            Firmware = "01.03.00.00", NozzleDiameter = 0.4, NozzleType = "HS01", HasSdCard = true,
        };
    }

    /// <summary>A Bambu Lab spool of another colour, as its tag would report it.</summary>
    private static void Recolor(Spool spool, string hex, string colorName, string product, string preset)
    {
        spool.Color = hex + "FF";
        spool.Colors = [spool.Color];
        spool.ColorName = colorName;
        spool.Name = product;
        spool.Material = "PLA";
        spool.Preset = preset;
        spool.Brand = "Bambu Lab";
        spool.IsBambu = true;
        spool.Weight = 1000;
        spool.Remain ??= 72;
    }

    public void Start(bool animate = true)
    {
        switch (_scenario)
        {
            case Scenario.Unconfigured:
                OnPhase?.Invoke(new PrinterConnection.Phase.Idle());
                return;
            case Scenario.Offline:
                OnPhase?.Invoke(new PrinterConnection.Phase.Offline(Loc.T("The printer isn't reachable on the network.")));
                return;
        }
        OnPhase?.Invoke(new PrinterConnection.Phase.Online());
        Shape();
        Emit();
        if (!animate || _scenario is not (Scenario.Printing or Scenario.Preparing)) return;
        _timer = new Timer(_ => Post(Advance), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }

    private void Shape()
    {
        var s = _status;
        switch (_scenario)
        {
            case Scenario.Preparing:
                s.State = JobState.Preparing; s.StageId = 7; s.Progress = 0; s.Layer = 0; s.RemainingMinutes = 168;
                s.Nozzle = new(164, 220);
                break;
            case Scenario.Paused:
                s.State = JobState.Paused; s.StageId = 6;
                s.Issues =
                [
                    new PrinterIssue
                    {
                        Id = "HMS_0700_2300_0002_0001", Severity = IssueSeverity.Serious,
                        Message = HmsCatalog.Shared.HmsText("0700230000020001") ?? "AMS A slot 4 filament has run out. Please insert a new filament.",
                        WikiUrl = HmsCatalog.WikiUrl("0700230000020001", "01P"),
                    },
                ];
                break;
            case Scenario.Finished:
                s.State = JobState.Finished; s.StageId = 255; s.Progress = 100; s.RemainingMinutes = 0; s.Layer = 305;
                s.Nozzle = new(46, 0); s.Bed = new(41, 0); s.ActiveSpoolId = null;
                break;
            case Scenario.Failed:
                s.State = JobState.Failed; s.StageId = 255; s.Nozzle = new(180, 0);
                s.Issues =
                [
                    new PrinterIssue
                    {
                        Id = "0300_4006", Severity = IssueSeverity.Fatal,
                        Message = HmsCatalog.Shared.ErrorText("03004006") ?? "The nozzle is clogged with filament. The print has been stopped.",
                        WikiUrl = HmsCatalog.WikiUrl("03004006", "01P"),
                    },
                ];
                break;
            case Scenario.Idle:
                s.State = JobState.Idle; s.StageId = 255; s.JobName = ""; s.Progress = 0; s.RemainingMinutes = 0;
                s.Layer = 0; s.TotalLayers = 0; s.Nozzle = new(27, 0); s.Bed = new(25, 0); s.Chamber = 24;
                s.PartFan = 0; s.AuxFan = 0; s.ActiveSpoolId = null; s.ChamberLightOn = false;
                break;
        }
    }

    private void Advance()
    {
        var s = _status;
        _tick++;
        if (s.State == JobState.Preparing)
        {
            s.Nozzle = s.Nozzle with { Current = Math.Min(s.Nozzle.Target, s.Nozzle.Current + 9) };
            if (s.Nozzle.IsAtTarget) { s.State = JobState.Printing; s.StageId = 0; }
        }
        else if (s.State == JobState.Printing && _tick % 2 == 0 && s.Progress < 99)
        {
            s.Progress++;
            s.Layer = Math.Min(s.TotalLayers, (int)(s.TotalLayers * (double)s.Progress / 100));
            s.RemainingMinutes = Math.Max(1, s.RemainingMinutes - 2);
            s.UpdatedAt = DateTimeOffset.Now;
        }
        if (s.Nozzle.Target > 0) s.Nozzle = s.Nozzle with { Current = s.Nozzle.Target + new[] { -1, 0, 0, 1 }[Random.Shared.Next(4)] };
        Emit();
    }

    private void Emit()
    {
        if (_status.State is JobState.Printing or JobState.Preparing) _status.UpdatedAt = DateTimeOffset.Now;
        OnStatus?.Invoke(_status.Clone(), false);
    }

    public void Pause() { _status.State = JobState.Paused; _status.StageId = 16; Emit(); }
    public void Resume() { _status.State = JobState.Printing; _status.StageId = 0; Emit(); }
    public void Stop() { _status.State = JobState.Failed; _status.StageId = 255; _status.Issues = []; OnStatus?.Invoke(_status.Clone(), true); }
    public void SetLight(bool on) { _status.ChamberLightOn = on; Emit(); }
    public void SetSpeed(SpeedLevel level) { _status.Speed = level; Emit(); }

    public void Dispose() => _timer?.Dispose();

    public static List<JobRecord> SampleHistory(IReadOnlyList<string> serials)
    {
        var now = DateTimeOffset.Now;
        JobRecord Job(string name, double hoursAgo, double minutes, JobOutcome outcome, string color, string material, int layers)
        {
            var end = now.AddHours(-hoursAgo);
            return new JobRecord
            {
                Name = name, StartedAt = end.AddMinutes(-minutes), EndedAt = end, Outcome = outcome, TotalLayers = layers,
                FilamentColor = color, FilamentMaterial = material,
            };
        }
        JobRecord[] jobs =
        [
            Job("Cable Clip x12", 3.2, 48, JobOutcome.Finished, "1C1C1EFF", "PLA", 64),
            Job("Benchy", 20, 41, JobOutcome.Finished, "E2763AFF", "PLA", 240),
            Job("Headphone Stand", 27, 312, JobOutcome.Finished, "F4EEE6FF", "PLA", 812),
            Job("Gridfinity Bin 2x3", 49, 12, JobOutcome.Canceled, "2E7D5BFF", "PETG", 140),
            Job("Planter Spiral", 75, 95, JobOutcome.Failed, "2E7D5BFF", "PETG", 410),
            Job("Desk Hook", 98, 36, JobOutcome.Finished, "1C1C1EFF", "PLA", 120),
        ];
        // Spread over the printers, oldest first per printer like real history.
        return jobs.Select((job, i) => job with { PrinterSerial = serials.Count == 0 ? null : serials[i % serials.Count] }).ToList();
    }
}
