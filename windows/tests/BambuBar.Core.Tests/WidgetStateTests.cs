using System.Text.Json;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Widgets;

namespace BambuBar.Core.Tests;

/// <summary>The widgets' state file (contract v1): the Mac's WidgetState rules, per state.</summary>
public sealed class WidgetStateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bambubar-widgets-" + Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 13, 30, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (DirectoryNotFoundException) { }
    }

    private static PrinterSnapshot Snap(PrinterStatus? status, PrinterSnapshot.LinkState link = PrinterSnapshot.LinkState.Online,
        JobRecord? last = null) => new()
    {
        Serial = "22E00A000000000", PrinterName = "P2S Workshop", Model = "P2S", Link = link, Status = status, LastJob = last,
        SavedAt = Now,
    };

    private static PrinterStatus Sample(Action<PrinterStatus>? change = null)
    {
        var s = DemoDriver.Sample();
        s.UpdatedAt = Now;
        change?.Invoke(s);
        return s;
    }

    [Fact]
    public void Printing()
    {
        var w = WidgetStateWriter.Build(Snap(Sample()), Now);
        Assert.Equal("printing", w.State);
        Assert.Equal("Printing", w.Caption);
        Assert.Equal("Rear Dry Pod", w.Headline);
        Assert.Equal("1h 02m left", w.StateLine);
        Assert.StartsWith("Done ", w.Detail);
        Assert.EndsWith(" · 170/305", w.Detail);
        Assert.Equal(64, w.Progress);
        Assert.Equal(Now.AddMinutes(62).ToUnixTimeSeconds(), w.EndsAt);
        // The active spool is Gold (F4A925): dark enough for the bar.
        Assert.Equal("#F4A925", w.Tint);
        Assert.Equal(["Nozzle", "Bed", "Chamber"], w.Temps.Select(t => t.Label));
        Assert.Equal("220°", w.Temps[0].Value);
        Assert.Equal("AMS 2 Pro A", w.Ams?.Title);
        Assert.Equal("16%", w.Ams?.Humidity);
        Assert.Equal(4, w.Spools.Count);
        Assert.True(w.Spools[3].Active);
        Assert.Equal("PLA Silk+ · A4", w.ActiveSpoolLine);
        Assert.Equal("{0}h {1}m", w.Strings.HoursMinutes);
        Assert.Equal("{0} left", w.Strings.Left);
    }

    [Fact]
    public void PausedLeadsWithTheReason()
    {
        var w = WidgetStateWriter.Build(Snap(Sample(s => { s.State = JobState.Paused; s.StageId = 6; })), Now);
        Assert.Equal("Filament ran out", w.Reason);
        Assert.Equal("Paused: filament ran out", w.StateLine);
        Assert.Equal("Layer 170/305 · 64%", w.Detail);
        Assert.Equal(WidgetPalette.Paused, w.Tint);
    }

    [Fact]
    public void CanceledIsGreyNotRed()
    {
        var w = WidgetStateWriter.Build(Snap(Sample(s => { s.State = JobState.Failed; s.Canceled = true; })), Now);
        Assert.Equal("Canceled", w.Caption);
        Assert.Equal(WidgetPalette.Idle, w.StateColor);
        Assert.Equal(WidgetPalette.Secondary, w.Tint);
    }

    [Fact]
    public void IdleShowsTheLastJob()
    {
        var last = new JobRecord { Name = "Benchy", StartedAt = Now.AddHours(-2), EndedAt = Now.AddMinutes(-65), Outcome = JobOutcome.Finished };
        var w = WidgetStateWriter.Build(Snap(Sample(s => { s.State = JobState.Idle; s.JobName = ""; }), last: last), Now);
        Assert.Equal("Ready", w.Headline);
        Assert.Equal("Last: Benchy", w.StateLine);
        Assert.Equal("1h 05m ago", w.LastJob?.Ago);
        Assert.Equal("took 55m", w.LastJob?.Took);
    }

    [Theory]
    [InlineData(PrinterSnapshot.LinkState.Offline, "Offline", "Offline", "Waiting for the printer")]
    [InlineData(PrinterSnapshot.LinkState.Unauthorized, "Access code rejected", "Access code rejected", "Enter the access code again in the app")]
    [InlineData(PrinterSnapshot.LinkState.Unconfigured, "Set up Bambu Bar", "Set up Bambu Bar", "Open the app to connect your printer")]
    [InlineData(PrinterSnapshot.LinkState.Connecting, "Connecting…", "Connecting…", "Waiting for the printer")]
    public void NotLive(PrinterSnapshot.LinkState link, string caption, string headline, string line)
    {
        // The last status is kept but not shown while the printer isn't answering.
        var w = WidgetStateWriter.Build(Snap(link == PrinterSnapshot.LinkState.Connecting ? null : Sample(), link), Now);
        Assert.False(w.Live);
        Assert.Equal(caption, w.Caption);
        Assert.Equal(headline, w.Headline);
        Assert.Equal(line, w.StateLine);
        Assert.Empty(w.Temps);
    }

    [Fact]
    public void WriterWritesEveryPrinterTheDefaultAndArt()
    {
        var writer = new WidgetStateWriter(_folder);
        var reloads = 0;
        writer.Reload = () => reloads++;
        var fleet = new FleetSnapshot { Printers = [Snap(Sample())], MenuBarSerial = "22E00A000000000", SavedAt = Now };
        writer.Write(fleet, reload: true);
        var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "22E00A000000000.json"))).RootElement;
        Assert.Equal(1, state.GetProperty("v").GetInt32());
        Assert.Equal("Rear Dry Pod", state.GetProperty("headline").GetString());
        var art = state.GetProperty("art").GetString()!;
        Assert.True(File.Exists(Path.Combine(_folder, art)));
        // Light on: the unlit variant is there for the skin's crossfade, and different.
        var unlit = state.GetProperty("artUnlit").GetString()!;
        Assert.NotEqual(art, unlit);
        Assert.True(File.Exists(Path.Combine(_folder, unlit)));
        Assert.Equal("22E00A000000000", JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "default.json"))).RootElement.GetProperty("serial").GetString());
        Assert.Equal(1, reloads);
        // A forgotten printer's file goes; the default falls back to "set up".
        writer.Write(new FleetSnapshot { Printers = [], SavedAt = Now }, reload: false);
        Assert.False(File.Exists(Path.Combine(_folder, "22E00A000000000.json")));
        Assert.Equal("unconfigured", JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "default.json"))).RootElement.GetProperty("link").GetString());
        Assert.Equal(1, reloads);
    }
}

