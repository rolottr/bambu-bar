using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>One printer is one serial, in uppercase, from wherever it's learned: its network announcement, the saved
/// printers, a slicer. And a name Bambu Bar made up gives way to the one the printer announces. Ported from the Mac's
/// PrinterIdentityTests.swift.</summary>
public class PrinterIdentityTests
{
    private static string Announcement(string serial, string? name = "Workshop P2S")
    {
        var lines = new List<string> { "NOTIFY * HTTP/1.1", "HOST: 239.255.255.250:1990", "NT: urn:bambulab-com:device:3dprinter:1",
            $"USN: {serial}", "Location: 192.168.1.20", "DevModel.bambu.com: N7" };
        if (name is not null) lines.Add($"DevName.bambu.com: {name}");
        return string.Join("\r\n", lines) + "\r\n\r\n";
    }

    [Fact]
    public void LowercaseAnnouncementIsTheSamePrinter()
    {
        var lower = Assert.IsType<DiscoveredPrinter>(Discovery.Parse(Announcement("22ef00000000001"), "192.168.1.20"));
        var upper = Assert.IsType<DiscoveredPrinter>(Discovery.Parse(Announcement("22EF00000000001"), "192.168.1.20"));
        Assert.Equal("22EF00000000001", lower.Serial);
        Assert.Equal(upper.Serial, lower.Serial);
        Assert.Equal("Workshop P2S", lower.Name);
        Assert.Equal("192.168.1.20", lower.Host);
        Assert.Equal("P2S", lower.ModelName);
    }

    [Fact]
    public void UnnamedAnnouncementIsNamedAfterTheUppercaseSerial() =>
        Assert.Equal("22EF00000000001", Discovery.Parse(Announcement("22ef00000000001", null), "10.0.0.2")?.Name);

    [Fact]
    public void OtherDevicesAreIgnored()
    {
        Assert.Null(Discovery.Parse("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\nUSN: uuid:abc\r\n\r\n", "10.0.0.3"));
        Assert.Null(Discovery.Parse("NOTIFY * HTTP/1.1\r\nNT: urn:bambulab-com:device:3dprinter:1\r\nUSN: \r\n\r\n", "10.0.0.3"));
    }

    [Fact]
    public void AnswerToASearchWithoutLocationUsesTheSender()
    {
        var answer = "HTTP/1.1 200 OK\r\nST: urn:bambulab-com:device:3dprinter:1\r\nUSN: 01P00A000000002\r\nDevConnect.bambu.com: lan\r\n\r\n";
        var printer = Assert.IsType<DiscoveredPrinter>(Discovery.Parse(answer, "10.0.0.4"));
        Assert.Equal("10.0.0.4", printer.Host);
        Assert.Equal("lan", printer.ConnectMode);
    }

    private static PrinterConfig Config(string serial, string name = "Printer", string host = "10.0.0.1") =>
        new() { Host = host, Serial = serial, Name = name, ModelCode = "N7" };

    [Fact]
    public void PrinterSavedTwiceIsLoadedOnce()
    {
        var loaded = PrinterConfig.Deduplicated([Config("22EF00000000001", "Workshop", "10.0.0.1"), Config("01P00A000000002"),
            Config("22EF00000000001", "Workshop copy", "10.0.0.9"), Config("22ef00000000001", "lower")]);
        Assert.Equal(["22EF00000000001", "01P00A000000002"], loaded.Select(c => c.Serial));
        Assert.Equal("Workshop", loaded[0].Name);
        Assert.Equal("10.0.0.1", loaded[0].Host);
    }

    [Fact]
    public void LowercaseSavedSerialIsUppercased() =>
        Assert.Equal(["22EF00000000001"], PrinterConfig.Deduplicated([Config("22ef00000000001")]).Select(c => c.Serial));

    [Fact]
    public void EndpointTakesTheMockFleetsPort()
    {
        Assert.Equal(("127.0.0.1", 18801), Config("X", host: "127.0.0.1:18801").Endpoint);
        Assert.Equal(("192.168.1.20", 8883), Config("X", host: "192.168.1.20").Endpoint);
    }

