using System.Text;
using BambuBar.Core.Json;

namespace BambuBar.Core.Protocol;

/// <summary>A printer some slicer already knows the access code for.</summary>
public sealed record SlicerPrinter
{
    /// <summary>Uppercase, as the printer announces it and its certificate names it, whatever case the slicer saved.</summary>
    public required string Serial { get; init; }
    /// <summary>Best first. Bambu Studio keeps the code it got from the cloud (<c>access_code</c>) and the one typed in
    /// for LAN mode (<c>user_access_code</c>); either may be stale, so both are kept to try in turn.</summary>
    public required IReadOnlyList<string> AccessCodes { get; init; }
    public string? Host { get; init; }
    public string? Name { get; init; }
    /// <summary>"Bambu Studio", "OrcaSlicer"…</summary>
    public required string Source { get; init; }

    public bool Equals(SlicerPrinter? other) => other is not null && Serial == other.Serial
        && AccessCodes.SequenceEqual(other.AccessCodes) && Host == other.Host && Name == other.Name && Source == other.Source;

    public override int GetHashCode() => HashCode.Combine(Serial, Host, Name, Source);
}

/// <summary>
/// Reads access codes out of Bambu Studio's and OrcaSlicer's settings, so setup needs no typing. Both slicers save
/// <c>&lt;AppData&gt;\&lt;App&gt;\&lt;App&gt;.conf</c> as JSON (on Windows with an MD5 line after it) with one object per
/// section: <c>access_code</c> and <c>user_access_code</c> map serial → code, <c>ip_address</c> and
/// <c>user_access_dev_ip</c> map serial → IP. Newer Bambu Studio versions save <c>user_access_dev_ip</c> scrambled with
/// the install's <c>app.slicer_uuid</c> (see <see cref="DecodeIp"/>). Names and models aren't saved; they come from
/// the printer's network announcements.
/// </summary>
public static class SlicerImport
{
    public static readonly IReadOnlyList<(string Folder, string Title)> KnownApps =
    [
        ("BambuStudio", "Bambu Studio"), ("BambuStudioBeta", "Bambu Studio Beta"),
        ("OrcaSlicer", "OrcaSlicer"), ("OrcaSlicerNightly", "OrcaSlicer Nightly"),
    ];

    /// <summary>Where this computer's slicers keep their settings: %APPDATA% on Windows, Application Support on a Mac.</summary>
    public static string LocalSupport
    {
        get
        {
            if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support") : Path.Combine(home, ".config");
        }
    }

    /// <summary>Where the file picker should open: Bambu Studio's folder when there is one, else the settings folder.</summary>
    public static string SuggestedFolder
    {
        get
        {
            foreach (var (folder, _) in KnownApps)
            {
                var dir = Path.Combine(LocalSupport, folder);
                if (Directory.Exists(dir)) return dir;
            }
            return LocalSupport;
        }
    }

    /// <summary>Every printer with a stored access code in this computer's slicers.</summary>
    public static List<SlicerPrinter> Scan() => ReadSupportFolder(LocalSupport);

    private static List<SlicerPrinter> ReadSupportFolder(string supportFolder)
    {
        var found = new List<SlicerPrinter>();
        foreach (var (folder, title) in KnownApps)
        {
            found.AddRange(Read(Path.Combine(supportFolder, folder, $"{folder}.conf"), title));
        }
        return Merge(found);
    }

