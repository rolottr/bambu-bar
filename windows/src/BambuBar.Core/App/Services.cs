using System.Collections.Concurrent;

namespace BambuBar.Core.App;

/// <summary>Where access codes are kept: Windows Credential Manager in the app ("Bambu Bar/&lt;serial&gt;").</summary>
public interface ISecretStore
{
    string? AccessCode(string serial);
    void SetAccessCode(string code, string serial);
    void RemoveAccessCode(string serial);
}

/// <summary>Codes held for this run only: demo, emulator and developer sessions, and tests.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.Ordinal);
    public string? AccessCode(string serial) => _codes.GetValueOrDefault(serial);
    public void SetAccessCode(string code, string serial) => _codes[serial] = code;
    public void RemoveAccessCode(string serial) => _codes.TryRemove(serial, out _);
}

/// <summary>System notifications (toasts on Windows). <paramref name="id"/> replaces an earlier one with the same id.</summary>
public interface INotifier
{
    void Post(string title, string body, string id);
    void RequestAuthorization();
}

public sealed class NullNotifier : INotifier
{
    public void Post(string title, string body, string id) { }
    public void RequestAuthorization() { }
}

/// <summary>Takes the fleet's snapshot for the widgets (the Rainmeter state writer in the app).</summary>
public interface IWidgetSink
{
    /// <summary>Called when the snapshot is worth writing; <paramref name="reload"/> when something a person would
    /// notice changed (the widgets should redraw now, not at their next timed read).</summary>
    void Write(FleetSnapshot fleet, bool reload);
}

/// <summary>Questions for the person (a stop confirmation) belong to the UI; Core asks through this.</summary>
public interface IConfirmer
{
    /// <summary>"Stop “Benchy”?" with the given detail; true to go ahead.</summary>
    bool ConfirmStop(string title, string detail);
}
