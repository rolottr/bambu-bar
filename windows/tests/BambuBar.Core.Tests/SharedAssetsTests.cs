using System.Text.Json;

namespace BambuBar.Core.Tests;

/// <summary>The files Core reads from shared/ are where it expects them. P1 adds the tests that read their content.</summary>
public class SharedAssetsTests
{
    /// <summary>One report per model (15) plus the H3C with the AMS-B, written by fleet.py --dump.</summary>
    [Fact]
    public void ModelReportsHaveModelSerialAndPrint()
    {
        var files = Directory.GetFiles(Repo.Shared("ModelReports"), "*.json");
        Assert.Equal(16, files.Length);
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = json.RootElement;
            Assert.Equal(JsonValueKind.String, root.GetProperty("model").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("serial").ValueKind);
            Assert.Equal(JsonValueKind.Object, root.GetProperty("print").ValueKind);
        }
    }

    [Fact]
    public void HmsTextsCertificatesAndFilamentsArePresent()
    {
        Assert.Contains(Repo.Shared("HMS", "hms_en.json.deflate"), Directory.GetFiles(Repo.Shared("HMS")));
        Assert.NotEmpty(Directory.GetFiles(Repo.Shared("Certificates"), "*.pem"));
        Assert.True(File.Exists(Repo.Shared("filaments.json")));
        Assert.True(File.Exists(Repo.Shared("Localizable.xcstrings")));
    }
}
