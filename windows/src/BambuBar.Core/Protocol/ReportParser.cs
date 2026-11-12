using System.Globalization;
using BambuBar.Core.Json;
using BambuBar.Core.Model;

namespace BambuBar.Core.Protocol;

using Obj = Dictionary<string, object?>;

/// <summary>Turns the merged report into a <see cref="PrinterStatus"/>. A port of the Mac's ReportParser.swift: keep
/// the two in step.</summary>
public static class ReportParser
{
    /// <summary><paramref name="serial"/> picks the wiki pages for this printer model; issues link to the HMS index
    /// without it.</summary>
    public static PrinterStatus Status(ReportDocument doc, HmsCatalog hms, string? serial = null)
    {
        var p = doc.Print;
        var s = new PrinterStatus { UpdatedAt = DateTimeOffset.Now };
        s.State = StatusText.FromGcodeState(String(p.Get("gcode_state")) ?? "IDLE");
        s.StageId = I(Int(p.Get("stg_cur")) ?? Int(Cast.Dict(p.Get("stage")).Get("_id")));
        if (s.StageId == 0 && String(p.Get("print_type")) == "idle" && !s.State.IsActive()) s.StageId = 255;
        s.TaskId = new[] { String(p.Get("task_id")), String(p.Get("subtask_id")) }
            .FirstOrDefault(t => t is { Length: > 0 } && t != "0");
        s.JobName = CleanJobName(String(p.Get("subtask_name")) ?? "", String(p.Get("gcode_file")) ?? "");
        s.Progress = (int)Math.Max(0, Math.Min(100, Int(p.Get("mc_percent")) ?? 0));
        s.RemainingMinutes = I(Math.Max(0, Int(p.Get("mc_remaining_time")) ?? 0));
        s.Layer = I(Int(p.Get("layer_num")) ?? 0);
        s.TotalLayers = I(Int(p.Get("total_layer_num")) ?? 0);

        // Temperatures: the newer `device` block packs current (low 16 bits) and target (high 16 bits).
        var device = Cast.Dict(p.Get("device"));
        var extruders = Extruders(device);
        if (Int(extruders.ById.GetValueOrDefault(0).Get("temp")) is { } packed)
        {
            s.Nozzle = Unpack(packed);
            if (Int(extruders.ById.GetValueOrDefault(1).Get("temp")) is { } left)
            {
                s.SecondNozzle = Unpack(left);
                s.LeftNozzleInUse = extruders.Current == 1;
            }
        }
        else
        {
            s.Nozzle = new(Double(p.Get("nozzle_temper")) ?? 0, Double(p.Get("nozzle_target_temper")) ?? 0);
        }
        if ((Int(Cast.Dict(Cast.Dict(device.Get("bed")).Get("info")).Get("temp")) ?? Int(device.Get("bed_temp"))) is { } bed)
        {
            s.Bed = Unpack(bed);
        }
        else
        {
            s.Bed = new(Double(p.Get("bed_temper")) ?? 0, Double(p.Get("bed_target_temper")) ?? 0);
        }
        if (Int(Cast.Dict(Cast.Dict(device.Get("ctc")).Get("info")).Get("temp")) is { } ctc)
        {
            var chamber = Unpack(ctc);
            s.Chamber = chamber.Current;
            s.ChamberTarget = chamber.Target;
        }
        else if ((Double(Cast.Dict(p.Get("info")).Get("temp")) ?? Double(p.Get("chamber_temper"))) is { } t)
        {
            s.Chamber = t;
            // The X1E's heater target, in the older report format (Bambu Studio's DevChamber reads it too).
            s.ChamberTarget = Double(p.Get("ctt"));
        }
        if (s.Chamber is { } c && (c <= 0 || c > 120)) { s.Chamber = null; s.ChamberTarget = null; }

        // Fans: the `airduct` parts already report percent; the legacy fields are 0-15. Bambu Studio names a part by
        // bits 4-11 of its id: 1 part cooling, 2 aux, 3 exhaust; the H3C adds 10 (left aux) and 15 (chamber).
        if (Cast.DictArray(Cast.Dict(device.Get("airduct")).Get("parts")) is { Count: > 0 } parts)
        {
            foreach (var part in parts)
            {
                if (Int(part.Get("id")) is not { } id || Int(part.Get("state")) is not { } state) continue;
                var pct = (int)Math.Max(0, Math.Min(100, state & 0xFF));
                switch ((id >> 4) & 0xFF)
                {
                    case 1: s.PartFan = pct; break;
                    case 2: s.AuxFan = pct; break;
                    case 3: s.ExhaustFan = pct; break;
                    case 10: s.LeftAuxFan = pct; break;
                    case 15: s.CirculationFan = pct; break;
                }
            }
        }
        else
        {
            // Every model sends all three, "0" for fans it doesn't have (an A1 has no aux or chamber fan); the
            // printer's model drops those (PrinterStatus.FitTo), so a stopped fan that exists still shows.
            s.PartFan = Fan(p.Get("cooling_fan_speed"));
            s.AuxFan = Fan(p.Get("big_fan1_speed"));
            s.ExhaustFan = Fan(p.Get("big_fan2_speed"));
        }
        s.PartFan ??= Fan(p.Get("cooling_fan_speed"));
        s.HeatbreakFan = Fan(p.Get("heatbreak_fan_speed"));

        var speed = Int(p.Get("spd_lvl")) ?? 2;
        s.Speed = speed is >= 1 and <= 4 ? (SpeedLevel)speed : SpeedLevel.Standard;
        if (Cast.DictArray(p.Get("lights_report")) is { } lights
            && lights.FirstOrDefault(l => String(l.Get("node")) == "chamber_light") is { } light)
        {
            s.ChamberLightOn = String(light.Get("mode")) != "off";
        }
        // The door, only from printers that say they have a door sensor (`fun` bit 12, as Bambu Studio checks):
        // others leave the bit at 0, which would read as a door that's always closed.
        var fun = Cast.ParseHex(String(p.Get("fun")));
        if (fun is { } f && (f & (1UL << 12)) != 0 && Cast.ParseHex(String(p.Get("stat"))) is { } stat)
        {
            s.DoorOpen = (stat & (1UL << 23)) != 0;
        }
        if (String(p.Get("wifi_signal")) is { } wifi && Cast.ParseSwiftInt(wifi.Replace("dBm", "", StringComparison.Ordinal)) is { } dbm)
        {
            s.WifiSignal = I(dbm);
        }
        if (fun is { } bits) s.ControlsLocked = (bits & 0x2000_0000UL) != 0;
        s.HasSdCard = Cast.AsBool(p.Get("sdcard"));
        s.PlateIndex = I(Int(p.Get("plate_idx")));
        if (Cast.Dict(p.Get("ipcam")) is { } ipcam && String(ipcam.Get("rtsp_url")) is { } url)
        {
            s.LiveViewEnabled = url != "disable" && url.Length > 0;
        }
        // The nozzle on the printing extruder: its `id` is the extruder's. The H3C also lists the hotends in its rack
        // (ids 16 and up), in rack order, so the first entry isn't necessarily the mounted one.
        if (Cast.DictArray(Cast.Dict(device.Get("nozzle")).Get("info")) is { } nozzles
            && (nozzles.FirstOrDefault(n => Int(n.Get("id")) == extruders.Current) ?? nozzles.FirstOrDefault()) is { } nozzle)
        {
            s.NozzleDiameter = Double(nozzle.Get("diameter"));
            s.NozzleType = String(nozzle.Get("type"));
        }
        else
        {
            s.NozzleDiameter = Double(p.Get("nozzle_diameter"));
            s.NozzleType = String(p.Get("nozzle_type"));
        }
        if (Cast.Dict(p.Get("upgrade_state")) is { } upgrade && Int(upgrade.Get("new_version_state")) == 1)
        {
            s.UpdateAvailable = true;
        }
        s.Firmware = doc.Modules.TryGetValue("ota", out var ota) ? ota.Version : null;

        ParseFilament(p, device, doc, s);
        s.Issues = Issues(p, hms, serial);
        s.Canceled = s.State == JobState.Failed && WasCanceled(doc);
        return s;
    }

