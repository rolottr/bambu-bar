namespace BambuBar.Core.Model;

/// <summary>Where to find the printer. The access code lives in Credential Manager, not here.</summary>
public sealed record PrinterConfig
{
    public required string Host { get; init; }
    public required string Serial { get; init; }
    public required string Name { get; init; }
    /// <summary>SSDP model code ("N7" for the P2S).</summary>
    public string ModelCode { get; init; } = "";
    /// <summary>The camera is on for this printer (Printers page). Off, or never set, it's hidden; the switch in
    /// Settings has to be on too.</summary>
    public bool? CameraEnabled { get; init; }

    public string ModelName => PrinterModel.NameFor(ModelCode, Serial);
    public PrinterModel? Model => PrinterModel.Find(ModelCode, Serial);

    /// <summary>Printers listen on 8883; <c>host:port</c> reaches a mock printer (shared/scripts/fleet).</summary>
    public (string Host, int Port) Endpoint
    {
        get
        {
            // Like Swift's split: empty pieces are dropped.
            var parts = Host.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && ushort.TryParse(parts[1], out var port) && parts[1].All(char.IsAsciiDigit)
                ? (parts[0], port)
                : (Host, 8883);
        }
    }

    /// <summary>What a printer is called before it has announced its own name: its model and the end of its serial
    /// ("P2S 495").</summary>
    public static string PlaceholderName(string serial, string modelCode)
    {
        var model = PrinterModel.Find(modelCode, serial);
        return $"{model?.Name ?? Loc.T("Printer")} {(serial.Length > 3 ? serial[^3..] : serial)}";
    }

    /// <summary>The name is one Bambu Bar made up, not one the person chose: the bare serial (in any case) or the
    /// placeholder. The name the printer announces on the network may replace it.</summary>
    public bool HasGeneratedName =>
        string.Equals(Name, Serial, StringComparison.OrdinalIgnoreCase)
        || Name == PlaceholderName(Serial, ModelCode)
        || Name == PlaceholderName(Serial, "");

    /// <summary>Saved printers with uppercase serials, once each. The first copy wins: it's the one the person has
    /// been looking at.</summary>
    public static List<PrinterConfig> Deduplicated(IEnumerable<PrinterConfig> configs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PrinterConfig>();
        foreach (var config in configs)
        {
            var serial = config.Serial.ToUpperInvariant();
            if (serial.Length == 0 || !seen.Add(serial)) continue;
            result.Add(config with { Serial = serial });
        }
        return result;
    }
}