    /// <summary>One entry per printer, in the order first seen. When several slicers (or files) know the same printer,
    /// their codes are all kept, in order, so a stale code in one doesn't hide a working one in another; the address
    /// and source come from the first that has them.</summary>
    public static List<SlicerPrinter> Merge(IEnumerable<SlicerPrinter> printers)
    {
        var merged = new List<SlicerPrinter>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var printer in printers)
        {
            var serial = printer.Serial.ToUpperInvariant();
            if (!index.TryGetValue(serial, out var i))
            {
                index[serial] = merged.Count;
                merged.Add(printer with { Serial = serial });
                continue;
            }
            var first = merged[i];
            var codes = first.AccessCodes.ToList();
            foreach (var code in printer.AccessCodes) if (!codes.Contains(code)) codes.Add(code);
            merged[i] = first with { AccessCodes = codes, Host = first.Host ?? printer.Host, Name = first.Name ?? printer.Name };
        }
        return merged;
    }

    /// <summary>Reads a settings file, or a folder holding one (Bambu Studio's own folder, AppData, a user's folder or a
    /// whole disk, from Windows or a Mac), as picked or dropped by the person.</summary>
    public static List<SlicerPrinter> ReadPicked(string path)
    {
        if (File.Exists(path))
        {
            var source = Path.GetFileName(path).StartsWith("orca", StringComparison.OrdinalIgnoreCase) ? "OrcaSlicer" : "Bambu Studio";
            return Read(path, Loc.F("%@ settings file", source));
        }
        if (!Directory.Exists(path)) return [];
        string[] candidates =
        [
            path, Path.Combine(path, "AppData", "Roaming"), Path.Combine(path, "Library", "Application Support"),
            .. KnownApps.Select(a => Path.Combine(path, a.Folder)),
        ];
        foreach (var candidate in candidates)
        {
            // A slicer's own folder: the .conf sits right inside it.
            foreach (var (folder, title) in KnownApps)
            {
                var printers = Read(Path.Combine(candidate, $"{folder}.conf"), title);
                if (printers.Count > 0) return printers;
            }
            var found = ReadSupportFolder(candidate);
            if (found.Count > 0) return found;
        }
        // A whole disk.
        var users = Path.Combine(path, "Users");
        if (Directory.Exists(users))
        {
            foreach (var user in Directory.GetDirectories(users).Order(StringComparer.Ordinal))
            {
                foreach (var support in new[] { Path.Combine(user, "AppData", "Roaming"), Path.Combine(user, "Library", "Application Support") })
                {
                    var found = ReadSupportFolder(support);
                    if (found.Count > 0) return found;
                }
            }
        }
        return [];
    }

    /// <summary>Parses one settings file.</summary>
    public static List<SlicerPrinter> Read(string path, string source)
    {
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { return []; }
        // Drop anything after the JSON object (the "# MD5 checksum …" line some builds append).
        var end = text.LastIndexOf('}');
        if (end >= 0) text = text[..(end + 1)];
        Dictionary<string, object?>? json;
        try { json = PlainJson.Parse(text) as Dictionary<string, object?>; }
        catch (System.Text.Json.JsonException) { return []; }
        if (json is null) return [];

        // Each section maps serial → value. Slicers don't always agree with themselves on the serial's case (an
        // uppercase key in one section, lowercase in another, sometimes both in one), so keys are uppercased before
        // anything is matched up: one printer, whatever the spelling. Values under two spellings of the same serial
        // are all kept, the uppercase spelling's first.
        Dictionary<string, List<string>> Section(string name)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (Cast.Dict(json.GetValueOrDefault(name)) is not { } dict) return result;
            var keys = dict.Keys.OrderBy(k => k == k.ToUpperInvariant() ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var value = (Cast.AsString(dict[key]) ?? "").Trim();
                if (value.Length == 0) continue;
                var serial = key.Trim().ToUpperInvariant();
                if (serial.Length == 0) continue;
                if (!result.TryGetValue(serial, out var values)) result[serial] = values = [];
                if (!values.Contains(value)) values.Add(value);
            }
            return result;
        }
        var cloud = Section("access_code");
        var typed = Section("user_access_code");
        var uuid = Cast.AsString(Cast.Dict(json.GetValueOrDefault("app"))?.GetValueOrDefault("slicer_uuid")) ?? "";
        var ips = Section("ip_address");
        var typedIps = Section("user_access_dev_ip")
            .ToDictionary(p => p.Key, p => p.Value.Select(v => DecodeIp(v, uuid)).OfType<string>().ToList());
        var out_ = new List<SlicerPrinter>();
        foreach (var serial in cloud.Keys.Union(typed.Keys).Order(StringComparer.Ordinal))
        {
            var codes = new List<string>();
            foreach (var code in cloud.GetValueOrDefault(serial, []).Concat(typed.GetValueOrDefault(serial, [])))
                if (!codes.Contains(code)) codes.Add(code);
            if (codes.Count == 0) continue;
            var host = typedIps.GetValueOrDefault(serial)?.FirstOrDefault() ?? ips.GetValueOrDefault(serial)?.FirstOrDefault();
            out_.Add(new SlicerPrinter { Serial = serial, AccessCodes = codes, Host = host, Source = source });
        }
        return out_;
    }

    /// <summary>Undoes <c>BBLCrossTalk::Encode_DevIp</c> (BambuStudio <c>BBLUtil.cpp</c>): hex of the IP XORed with the
    /// low byte of successive <c>std::mt19937</c> outputs, seeded with the 32-bit FNV-1a hash of the slicer's UUID.
    /// Plain IPs (older versions, OrcaSlicer) come back as they are.</summary>
    public static string? DecodeIp(string value, string key)
    {
        static bool IsAddress(string text) =>
            text.Length is > 0 and <= 64 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-')
            && text.Any(c => c is '.' or ':');
        if (IsAddress(value) && value.Contains('.')) return value;
        if (key.Length == 0 || value.Length % 2 != 0 || !value.All(char.IsAsciiHexDigit)) return IsAddress(value) ? value : null;
        var rng = new MersenneTwister(Fnv1a32(Encoding.UTF8.GetBytes(key)));
        var bytes = new byte[value.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(Convert.ToByte(value.Substring(i * 2, 2), 16) ^ (byte)rng.Next());
        }
        var plain = Encoding.UTF8.GetString(bytes);
        return IsAddress(plain) ? plain : null;
    }

    /// <summary><c>BBLCrossTalk::Encode_DevIp</c>, the inverse of <see cref="DecodeIp"/> (tests and the fleet).</summary>
    public static string EncodeIp(string ip, string key)
    {
        var rng = new MersenneTwister(Fnv1a32(Encoding.UTF8.GetBytes(key)));
        return string.Concat(Encoding.UTF8.GetBytes(ip).Select(b => ((byte)(b ^ (byte)rng.Next())).ToString("x2")));
    }

    private static uint Fnv1a32(byte[] data)
    {
        var hash = 2_166_136_261u;
        foreach (var b in data) hash = unchecked((hash ^ b) * 16_777_619u);
        return hash;
    }
}

/// <summary>The 32-bit Mersenne Twister, matching C++'s <c>std::mt19937</c>.</summary>
public sealed class MersenneTwister
{
    private readonly uint[] _state = new uint[624];
    private int _index = 624;

    public MersenneTwister(uint seed)
    {
        _state[0] = seed;
        for (var i = 1; i < 624; i++) _state[i] = unchecked(1_812_433_253u * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i);
    }

    public uint Next()
    {
        if (_index >= 624)
        {
            for (var i = 0; i < 624; i++)
            {
                var y = (_state[i] & 0x8000_0000u) | (_state[(i + 1) % 624] & 0x7FFF_FFFFu);
                _state[i] = _state[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1) == 0 ? 0u : 0x9908_B0DFu);
            }
            _index = 0;
        }
        var x = _state[_index++];
        x ^= x >> 11;
        x ^= (x << 7) & 0x9D2C_5680u;
        x ^= (x << 15) & 0xEFC6_0000u;
        x ^= x >> 18;
        return x;
    }
}