    // MARK: Filament

    private static void ParseFilament(Obj p, Obj? device, ReportDocument doc, PrinterStatus s)
    {
        var amsRoot = Cast.Dict(p.Get("ams"));
        var extruders = Extruders(device);
        var units = Cast.DictArray(amsRoot.Get("ams")) ?? [];
        var existBits = Cast.ParseHex(String(amsRoot.Get("tray_exist_bits")));
        var rfidBits = Cast.ParseHex(String(amsRoot.Get("tray_is_bbl_bits")));
        var parsed = new List<AmsUnit>();
        foreach (var u in units)
        {
            if (Int(u.Get("id")) is not { } longId) continue;
            var id = I(longId);
            var type = AmsTypeOf(id, String(u.Get("info")), doc.Modules);
            var kind = KindOf(type);
            var spools = new List<Spool>();
            foreach (var t in Cast.DictArray(u.Get("tray")) ?? [])
            {
                if (Int(t.Get("id")) is not { } longSlot) continue;
                var slot = I(longSlot);
                var spool = SpoolFrom(t, SpoolId(type, id, slot), id, slot);
                if (TrayBit(type, id, slot) is { } bit && bit is >= 0 and < 64)
                {
                    if (existBits is { } exist && (exist & (1UL << bit)) == 0) spool.IsLoaded = false;
                    // Only ever narrows the tag check: the P2S sets every bit, Bambu spool or not.
                    if (rfidBits is { } rfid && (rfid & (1UL << bit)) == 0) spool.IsBambu = false;
                }
                spools.Add(Describe(spool));
            }
            // The AMS 3H measures each of its drying zones instead of the whole unit; show the dampest one.
            var zones = Cast.DictArray(u.Get("zone_info")) ?? [];
            var sensor = u.ContainsKey("humidity_raw") || u.ContainsKey("humidity") || zones.Count == 0
                ? u
                : zones.Aggregate((best, z) => (Int(best.Get("humidity_raw")) ?? -1) < (Int(z.Get("humidity_raw")) ?? -1) ? z : best);
            var humidity = Int(sensor.Get("humidity_raw"));
            if (humidity is < 1 or > 100) humidity = null;
            var level = Int(sensor.Get("humidity"));
            if (level is < 1 or > 5) level = null;
            var temp = Double(sensor.Get("temp"));
            if (temp is <= 0 or > 100) temp = null;
            // The AMS Lite has no humidity or temperature sensor; it reports "5" (the driest level) and "0.0".
            if (kind == AmsKind.Lite) { humidity = null; level = null; temp = null; }
            parsed.Add(new AmsUnit
            {
                Id = id, Kind = kind, Humidity = I(humidity), HumidityLevel = level is { } l ? (int)(6 - l) : null,
                Temperature = temp, DryingMinutesLeft = I(Int(u.Get("dry_time")) ?? 0),
                Spools = spools.OrderBy(sp => sp.Slot).ToList(),
            });
        }
        s.Ams = parsed.OrderBy(u => u.Id).ToList();

        // The external spool of the printing nozzle: Bambu Studio numbers the virtual slots 255 down, one per extruder
        // (255 right, 254 left on an H2D) or, on the H3C, one per hotend rack position (255 - addr_id, 247-255).
        var slots = Cast.DictArray(p.Get("vir_slot"));
        var nozzles = Cast.DictArray(Cast.Dict(device.Get("nozzle")).Get("info")) ?? [];
        var mountedAddr = Int(nozzles.FirstOrDefault(n => Int(n.Get("id")) == extruders.Current).Get("addr_id"));
        var wanted = 255 - (mountedAddr ?? extruders.Current);
        var virtualSlot = slots?.FirstOrDefault(v => Int(v.Get("id")) == wanted) ?? slots?.FirstOrDefault() ?? Cast.Dict(p.Get("vt_tray"));
        if (virtualSlot is not null)
        {
            var ext = SpoolFrom(virtualSlot, Spool.ExternalId, 255, 0);
            ext.IsLoaded = (String(virtualSlot.Get("tray_type")) ?? "").Length > 0;
            // Nothing weighs the spool on the external holder; its `remain` is a placeholder.
            ext.Remain = null;
            s.ExternalSpool = Describe(ext);
        }

        // Active slot: the printing extruder's `snow` (unit << 8 | slot) on newer firmware, else `tray_now`.
        if (Int(extruders.ById.GetValueOrDefault((int)extruders.Current).Get("snow")) is { } snow && snow != 0xFFFF)
        {
            long unit = snow >> 8, slot = snow & 0xFF;
            if (unit is >= 247 and <= 255)
                s.ActiveSpoolId = slot == 255 ? null : Spool.ExternalId;
            else if (s.Ams.FirstOrDefault(a => a.Id == unit)?.Spools.FirstOrDefault(sp => sp.Slot == slot) is { } spool)
                s.ActiveSpoolId = spool.Id;
            else if (unit >= 128)
                s.ActiveSpoolId = I(unit);
            else
                s.ActiveSpoolId = I(unit * 4 + (slot & 0x3));
        }
        else if (Int(amsRoot.Get("tray_now")) is { } now)
        {
            s.ActiveSpoolId = now switch { 255 => null, 254 => Spool.ExternalId, _ => I(now) };
        }
        // Only meaningful while filament is actually in use.
        if (!s.State.IsActive() && s.ActiveSpoolId is not null && s.ActiveSpool?.IsLoaded != true) s.ActiveSpoolId = null;
    }

