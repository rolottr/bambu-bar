using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>One printer per serial, whatever case each slicer section spells it in, with every access code kept to
/// try. Ported from the Mac's SlicerImportTests.swift, plus Windows folders and the std::mt19937 reference value.</summary>
public sealed class SlicerImportTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); } catch (IOException) { }
        }
    }

    private List<SlicerPrinter> Read(string json, string source = "Test")
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".conf");
        File.WriteAllText(path, json);
        _files.Add(path);
        return SlicerImport.Read(path, source);
    }

    private static SlicerPrinter P(string serial, string[] codes, string? host = null, string source = "Test") =>
        new() { Serial = serial, AccessCodes = codes, Host = host, Source = source };

    [Fact]
    public void SerialSpelledDifferentlyInEachSectionIsOnePrinter()
    {
        var rows = Read("""
        {"access_code": {"22EF00000000001": "STALE"},
         "user_access_code": {"22ef00000000001": "VALID"},
         "ip_address": {"22ef00000000001": "192.168.1.20"}}
        """);
        var row = Assert.Single(rows);
        Assert.Equal("22EF00000000001", row.Serial);
        Assert.Equal(["STALE", "VALID"], row.AccessCodes);
        Assert.Equal("192.168.1.20", row.Host);
    }

    [Fact]
    public void LowercaseKeysKeepTheirCodeAndAddress()
    {
        var rows = Read("""{"user_access_code": {"22ef00000000001": "VALID"}, "ip_address": {"22ef00000000001": "10.0.0.7"}}""");
        Assert.Equal([P("22EF00000000001", ["VALID"], "10.0.0.7")], rows);
    }

    [Fact]
    public void BothSpellingsInOneSectionKeepBothCodes()
    {
        var rows = Read("""{"user_access_code": {"22ef00000000001": "LOWER", "22EF00000000001": "UPPER"}}""");
        Assert.Equal(["UPPER", "LOWER"], Assert.Single(rows).AccessCodes);
    }

    [Fact]
    public void TheSameCodeTwiceIsTriedOnce()
    {
        var rows = Read("""{"access_code": {"22EF00000000001": "SAME"}, "user_access_code": {"22ef00000000001": "SAME"}}""");
        Assert.Equal(["SAME"], Assert.Single(rows).AccessCodes);
    }

    [Fact]
    public void AddressOnlyUnderTheOtherSpellingIsFound()
    {
        const string key = "daf7e1d0-0c1f-4bbc-b88f-309df40dab23";
        var rows = Read($$$"""
        {"app": {"slicer_uuid": "{{{key}}}"},
         "access_code": {"22EF00000000001": "STALE"},
         "user_access_code": {"22ef00000000001": "VALID"},
         "user_access_dev_ip": {"22ef00000000001": "{{{SlicerImport.EncodeIp("127.0.0.1:18801", key)}}}"}}
        """);
        var row = Assert.Single(rows);
        Assert.Equal("127.0.0.1:18801", row.Host);
        Assert.Equal(["STALE", "VALID"], row.AccessCodes);
    }

    [Fact]
    public void PrintersWithoutACodeAreLeftOut() =>
        Assert.Empty(Read("""{"ip_address": {"22EF00000000001": "10.0.0.7"}, "user_access_code": {"22EF00000000002": " "}}"""));

    [Fact]
    public void ChecksumLineAfterTheJsonIsIgnored() =>
        Assert.Single(Read("{\"user_access_code\": {\"22EF00000000001\": \"VALID\"}}\r\n# MD5 checksum 0123456789abcdef\r\n"));

    [Fact]
    public void TheSamePrinterInTwoSlicersKeepsTheCodesOfBoth()
    {
        var studio = P("22EF00000000001", ["STALE"], source: "Bambu Studio");
        var orca = P("22ef00000000001", ["VALID", "STALE"], "10.0.0.7", "OrcaSlicer");
        var other = P("01P00A000000002", ["X"], "10.0.0.8", "OrcaSlicer");
        Assert.Equal([P("22EF00000000001", ["STALE", "VALID"], "10.0.0.7", "Bambu Studio"), other],
            SlicerImport.Merge([studio, orca, other]));
    }

    [Fact]
    public void MergeUppercasesALoneLowercaseSerial() =>
        Assert.Equal(["22EF00000000001"], SlicerImport.Merge([P("22ef00000000001", ["A"], source: "S")]).Select(p => p.Serial));

    /// <summary>A copy of a Windows user folder or of a Mac's home, dropped on the window, is searched for the slicers.</summary>
    [Theory]
    [InlineData("AppData/Roaming/BambuStudio")]
    [InlineData("Library/Application Support/OrcaSlicer")]
    public void PickedUserFolderFindsTheSettings(string where)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _files.Add(root);
        var folder = Path.Combine([root, .. where.Split('/')]);
        Directory.CreateDirectory(folder);
        var app = Path.GetFileName(folder);
        File.WriteAllText(Path.Combine(folder, $"{app}.conf"), """{"user_access_code": {"22EF00000000001": "VALID"}}""");
        Assert.Equal("22EF00000000001", Assert.Single(SlicerImport.ReadPicked(root)).Serial);
        // A whole disk: Users\<name>\…
        var disk = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _files.Add(disk);
        Directory.CreateDirectory(Path.Combine(disk, "Users"));
        Directory.Move(root, Path.Combine(disk, "Users", "someone"));
        Assert.Single(SlicerImport.ReadPicked(disk));
    }

    /// <summary>The C++ standard's own check of std::mt19937: default seed 5489, the 10000th value is 4123659995.</summary>
    [Fact]
    public void MersenneTwisterMatchesStdMt19937()
    {
        var rng = new MersenneTwister(5489);
        Assert.Equal(3_499_211_612u, rng.Next());
        for (var i = 2; i < 10_000; i++) rng.Next();
        Assert.Equal(4_123_659_995u, rng.Next());
    }

    [Fact]
    public void PlainAddressesAreKept()
    {
        Assert.Equal("192.168.1.20", SlicerImport.DecodeIp("192.168.1.20", "anything"));
        Assert.Equal("192.168.1.20", SlicerImport.DecodeIp("192.168.1.20", ""));
        Assert.Null(SlicerImport.DecodeIp("zz", "key"));
    }
}
