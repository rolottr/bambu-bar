using System.Text.Json;

namespace BambuBar.Integration.Tests;

/// <summary>The mock fleet is up. P1.12 adds discovery, MQTT and slicer import against it.</summary>
public class FleetTests
{
    [Fact]
    public void FleetWroteItsBambuStudioSettings()
    {
        Simulators.Require();
        var text = File.ReadAllText(Simulators.FleetSettings);
        // Bambu Studio ends the file with an MD5 line after the JSON (ledger §4.12).
        using var json = JsonDocument.Parse(text[..(text.LastIndexOf('}') + 1)]);
        var codes = json.RootElement.GetProperty("user_access_code");
        Assert.Equal(Simulators.FleetSize, codes.EnumerateObject().Count());
    }
}
