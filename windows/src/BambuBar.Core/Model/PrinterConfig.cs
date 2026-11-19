using BambuBar.Core.Camera;

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
    /// <summary>Issue codes never to show for this printer (<c>HMS_0300_0100_0001_0007</c>, <c>0700_8006</c>), added
    /// with Ignore next to an issue and removed on the Printers page. Null for printers saved by earlier versions.</summary>
    public List<string>? IgnoredIssues { get; init; }
    /// <summary>An external camera to show instead of the printer's own (Printers page), without its
    /// credentials. Null: the printer's own camera.</summary>
    public string? CameraUrl { get; init; }
    /// <summary>The username <see cref="CameraUrl"/> needs, if it needs one; its password is in Credential
    /// Manager.</summary>
    public string? CameraUsername { get; init; }

    /// <summary>Where this printer's camera stream comes from: an external camera when one is entered and its URL
    /// still parses, otherwise whatever this model serves. <paramref name="password"/> comes from the secret
    /// store.</summary>
    public CameraSource CameraSourceFor(string? password)
    {
        if (CameraUrl is null || !RtspUrl.TryParse(CameraUrl, out var url, out _))
        {
            return CameraTransports.ForModel(ModelName).Source();
        }
        return new Camera.CameraSource.External(new ExternalCamera
        {
            Url = url, Username = CameraUsername ?? "", Password = password ?? "",
        });
    }

    /// <summary>What to use for a camera someone entered, for both Test and Save so the two can't disagree.
    /// Each credential typed into the URL wins over the field beside it, so what was pasted is what connects,
    /// while a URL carrying only a username keeps the password that was typed.</summary>
    public static (RtspUrl Url, string Username, string Password) EnteredCamera(RtspUrl url, string username,
        string password) =>
        (url, url.Username.Length == 0 ? username : url.Username,
            url.Password.Length == 0 ? password : url.Password);

    /// <summary>Why an entered camera URL can't be used, if it can't. A settings file edited by hand can hold one
    /// that no longer parses; saying so beats quietly showing the printer's own camera instead.</summary>
    public string? CameraSourceProblem =>
        CameraUrl is not null && !RtspUrl.TryParse(CameraUrl, out _, out var problem) ? problem.Message : null;

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
