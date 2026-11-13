using System.ComponentModel;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>Saving refused by Windows (an antivirus guarding %APPDATA%, Credential Manager saying no) is reported, never
/// thrown: "Add All" and "Connect" used to end the app without a word when the first save failed.</summary>
public sealed class SaveFailureTests : IDisposable
{
    /// <summary>Credential Manager refusing every write, as with error 1312 under a policy that blocks saved credentials.</summary>
    private sealed class RefusingSecrets : ISecretStore
    {
        public string? AccessCode(string serial) => null;
        public void SetAccessCode(string code, string serial) => throw new Win32Exception(1312);
        public void RemoveAccessCode(string serial) => throw new Win32Exception(1312);
    }

    private readonly SerialContext _context = new("SaveFailureTests");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bambubar-tests-" + Guid.NewGuid());
    private PrinterStore? _store;

    public void Dispose()
    {
        if (_store is { } store) _context.Send(_ => store.Dispose(), null);
        _context.Dispose();
        try { Directory.Delete(_folder, true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>A settings path whose folder is a file: every save fails with an IOException.</summary>
    private string UnwritableSettingsPath()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "Bambu Bar");
        File.WriteAllText(blocker, "");
        return Path.Combine(blocker, "settings.json");
    }

    private (PrinterStore Store, List<Exception> Failures) Store(ISecretStore secrets, string settingsPath)
    {
        var failures = new List<Exception>();
        var store = _context.Invoke(() =>
        {
            _store = new PrinterStore(new PrinterStore.Services
            {
                Settings = new AppSettings(settingsPath),
                History = new HistoryStore(Path.Combine(_folder, "history.json")),
                Secrets = secrets,
                DiscoveryPorts = [],
            });
            _store.SaveFailed += failures.Add;
            return _store;
        });
        return (store, failures);
    }

    private static SlicerPrinter Slicer(string serial, string code) =>
        // Port 1 on loopback: refused at once, so nothing connects.
        new() { Serial = serial, AccessCodes = [code], Host = "127.0.0.1:1", Source = "Bambu Studio" };

    [Fact]
    public void SettingsThatCantBeWrittenAreReportedAndKeptInMemory()
    {
        var settings = new AppSettings(UnwritableSettingsPath());
        var failures = new List<Exception>();
        settings.SaveFailed += failures.Add;

        settings.Set(AppSettings.Keys.ConfirmStop, false);

        Assert.IsAssignableFrom<IOException>(Assert.Single(failures));
        Assert.False(settings.Get(AppSettings.Keys.ConfirmStop, true));
    }

    [Fact]
    public void AddAllWithTwoPrintersSurvivesRefusedSaves()
    {
        var (store, failures) = Store(new RefusingSecrets(), UnwritableSettingsPath());

        _context.Send(_ => store.AddAll([Slicer("01P00A000000001", "11111111"), Slicer("039F00000000002", "22222222")]), null);

        Assert.Equal(2, _context.Invoke(() => store.Sessions.Count));
        Assert.Contains(failures, f => f is Win32Exception);
        Assert.Contains(failures, f => f is IOException);
        Assert.NotNull(_context.Invoke(() => store.Current));
    }

    [Fact]
    public void ConnectSurvivesARefusedAccessCode()
    {
        var (store, failures) = Store(new RefusingSecrets(), Path.Combine(_folder, "settings.json"));
        var config = new PrinterConfig { Host = "127.0.0.1:1", Serial = "22E00A000000001", Name = "Workshop", ModelCode = "N7" };

        _context.Send(_ => store.Connect(config, "12345678"), null);

        Assert.Equal("22E00A000000001", _context.Invoke(() => store.Current?.Id));
        Assert.IsType<Win32Exception>(Assert.Single(failures));
        // The printer itself was saved; only its code wasn't.
        Assert.Contains("22E00A000000001", File.ReadAllText(Path.Combine(_folder, "settings.json")));
    }

    [Fact]
    public void ForgetSurvivesARefusedDelete()
    {
        var (store, failures) = Store(new RefusingSecrets(), Path.Combine(_folder, "settings.json"));
        var config = new PrinterConfig { Host = "127.0.0.1:1", Serial = "22E00A000000001", Name = "Workshop", ModelCode = "N7" };
        _context.Send(_ => store.Connect(config, "12345678"), null);

        _context.Send(_ => store.Forget("22E00A000000001"), null);

        Assert.Equal(0, _context.Invoke(() => store.Sessions.Count));
        Assert.Equal(2, failures.Count);
    }
}