/// <summary>Writes a state file per scene for the skins' development (BAMBUBAR_STATE_OUT=folder).</summary>
public class WidgetStateSamples
{
    [Fact]
    public void WritesSamplesWhenAsked()
    {
        var folder = Environment.GetEnvironmentVariable("BAMBUBAR_STATE_OUT");
        Assert.SkipWhen(folder is null, "Set BAMBUBAR_STATE_OUT to write sample state files");
        Directory.CreateDirectory(folder!);
        var now = DateTimeOffset.Now;
        var last = new JobRecord { Name = "Benchy", StartedAt = now.AddHours(-3), EndedAt = now.AddHours(-2), Outcome = JobOutcome.Finished };
        void Save(string name, PrinterSnapshot snapshot) =>
            File.WriteAllText(Path.Combine(folder!, name + ".json"),
                System.Text.Json.JsonSerializer.Serialize(WidgetStateWriter.Build(snapshot, now, folder, motion: true), WidgetState.Json));
        PrinterSnapshot Snap(string scenario, PrinterSnapshot.LinkState link = PrinterSnapshot.LinkState.Online, string model = "P2S", int variant = 0)
        {
            PrinterStatus? status = null;
            if (Enum.TryParse<DemoDriver.Scenario>(scenario, true, out var s))
            {
                var driver = new DemoDriver(s, variant) { OnStatus = (st, _) => status = st };
                driver.Start(animate: false);
            }
            return new PrinterSnapshot { Serial = "22E00A000000000", PrinterName = "P2S Workshop", Model = model, Link = link,
                Status = status, LastJob = last, SavedAt = now };
        }
        foreach (var scenario in new[] { "printing", "preparing", "paused", "finished", "failed", "idle" }) Save(scenario, Snap(scenario));
        Save("a1-multicolour", Snap("printing", model: "A1", variant: 2));
        Save("h2d-two-nozzles", Snap("printing", model: "H2D", variant: 3));
        Save("offline", Snap("printing", PrinterSnapshot.LinkState.Offline));
        Save("connecting", Snap("none", PrinterSnapshot.LinkState.Connecting));
        Save("unauthorized", Snap("none", PrinterSnapshot.LinkState.Unauthorized));
        Save("unconfigured", PrinterSnapshot.Unconfigured(now));
    }
}