    /// <summary>Setups before 1.2 saved "N7" (P2S) for a printer typed in by hand; the serial tells the truth.</summary>
    [Fact]
    public void SerialPrefixWinsOverAStaleModelCode()
    {
        Assert.Equal("A1", PrinterModel.Find("N7", "039F00000000008")?.Name);
        Assert.Equal("A1", new PrinterConfig { Host = "", Serial = "039F00000000008", Name = "", ModelCode = "N7" }.ModelName);
        Assert.Equal("P1S", PrinterModel.Find("N7", "01PF00000000002")?.Name);
    }

    [Fact]
    public void ModelCodeStillDecidesForUnknownSerials()
    {
        Assert.Equal("A1", PrinterModel.Find("N2S", "ZZZ00EMULATOR0")?.Name);
        Assert.Equal("X1E", PrinterModel.Find("C13")?.Name);
        Assert.Equal("X1E", PrinterModel.Find("BL-P003")?.Name);
        Assert.Null(PrinterModel.Find("", "ZZZ00EMULATOR0"));
    }

    [Fact]
    public void EveryModelIsFoundByItsOwnSerialAndCode()
    {
        foreach (var model in PrinterModel.All)
        {
            Assert.Equal(model, PrinterModel.Find(model.Code, model.SerialPrefix + "000000000001"));
            Assert.Equal(model, PrinterModel.Find("", model.SerialPrefix + "000000000001"));
        }
    }

    [Fact]
    public void H3CHasItsOwnShape()
    {
        Assert.Equal(PrinterShape.H3C, PrinterShapes.FromModel("H3C"));
        Assert.Equal(PrinterShape.H3C, PrinterShapes.FromModel("N10"));
        Assert.Equal(PrinterShape.H3C, PrinterShapes.FromModel("N10-V2"));
        Assert.Equal(PrinterShape.Wide, PrinterShapes.FromModel("H2C"));
        Assert.Equal("h3c", PrinterShape.H3C.AssetName());
        Assert.True(PrinterShape.H3C.IsWide());
    }

    [Fact]
    public void PlaceholderName()
    {
        Assert.Equal("P2S 495", PrinterConfig.PlaceholderName("22EF00000000495", "N7"));
        Assert.Equal("P2S 495", PrinterConfig.PlaceholderName("22EF00000000495", ""));
    }

    [Fact]
    public void MadeUpNamesGiveWayToTheAnnouncedOne()
    {
        Assert.True(Config("22EF00000000495", "22EF00000000495").HasGeneratedName);
        Assert.True(Config("22EF00000000495", "22ef00000000495").HasGeneratedName);
        Assert.True(Config("22EF00000000495", "P2S 495").HasGeneratedName);
        Assert.True(new PrinterConfig { Host = "", Serial = "22EF00000000495", Name = "P2S 495" }.HasGeneratedName);
    }

    [Fact]
    public void NamesChosenByHandAreKept()
    {
        Assert.False(Config("22EF00000000495", "Workshop").HasGeneratedName);
        Assert.False(Config("22EF00000000495", "P2S 496").HasGeneratedName);
        Assert.False(Config("22EF00000000495", "P2S").HasGeneratedName);
    }

    /// <summary>Reports that aren't UTF-8 are read as Latin-1 rather than dropped.</summary>
    [Fact]
    public void Latin1PayloadIsDecoded()
    {
        var payload = System.Text.Encoding.Latin1.GetBytes("{\"print\":{\"subtask_name\":\"Café\"}}");
        var json = Assert.IsType<Dictionary<string, object?>>(PrinterConnection.Decode(payload));
        Assert.Equal("Café", ((Dictionary<string, object?>)json["print"]!)["subtask_name"]);
    }

    [Fact]
    public void BundledRootsLoad() => Assert.Equal(8, BambuTrust.BundledCount);
}
