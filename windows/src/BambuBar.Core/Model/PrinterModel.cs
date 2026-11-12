namespace BambuBar.Core.Model;

/// <summary>The drawing and 3D model a printer gets.</summary>
public enum PrinterShape
{
    /// <summary>P2S, P1S, X1 family, X2D: a closed box with a glass door.</summary>
    Enclosed,
    /// <summary>H2 family: a wider closed box.</summary>
    Wide,
    /// <summary>H3C: a wide closed box with nine docked tools and the AMS 3H built into a hood on top.</summary>
    H3C,
    /// <summary>P1P: the CoreXY frame without panels.</summary>
    OpenFrame,
    /// <summary>A1 and A2L: an open bed-slinger under an arch-shaped gantry.</summary>
    BedSlinger,
    /// <summary>A1 mini: a bed-slinger with a single Z column.</summary>
    Mini,
}

public static class PrinterShapes
{
    /// <summary>From a model name ("P2S", "A1 mini") or SSDP code ("N7", "N1").</summary>
    public static PrinterShape FromModel(string? model)
    {
        if (PrinterModel.Named(model) is { } known) return known.Shape;
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("mini") || m is "n1" or "a12" or "a04") return PrinterShape.Mini;
        if (m.StartsWith("a1") || m.StartsWith("a2") || m is "n2s" or "a11" or "n9") return PrinterShape.BedSlinger;
        if (m.StartsWith("h3") || m.StartsWith("n10")) return PrinterShape.H3C;
        if (m.StartsWith("h2") || m.StartsWith("o1") || m.StartsWith("o2")) return PrinterShape.Wide;
        if (m is "p1p" or "c11") return PrinterShape.OpenFrame;
        return PrinterShape.Enclosed;
    }

    public static bool IsBedSlinger(this PrinterShape s) => s is PrinterShape.BedSlinger or PrinterShape.Mini;

    /// <summary>The wide drawing: H2 family and H3C.</summary>
    public static bool IsWide(this PrinterShape s) => s is PrinterShape.Wide or PrinterShape.H3C;

    /// <summary>Base name of the shape's renders and model in Assets\Printers ("enclosed-body.png").</summary>
    public static string AssetName(this PrinterShape s) => s switch
    {
        PrinterShape.Enclosed => "enclosed",
        PrinterShape.Wide => "wide",
        PrinterShape.H3C => "h3c",
        PrinterShape.OpenFrame => "openFrame",
        PrinterShape.BedSlinger => "bedSlinger",
        _ => "mini",
    };
}

/// <summary>
/// What a Bambu Lab model has, so the app and widgets show only what the printer can report. From the printer
/// profiles Bambu Lab publishes with Bambu Studio (see the Mac's PrinterModel.swift for the fields' sources).
/// </summary>
public sealed record PrinterModel
{
    /// <summary>Bambu's model id, as SSDP and Bambu Studio name it ("N7").</summary>
    public required string Code { get; init; }
    /// <summary>"P2S", "X1 Carbon"…</summary>
    public required string Name { get; init; }
    public required PrinterShape Shape { get; init; }
    /// <summary>The first three characters of its serial numbers.</summary>
    public required string SerialPrefix { get; init; }
    /// <summary>A closed chamber with a door.</summary>
    public required bool IsEnclosed { get; init; }
    /// <summary>Reports a chamber temperature worth showing (the P1S has a chamber but no sensor).</summary>
    public required bool HasChamberSensor { get; init; }
    /// <summary>Highest chamber temperature it can heat to; null without active chamber heating.</summary>
    public required int? ChamberHeaterMax { get; init; }
    public required bool HasAuxFan { get; init; }
    public required bool HasChamberFan { get; init; }
    /// <summary>The AMS reports humidity (not the AMS Lite of the A series).</summary>
    public required bool HasAmsHumidity { get; init; }
    /// <summary>Only the AMS Lite fits (A1, A1 mini).</summary>
    public required bool UsesAmsLite { get; init; }
    /// <summary>Two tool heads (H2D, H2C, X2D). The H3C has one, fed from a rack of nozzles.</summary>
    public required int NozzleCount { get; init; }
    public required int NozzleMax { get; init; }
    public required int BedMax { get; init; }
    /// <summary>Width × depth × height in millimetres.</summary>
    public required (int X, int Y, int Z) BuildVolume { get; init; }
    public required bool HasLidar { get; init; }
    /// <summary>Takes the laser and cutting modules (H2 family).</summary>
    public required bool HasLaserModule { get; init; }

    /// <summary>A switchable chamber light (none on the A series).</summary>
    public bool HasChamberLight => Shape is not (PrinterShape.BedSlinger or PrinterShape.Mini);

    public string BuildVolumeText => $"{BuildVolume.X} × {BuildVolume.Y} × {BuildVolume.Z} mm";

    public string ChamberText =>
        ChamberHeaterMax is { } max ? Loc.F("Heated, up to %lld °C", max)
        : HasChamberSensor ? Loc.T("Enclosed, with temperature sensor")
        : IsEnclosed ? Loc.T("Enclosed") : Loc.T("Open frame");

