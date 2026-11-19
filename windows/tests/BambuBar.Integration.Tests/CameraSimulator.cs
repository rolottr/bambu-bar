using System.Text.Json;

using BambuBar.Core.Camera;

namespace BambuBar.Integration.Tests;

/// <summary>The mock camera (shared/scripts/camera/camera.py) that compose.yaml publishes on loopback: RTSPS on 322,
/// the JPEG stream on 6000, its scenario chosen over HTTP on 18080. The camera half of the Mac's Simulators.swift.</summary>
internal static class CameraSimulator
{
    public const string Host = "127.0.0.1";
    public const string Code = "12345678";
    public const string Serial = "01P00C000000001";
    /// <summary>The plain RTSP an external camera serves (compose.yaml publishes 554 as 1554).</summary>
    public static int PlainPort =>
        int.TryParse(Environment.GetEnvironmentVariable("BAMBUBAR_CAMERA_PLAIN_PORT"), out var p) ? p : 1554;
    /// <summary>Where the camera serves RTSPS. 322 is the printers' port, which the clients hardcode; an entered
    /// URL may name any port, so a run without the privilege to bind 322 can point the external cases
    /// elsewhere.</summary>
    public static int TlsPort =>
        int.TryParse(Environment.GetEnvironmentVariable("BAMBUBAR_CAMERA_TLS_PORT"), out var p) ? p : 322;
    public const string User = "admin";
    public const string Password = "cam-secret";

    /// <summary>A source for the mock camera's plain RTSP port, reached the way an entered camera is.</summary>
    public static CameraSource External(string path = "/stream1", string username = User, string password = Password,
        string host = Host, int? port = null)
    {
        Assert.True(RtspUrl.TryParse($"rtsp://{host}:{port ?? PlainPort}{path}", out var url, out var problem),
            $"{problem}");
        return new CameraSource.External(new ExternalCamera { Url = url, Username = username, Password = password });
    }
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