    private static Spool SpoolFrom(Obj t, int id, int unit, int slot)
    {
        var material = String(t.Get("tray_type")) ?? "";
        var state = Int(t.Get("state"));
        var loaded = material.Length > 0;
        if (state is > 3 and { } st) loaded = ((st & 0x01) != 0 && (st & 0x08) != 0) || st == 9;
        var color = String(t.Get("tray_color")) ?? "00000000";
        var colors = Cast.StringArray(t.Get("cols"))?.Where(c => c.Length >= 6).ToList() ?? [];
        var remain = Int(t.Get("remain"));
        if (remain is < 0 or > 100) remain = null;
        var tag = String(t.Get("tag_uid")) ?? "";
        var weight = Int(t.Get("tray_weight"));
        if (weight is <= 0 or > 10_000) weight = null;
        var preset = String(t.Get("tray_info_idx")) is { Length: > 0 } idx ? idx : null;
        var spool = new Spool
        {
            Id = id, Unit = unit, Slot = slot, Material = material, Name = String(t.Get("tray_sub_brands")) ?? "",
            Color = color, Colors = colors.Count == 0 ? [color] : colors, Remain = I(remain), Weight = I(weight),
            IsLoaded = loaded, IsBambu = tag.Length > 0 && tag.Any(ch => ch != '0'), Preset = preset,
        };
        spool.ColorName = FilamentCatalog.ColorName(String(t.Get("tray_id_name")) ?? "", spool.Preset, color);
        if (Int(t.Get("nozzle_temp_min")) is { } low && Int(t.Get("nozzle_temp_max")) is { } high && low > 0 && high >= low)
        {
            spool.NozzleRange = new(I(low), I(high));
        }
        return spool;
    }

