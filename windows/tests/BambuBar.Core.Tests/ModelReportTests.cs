using BambuBar.Core.Json;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>
/// Every Bambu Lab model's report, as the mock fleet sends it (shared/ModelReports, written by
/// <c>python3 shared/scripts/fleet/fleet.py --dump shared/ModelReports</c>), through the parser and the model's
/// capabilities: the app shows what the printer has and nothing it doesn't, placeholders included (a P1S sends a
/// chamber temperature of 5 without a sensor; an A1 sends "0" for fans it doesn't have). Ported from the Mac's
/// ModelReportTests.swift.
/// </summary>
public class ModelReportTests
{
    internal sealed record Report(PrinterModel Model, PrinterStatus Status, Dictionary<string, object?> Print);

    internal static List<Report> Reports()
    {
        var files = Directory.GetFiles(Repo.Shared("ModelReports"), "*.json").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);
        return files.Select(file =>
        {
            var json = Assert.IsType<Dictionary<string, object?>>(PlainJson.Parse(File.ReadAllText(file)));
            var code = Assert.IsType<string>(json["model"]);
            var serial = Assert.IsType<string>(json["serial"]);
            var model = PrinterModel.Find(code, serial);
            Assert.True(model is not null, $"{code} isn't in PrinterModel.All");
            Assert.True(model.Code == code, $"{serial} found as {model.Name}");
            var doc = new ReportDocument();
            doc.Apply(new() { ["print"] = Assert.IsType<Dictionary<string, object?>>(json["print"]) });
            var status = ReportParser.Status(doc, HmsCatalog.Shared);
            status.FitTo(model);
            return new Report(model, status, doc.Print);
        }).ToList();
    }

    [Fact]
    public void EveryModelHasAReport()
    {
        var covered = Reports().Select(r => r.Model.Code).ToHashSet();
        Assert.Equal(PrinterModel.All.Select(m => m.Code).ToHashSet(), covered);
    }

    [Fact]
    public void OnlyWhatTheModelHasShows()
    {
        foreach (var (model, s, _) in Reports())
        {
            var name = model.Name;
            Assert.True(s.Chamber is not null == model.HasChamberSensor, $"{name}: chamber temperature");
            Assert.True(s.ChamberTarget is not null == model.ChamberHeaterMax is not null, $"{name}: chamber heater target");
            Assert.True(s.AuxFan is not null == model.HasAuxFan, $"{name}: aux fan");
            Assert.True(s.ExhaustFan is not null == model.HasChamberFan, $"{name}: chamber fan");
            Assert.True(s.PartFan is not null, $"{name}: part cooling fan");
            Assert.True(s.ChamberLightOn is not null == model.HasChamberLight, $"{name}: chamber light");
            Assert.True(s.SecondNozzle is not null == (model.NozzleCount == 2), $"{name}: second nozzle");
            if (!model.IsEnclosed) Assert.True(s.DoorOpen is null, $"{name}: door on an open printer");
            foreach (var unit in s.Ams.Where(u => u.Kind == AmsKind.Lite))
            {
                Assert.True(unit.Humidity is null, $"{name}: AMS Lite humidity");
                Assert.True(unit.HumidityLevel is null, $"{name}: AMS Lite humidity level");
                Assert.True(unit.Temperature is null, $"{name}: AMS Lite temperature");
            }
            Assert.True(s.ExternalSpool is not null, $"{name}: external spool (vt_tray or vir_slot)");
            Assert.True(s.Nozzle.Target > 0, $"{name}: printing nozzle target");
        }
    }

    /// <summary>The door shows only for printers that say they have a sensor (<c>fun</c> bit 12), as Bambu Studio checks.</summary>
    [Fact]
    public void DoorFollowsTheSensorFlag()
    {
        foreach (var (model, s, print) in Reports())
        {
            var fun = Cast.ParseHex(print.GetValueOrDefault("fun") as string) ?? 0;
            Assert.True(s.DoorOpen is not null == ((fun & (1UL << 12)) != 0 && print.ContainsKey("stat")), model.Name);
        }
    }

    /// <summary>The X1E reports its chamber heater in the flat format: <c>chamber_temper</c> and <c>ctt</c>.</summary>
    [Fact]
    public void X1EChamberTargetFromTheFlatFormat()
    {
        var x1e = Reports().Single(r => r.Model.Code == "C13");
        Assert.False(x1e.Print.ContainsKey("device"));
        Assert.NotNull(x1e.Status.Chamber);
        Assert.NotNull(x1e.Status.ChamberTarget);
    }

    /// <summary>A real H3C report (shared/scripts/emulator/h3c-sample.json, idle, from ha-bambulab issue 2209): its AMS
    /// 3H has nine slots and measures humidity per drying zone, nine hotends sit in its rack, nine virtual slots, five
    /// fans.</summary>
    [Fact]
    public void RealH3CReport()
    {
        var json = Assert.IsType<Dictionary<string, object?>>(
            PlainJson.Parse(File.ReadAllText(Repo.Shared("scripts", "emulator", "h3c-sample.json"))));
        var doc = new ReportDocument();
        doc.Apply(Assert.IsType<Dictionary<string, object?>>(json["get_version"]));
        doc.Apply(Assert.IsType<Dictionary<string, object?>>(json["pushall"]));
        var s = ReportParser.Status(doc, HmsCatalog.Shared);
        s.FitTo(PrinterModel.Find("N10"));

        var unit = Assert.Single(s.Ams);
        Assert.Equal(AmsKind.Ams3H, unit.Kind);
        Assert.Equal("AMS 3H", unit.Title);
        Assert.Equal(Enumerable.Range(0, 9), unit.Spools.Select(sp => sp.Slot));
        Assert.DoesNotContain(unit.Spools, sp => sp.IsLoaded);
        int[] otherIds = [Spool.ExternalId, .. Enumerable.Range(0, 16), .. Enumerable.Range(128, 8)];
        Assert.Empty(unit.Spools.Select(sp => sp.Id).Intersect(otherIds));
        // The dampest of its two zones: 46 %, level 1 of Bambu Studio's 5 (wettest) shown as 5, at 25.2 °C.
        Assert.Equal(46, unit.Humidity);
        Assert.Equal(5, unit.HumidityLevel);
        Assert.Equal(25.2, unit.Temperature);

        Assert.Equal(22, s.Chamber);
        Assert.Equal(false, s.DoorOpen);
        Assert.Equal(false, s.ChamberLightOn);
        Assert.Equal("HS01", s.NozzleType);
        Assert.Equal(0.4, s.NozzleDiameter);
        Assert.Null(s.SecondNozzle);
        foreach (var (name, fan) in new[] { ("part", s.PartFan), ("right aux", s.AuxFan), ("left aux", s.LeftAuxFan),
                                            ("exhaust", s.ExhaustFan), ("chamber", s.CirculationFan) })
        {
            Assert.True(fan == 0, name);
        }
        var external = Assert.IsType<Spool>(s.ExternalSpool);
        Assert.False(external.IsLoaded);
        Assert.Null(s.ActiveSpoolId);
    }

    /// <summary>The fleet's H3C, printing from the AMS 3H's first slot: the slot comes from the extruder's <c>snow</c>
    /// (unit 32 &lt;&lt; 8 | slot), the loaded slots from <c>tray_exist_bits</c> bits 28 and up.</summary>
    [Fact]
    public void H3CPrintingFromTheAms3H()
    {
        var h3c = Reports().First(r => r.Status.Ams.FirstOrDefault()?.Kind == AmsKind.Ams3H).Status;
        var unit = h3c.Ams[0];
        Assert.Equal([0, 1, 2, 3], unit.Spools.Where(sp => sp.IsLoaded).Select(sp => sp.Slot));
        var active = Assert.IsType<Spool>(h3c.ActiveSpool);
        Assert.Equal(0, active.Slot);
        Assert.Equal("ABS", active.Material);
        Assert.NotNull(h3c.LeftAuxFan);
        Assert.NotNull(h3c.CirculationFan);
    }

    /// <summary>The fleet's H3C with an AMS-B instead (reconstructed from Bambu Studio's source, no report yet): type 7,
    /// its slots at <c>tray_exist_bits</c> 37 and up, numbered 1, 2…, and no humidity or temperature it didn't send.</summary>
    [Fact]
    public void H3CWithTheAmsB()
    {
        var h3c = Reports().First(r => r.Status.Ams.FirstOrDefault()?.Kind == AmsKind.AmsB).Status;
        var unit = h3c.Ams[0];
        Assert.Equal("AMS-B", unit.Title);
        Assert.Equal([0, 2, 3, 5], unit.Spools.Where(sp => sp.IsLoaded).Select(sp => sp.Slot));
        Assert.Equal(Enumerable.Range(1, 9).Select(i => i.ToString()), unit.Spools.Select(sp => sp.Label));
        Assert.Null(unit.Humidity);
        Assert.Null(unit.HumidityLevel);
        Assert.Null(unit.Temperature);
        var active = Assert.IsType<Spool>(h3c.ActiveSpool);
        Assert.Equal(2, active.Slot);
        Assert.Equal("PETG", active.Material);
    }

    /// <summary>Numbers that aren't: "nan" and "inf" from a printer must not reach the widget state or break the
    /// integer conversion.</summary>
    [Fact]
    public void NonFiniteNumbersAreDropped()
    {
        Assert.Null(ReportParser.Double("nan"));
        Assert.Null(ReportParser.Double("inf"));
        Assert.Null(ReportParser.Double(double.PositiveInfinity));
        Assert.Equal(38.4, ReportParser.Double("38.4"));
        Assert.Null(ReportParser.Int("nan"));
        Assert.Null(ReportParser.Int("1e300"));
        Assert.Equal(42, ReportParser.Int("42"));
        Assert.Equal(42, ReportParser.Int("42.9"));
    }
}
