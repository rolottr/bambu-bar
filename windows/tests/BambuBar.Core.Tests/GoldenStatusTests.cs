using System.Text.Json.Nodes;
using BambuBar.Core.Json;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>
/// The Windows parser shows exactly what the Mac's does. shared/ModelReports/expected holds the Mac app's output for
/// every fleet fixture and captured sample, written by the real app with its bundled catalogs:
/// <code>"build/App.noindex/Bambu Bar.app/Contents/MacOS/Bambu Bar" --dump-status shared/ModelReports/expected \
///     shared/ModelReports/*.json shared/scripts/emulator/*-sample.json -AppleLanguages '(en)' -AppleLocale en_US</code>
/// Regenerate them whenever the Mac parser or the fixtures change.
/// </summary>
public class GoldenStatusTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Repo.Shared("ModelReports", "expected"), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesTheMac(string name)
    {
        var input = File.Exists(Repo.Shared("ModelReports", $"{name}.json"))
            ? Repo.Shared("ModelReports", $"{name}.json")
            : Repo.Shared("scripts", "emulator", $"{name}.json");
        var json = Assert.IsType<Dictionary<string, object?>>(PlainJson.Parse(File.ReadAllText(input)));
        var doc = new ReportDocument();
        var serial = json.GetValueOrDefault("serial") as string ?? "";
        PrinterModel? model;
        if (json.GetValueOrDefault("print") is Dictionary<string, object?> print)
        {
            doc.Apply(new() { ["print"] = print });
            model = PrinterModel.Find(json.GetValueOrDefault("model") as string ?? "", serial);
        }
        else
        {
            if (json.GetValueOrDefault("get_version") is Dictionary<string, object?> version) doc.Apply(version);
            if (json.GetValueOrDefault("pushall") is Dictionary<string, object?> pushall) doc.Apply(pushall);
            model = PrinterModel.Named(name.Replace("-sample", ""));
        }
        var status = ReportParser.Status(doc, new HmsCatalog("en"), serial.Length == 0 ? null : serial);
        status.FitTo(model);

        var expected = JsonNode.Parse(File.ReadAllText(Repo.Shared("ModelReports", "expected", $"{name}.json")));
        var differences = new List<string>();
        // Through text, so both sides hold parsed JSON values and numbers compare the same way.
        Compare(expected, JsonNode.Parse(Golden(status).ToJsonString()), "$", differences);
        Assert.True(differences.Count == 0, $"{name} differs from the Mac:\n" + string.Join("\n", differences));
    }

    /// <summary>The same shape as the Mac's <c>Probe.golden</c>: Swift names, every optional present.</summary>
    private static JsonObject Golden(PrinterStatus s)
    {
        static JsonNode? Temp(Temperature? t) => t is { } v ? new JsonObject { ["current"] = v.Current, ["target"] = v.Target } : null;
        static JsonNode? SpoolNode(Spool? sp) => sp is null ? null : new JsonObject
        {
            ["id"] = sp.Id, ["unit"] = sp.Unit, ["slot"] = sp.Slot, ["material"] = sp.Material, ["name"] = sp.Name,
            ["color"] = sp.Color, ["colors"] = new JsonArray([.. sp.Colors.Select(c => (JsonNode?)c)]),
            ["remain"] = sp.Remain, ["weight"] = sp.Weight, ["isLoaded"] = sp.IsLoaded, ["isBambu"] = sp.IsBambu,
            ["preset"] = sp.Preset, ["brand"] = sp.Brand, ["colorName"] = sp.ColorName,
            ["nozzleRange"] = sp.NozzleRange is { } r ? new JsonArray(r.Low, r.High) : null, ["label"] = sp.Label,
            ["gramsLeft"] = sp.GramsLeft,
        };
        static string KindRaw(AmsKind k) => k switch
        {
            AmsKind.Ams => "ams", AmsKind.Lite => "lite", AmsKind.Pro2 => "pro2", AmsKind.Ht => "ht",
            AmsKind.Ams3H => "ams3h", _ => "amsB",
        };
        return new JsonObject
        {
            ["state"] = s.State.ToString().ToLowerInvariant(), ["stageID"] = s.StageId, ["jobName"] = s.JobName,
            ["progress"] = s.Progress, ["remainingMinutes"] = s.RemainingMinutes, ["layer"] = s.Layer,
            ["totalLayers"] = s.TotalLayers, ["nozzle"] = Temp(s.Nozzle), ["bed"] = Temp(s.Bed), ["chamber"] = s.Chamber,
            ["chamberTarget"] = s.ChamberTarget, ["secondNozzle"] = Temp(s.SecondNozzle),
            ["leftNozzleInUse"] = s.LeftNozzleInUse, ["partFan"] = s.PartFan, ["auxFan"] = s.AuxFan,
            ["exhaustFan"] = s.ExhaustFan, ["heatbreakFan"] = s.HeatbreakFan, ["leftAuxFan"] = s.LeftAuxFan,
            ["circulationFan"] = s.CirculationFan, ["speed"] = (int)s.Speed, ["chamberLightOn"] = s.ChamberLightOn,
            ["doorOpen"] = s.DoorOpen, ["wifiSignal"] = s.WifiSignal,
            ["ams"] = new JsonArray([.. s.Ams.Select(u => (JsonNode?)new JsonObject
            {
                ["id"] = u.Id, ["kind"] = KindRaw(u.Kind), ["title"] = u.Title, ["humidity"] = u.Humidity,
                ["humidityLevel"] = u.HumidityLevel, ["temperature"] = u.Temperature,
                ["dryingMinutesLeft"] = u.DryingMinutesLeft,
                ["spools"] = new JsonArray([.. u.Spools.Select(SpoolNode)]),
            })]),
            ["externalSpool"] = SpoolNode(s.ExternalSpool), ["activeSpoolID"] = s.ActiveSpoolId,
            ["issues"] = new JsonArray([.. s.Issues.Select(i => (JsonNode?)new JsonObject
            {
                ["id"] = i.Id, ["severity"] = (int)i.Severity, ["message"] = i.Message,
            })]),
            ["controlsLocked"] = s.ControlsLocked, ["firmware"] = s.Firmware, ["nozzleDiameter"] = s.NozzleDiameter,
            ["nozzleType"] = s.NozzleType, ["liveViewEnabled"] = s.LiveViewEnabled, ["hasSDCard"] = s.HasSdCard,
            ["plateIndex"] = s.PlateIndex, ["updateAvailable"] = s.UpdateAvailable, ["taskID"] = s.TaskId,
            ["canceled"] = s.Canceled, ["stateTitle"] = s.StateTitle, ["detailText"] = s.DetailText,
            ["activeSpoolLabel"] = s.ActiveSpool?.Label,
        };
    }

    /// <summary>Structural comparison; numbers compare as doubles (Swift may write 25.2 as 25.199999999999999).</summary>
    private static void Compare(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        switch (expected, actual)
        {
            case (null, null):
                return;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (!e.ContainsKey(key)) differences.Add($"{path}.{key}: not in the Mac's output");
                    else if (!a.ContainsKey(key)) differences.Add($"{path}.{key}: missing");
                    else Compare(e[key], a[key], $"{path}.{key}", differences);
                }
                return;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count) { differences.Add($"{path}: {a.Count} items, the Mac has {e.Count}"); return; }
                for (var i = 0; i < e.Count; i++) Compare(e[i], a[i], $"{path}[{i}]", differences);
                return;
            case (JsonValue e, JsonValue a):
                if (e.TryGetValue<double>(out var ed) && a.TryGetValue<double>(out var ad))
                {
                    if (ed != ad) differences.Add($"{path}: {ad}, the Mac has {ed}");
                }
                else if (e.ToJsonString() != a.ToJsonString())
                {
                    differences.Add($"{path}: {a.ToJsonString()}, the Mac has {e.ToJsonString()}");
                }
                return;
            default:
                differences.Add($"{path}: {actual?.ToJsonString() ?? "null"}, the Mac has {expected?.ToJsonString() ?? "null"}");
                return;
        }
    }
}