    /// <summary>Fills in the maker and product from the slot's preset once it's known whether the spool has a Bambu
    /// tag. A spool without one is whatever the slot was set to on the printer or in Bambu Studio.</summary>
    private static Spool Describe(Spool spool)
    {
        if (!spool.IsLoaded)
        {
            return new Spool
            {
                Id = spool.Id, Unit = spool.Unit, Slot = spool.Slot, Material = "", Name = "", Color = spool.Color,
                Colors = spool.Colors, Remain = null, IsLoaded = false, IsBambu = false,
            };
        }
        var preset = spool.Preset is { } id ? FilamentCatalog.Preset(id) : null;
        spool.Brand = spool.IsBambu ? "Bambu Lab" : preset?.Vendor;
        if (spool.Name.Length == 0 && preset?.Product is { } product
            && !string.Equals(product, spool.Material, StringComparison.OrdinalIgnoreCase))
        {
            spool.Name = product;
        }
        return spool;
    }

    /// <summary>Bambu Studio's AMS type (<c>DevAmsType</c>, bits 0-3 of the unit's <c>info</c>), which also places the
    /// unit's slots in <c>tray_exist_bits</c>.</summary>
    private enum AmsType
    {
        Ams, Lite, Pro2, Ht,
        /// <summary>The A2L's AMS Lite (type 5).</summary>
        LiteMixed,
        /// <summary>The H3C's AMS 3H (type 6, ZMULTI_A).</summary>
        Ams3H,
        /// <summary>The H3C's AMS-B (type 7, ZMULTI_B), for hotend rack B. Not seen in a report yet.</summary>
        AmsB,
    }