    // Models are equal when their ids are, as on the Mac.
    public bool Equals(PrinterModel? other) => other is not null && Code == other.Code;
    public override int GetHashCode() => Code.GetHashCode(StringComparison.Ordinal);

    private static PrinterModel M(string code, string name, PrinterShape shape, string prefix, bool enclosed, bool sensor,
        int? heater, bool aux, bool chamberFan, bool humidity, bool lite, int nozzles, int nozzleMax, int bedMax,
        (int, int, int) volume, bool lidar, bool laser) => new()
    {
        Code = code, Name = name, Shape = shape, SerialPrefix = prefix, IsEnclosed = enclosed, HasChamberSensor = sensor,
        ChamberHeaterMax = heater, HasAuxFan = aux, HasChamberFan = chamberFan, HasAmsHumidity = humidity,
        UsesAmsLite = lite, NozzleCount = nozzles, NozzleMax = nozzleMax, BedMax = bedMax, BuildVolume = volume,
        HasLidar = lidar, HasLaserModule = laser,
    };

    public static IReadOnlyList<PrinterModel> All { get; } =
    [
        M("N7", "P2S", PrinterShape.Enclosed, "22E", true, true, null, true, true, true, false, 1, 300, 110, (256, 256, 256), false, false),
        M("C12", "P1S", PrinterShape.Enclosed, "01P", true, false, null, true, true, true, false, 1, 300, 100, (256, 256, 250), false, false),
        M("C11", "P1P", PrinterShape.OpenFrame, "01S", false, false, null, true, true, true, false, 1, 300, 100, (256, 256, 250), false, false),
        M("BL-P001", "X1 Carbon", PrinterShape.Enclosed, "00M", true, true, null, true, true, true, false, 1, 300, 110, (256, 256, 250), true, false),
        M("BL-P002", "X1", PrinterShape.Enclosed, "00W", true, true, null, true, true, true, false, 1, 300, 110, (256, 256, 250), true, false),
        M("C13", "X1E", PrinterShape.Enclosed, "03W", true, true, 60, true, true, true, false, 1, 320, 110, (256, 256, 250), true, false),
        M("N6", "X2D", PrinterShape.Enclosed, "20P", true, true, 65, true, true, true, false, 2, 300, 120, (256, 256, 261), false, false),
        M("N2S", "A1", PrinterShape.BedSlinger, "039", false, false, null, false, false, false, true, 1, 300, 100, (256, 256, 256), false, false),
        M("N1", "A1 mini", PrinterShape.Mini, "030", false, false, null, false, false, false, true, 1, 300, 80, (180, 180, 180), false, false),
        M("N9", "A2L", PrinterShape.BedSlinger, "26A", false, false, null, false, false, false, false, 1, 300, 80, (330, 320, 325), false, false),
        M("O1D", "H2D", PrinterShape.Wide, "094", true, true, 65, true, true, true, false, 2, 350, 120, (350, 320, 325), false, true),
        M("O1E", "H2D Pro", PrinterShape.Wide, "239", true, true, 65, true, true, true, false, 2, 350, 120, (350, 320, 325), false, true),
        M("O1C", "H2C", PrinterShape.Wide, "31B", true, true, 65, true, true, true, false, 2, 350, 120, (330, 320, 325), false, true),
        M("N10", "H3C", PrinterShape.H3C, "32X", true, true, 65, true, true, true, false, 1, 350, 120, (330, 320, 325), false, false),
        M("O1S", "H2S", PrinterShape.Wide, "093", true, true, 65, true, true, true, false, 1, 350, 120, (340, 320, 340), false, true),
    ];

    /// <summary>Other ids the same models report (older firmware, Pro and V2 variants; the V2 ids are each profile's
    /// <c>subseries</c> in Bambu Studio's resources/printers).</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["BL-P003"] = "C13", ["A12"] = "N1", ["A04"] = "N1", ["A11"] = "N2S", ["O2D"] = "O1E", ["O1C2"] = "O1C",
        ["N6-V2"] = "N6", ["N7-V2"] = "N7", ["N10-V2"] = "N10", ["O1C2-V2"] = "O1C", ["O1D-V2"] = "O1D",
        ["O1E-V2"] = "O1E", ["O1S-V2"] = "O1S",
    };

    /// <summary>By serial prefix when it's one Bambu Lab uses, then by model id. The serial wins because setups before
    /// 1.2 saved "N7" for printers typed in by hand, which named an A1 a P2S everywhere.</summary>
    public static PrinterModel? Find(string code, string serial = "")
    {
        var prefix = (serial.Length > 3 ? serial[..3] : serial).ToUpperInvariant();
        if (prefix.Length > 0 && All.FirstOrDefault(m => m.SerialPrefix == prefix) is { } match) return match;
        var upper = code.ToUpperInvariant();
        var id = Aliases.GetValueOrDefault(upper, upper);
        return All.FirstOrDefault(m => m.Code == id);
    }

    /// <summary>By display name ("P2S", "A1 mini") or model id; what the widgets get in their state.</summary>
    public static PrinterModel? Named(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Find(name);
    }

    public static string NameFor(string code, string serial = "") => Find(code, serial)?.Name ?? Loc.T("Bambu Lab printer");
}
