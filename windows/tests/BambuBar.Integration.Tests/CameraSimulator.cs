using System.Text.Json;

namespace BambuBar.Integration.Tests;

/// <summary>The mock camera (shared/scripts/camera/camera.py) that compose.yaml publishes on loopback: RTSPS on 322,
/// the JPEG stream on 6000, its scenario chosen over HTTP on 18080. The camera half of the Mac's Simulators.swift.</summary>
internal static class CameraSimulator
{
    public const string Host = "127.0.0.1";
    public const string Code = "12345678";
    public const string Serial = "01P00C000000001";
    private static readonly Uri Control = new("http://127.0.0.1:18080/");
    private static readonly HttpClient Http = new() { BaseAddress = Control, Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>What the camera does for the next connections (the list is in shared/scripts/camera/camera.py).</summary>
    public static async Task SetScenarioAsync(string name, CancellationToken token)
    {
        using var response = await Http.PutAsync($"scenario/{name}", null, token);
        response.EnsureSuccessStatusCode();
        await ClearEventsAsync(token);
    }

    /// <summary>What the camera received since the scenario was set: requests, logins, teardowns.</summary>
    public static async Task<List<JsonElement>> EventsAsync(CancellationToken token)
    {
        var text = await Http.GetStringAsync("events", token);
        var events = new List<JsonElement>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                events.Add(document.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return events;
    }

    public static async Task ClearEventsAsync(CancellationToken token)
    {
        using var response = await Http.DeleteAsync("events", token);
    }
}