    private static AmsKind KindOf(AmsType t) => t switch
    {
        AmsType.Ams => AmsKind.Ams,
        AmsType.Lite or AmsType.LiteMixed => AmsKind.Lite,
        AmsType.Pro2 => AmsKind.Pro2,
        AmsType.Ht => AmsKind.Ht,
        AmsType.Ams3H => AmsKind.Ams3H,
        _ => AmsKind.AmsB,
    };

    /// <summary>The slot's bit in <c>tray_exist_bits</c> and <c>tray_is_bbl_bits</c> (<c>DevAms::GetTrayId</c>).</summary>
    private static int? TrayBit(AmsType t, int unit, int slot) => t switch
    {
        AmsType.Ams or AmsType.Lite or AmsType.Pro2 => unit * 4 + slot,
        AmsType.Ht => unit >= 128 ? 16 + (unit - 128) + slot : null,
        AmsType.LiteMixed => 24 + slot,
        AmsType.Ams3H => 28 + slot,
        _ => 37 + slot,
    };

    /// <summary>The app's spool id: unit * 4 + slot for the four-slot units, the unit id for an AMS HT (one slot), and
    /// past both ranges for the H3C's rack AMS.</summary>
    private static int SpoolId(AmsType t, int unit, int slot) => t switch
    {
        AmsType.Ht when unit >= 128 => unit,
        AmsType.Ams3H => Spool.Ams3HBase + slot,
        AmsType.AmsB => Spool.AmsBBase + slot,
        _ => unit * 4 + slot,
    };

    private static AmsType AmsTypeOf(int id, string? info, Dictionary<string, (string, string)> modules)
    {
        var type = Cast.ParseHex(info) is { } bits ? (ulong?)(bits & 0xF) : null;
        if (type == 6 || modules.ContainsKey($"n10a/{id}")) return AmsType.Ams3H;
        if (type == 7) return AmsType.AmsB;
        if (type == 5) return AmsType.LiteMixed;
        if (id >= 128 || modules.ContainsKey($"n3s/{id}")) return AmsType.Ht;
        if (modules.ContainsKey($"n3f/{id}")) return AmsType.Pro2;
        if (modules.ContainsKey($"ams_f1/{id}")) return AmsType.Lite;
        if (modules.ContainsKey($"ams/{id}")) return AmsType.Ams;
        return type switch { 2 => AmsType.Lite, 3 => AmsType.Pro2, 4 => AmsType.Ht, _ => AmsType.Ams };
    }

    // MARK: Issues

    private static List<PrinterIssue> Issues(Obj p, HmsCatalog catalog, string? serial)
    {
        var out_ = new List<PrinterIssue>();
        foreach (var entry in Cast.DictArray(p.Get("hms")) ?? [])
        {
            if (Int(entry.Get("attr")) is not { } attr || Int(entry.Get("code")) is not { } code) continue;
            uint a = unchecked((uint)attr), c = unchecked((uint)code);
            if ((c & 0xFFFF) < 0x4000 && c >> 16 == 0) continue;
            var key = $"{a:X8}{c:X8}";
            var pretty = $"HMS_{a >> 16:X4}_{a & 0xFFFF:X4}_{c >> 16:X4}_{c & 0xFFFF:X4}";
            var severity = (c >> 16) is >= 1 and <= 4 ? (IssueSeverity)(c >> 16) : IssueSeverity.Common;
            out_.Add(new PrinterIssue
            {
                Id = pretty, Severity = severity,
                Message = catalog.HmsText(key) ?? Loc.F("The printer reported %@.", pretty),
                WikiUrl = HmsCatalog.WikiUrl(key, serial),
            });
        }
        if (Int(p.Get("print_error")) is { } error && error != 0)
        {
            var e = unchecked((uint)error);
            var key = $"{e:X8}";
            // 0300_400C and 0500_400E are the printer's echo of a user cancel, not a fault.
            if (key is not ("0300400C" or "0500400E"))
            {
                var pretty = $"{e >> 16:X4}_{e & 0xFFFF:X4}";
                var level = (e & 0xF000) >> 12;
                var severity = level == 0x4 ? IssueSeverity.Fatal : level == 0x8 ? IssueSeverity.Serious : IssueSeverity.Common;
                if (!out_.Exists(i => i.Id.EndsWith(pretty, StringComparison.Ordinal)))
                {
                    out_.Add(new PrinterIssue
                    {
                        Id = pretty, Severity = severity,
                        Message = catalog.ErrorText(key) ?? Loc.F("The printer reported error %@.", pretty),
                        WikiUrl = HmsCatalog.WikiUrl(key, serial),
                    });
                }
            }
        }
        return out_.OrderBy(i => i.Severity).ToList();
    }

    /// <summary>True when <c>print_error</c> is the code for a job the user cancelled.</summary>
    public static bool WasCanceled(ReportDocument doc)
    {
        if (Int(doc.Print.Get("print_error")) is not { } e) return false;
        var key = $"{unchecked((uint)e):X8}";
        return key is "0300400C" or "0500400E";
    }

    // MARK: Helpers

    /// <summary>Bambu Studio names jobs after the 3MF; strip path and extensions for display.</summary>
    public static string CleanJobName(string name, string file)
    {
        var n = name.Trim();
        if (n.Length == 0)
        {
            n = LastPathComponent(file);
            if (n.StartsWith("plate_", StringComparison.Ordinal)) n = "";
        }
        foreach (var ext in new[] { ".gcode.3mf", ".3mf", ".gcode" })
        {
            if (n.ToLowerInvariant().EndsWith(ext, StringComparison.Ordinal)) n = n[..^ext.Length];
        }
        // Bambu Studio turns spaces into underscores when it names the job after the project file.
        if (!n.Contains(' ')) n = n.Replace('_', ' ');
        return n;
    }

    /// <summary>NSString's <c>lastPathComponent</c>: the part after the last "/", ignoring trailing slashes.</summary>
    private static string LastPathComponent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0) return path.Length > 0 ? "/" : "";
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>The newer firmware's <c>device.extruder</c> block. Bambu Studio keys each extruder by its <c>id</c> (0 is
    /// the right nozzle, 1 the left on two-nozzle models), not by its place in the list; bits 4-7 of <c>state</c> say
    /// which one is printing.</summary>
    private static (Dictionary<long, Obj> ById, long Current) Extruders(Obj? device)
    {
        var root = Cast.Dict(device.Get("extruder"));
        var byId = new Dictionary<long, Obj>();
        var list = Cast.DictArray(root.Get("info")) ?? [];
        for (var i = 0; i < list.Count; i++) byId[Int(list[i].Get("id")) ?? i] = list[i];
        var current = Int(root.Get("state")) is { } st ? (st >> 4) & 0xF : 0;
        return (byId, byId.ContainsKey(current) ? current : 0);
    }

    private static Temperature Unpack(long packed) =>
        // Plain values (no target packed in) stay below 0x10000; a heater that's off reports target 0.
        new(packed & 0xFFFF, (packed >> 16) & 0xFFFF);

    private static int? Fan(object? value)
    {
        if (Int(value) is not { } raw) return null;
        if (raw <= 15) return I((long)Math.Round(raw / 15.0 * 100, MidpointRounding.AwayFromZero));
        return (int)Math.Min(100, (long)Math.Round(raw / 255.0 * 100, MidpointRounding.AwayFromZero));
    }

    /// <summary>A whole number from a number, a numeric string or a bool; null for anything else, "nan", "inf" or
    /// numbers past the 64-bit range. Fractions are cut toward zero.</summary>
    public static long? Int(object? value) => value switch
    {
        double d => Cast.ExactLong(Math.Truncate(d)),
        string text => Cast.ParseSwiftInt(text) ?? (Cast.ParseSwiftDouble(text) is { } d ? Cast.ExactLong(Math.Truncate(d)) : null),
        _ => Cast.AsInt(value),
    };

    /// <summary>Finite numbers only. "nan" parses, slips past every range check (comparisons with NaN are false) and
    /// breaks JSON writers downstream.</summary>
    public static double? Double(object? value)
    {
        var result = value is string text ? Cast.ParseSwiftDouble(text) : Cast.AsDouble(value);
        return result is { } d && double.IsFinite(d) ? d : null;
    }

    public static string? String(object? value) => Cast.Describe(value);

    /// <summary>Swift's 64-bit Int stored in a 32-bit field: values past its range are clamped (no printer sends them).</summary>
    private static int I(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    private static int? I(long? value) => value is { } v ? I(v) : null;

    private static object? Get(this Obj? dict, string key) => dict is not null && dict.TryGetValue(key, out var v) ? v : null;
}
