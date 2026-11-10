using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Protocol;

namespace BambuBar.Integration.Tests;

/// <summary>The store against the mock fleet, as <c>BambuBar.exe --emulator &lt;conf&gt; --add-all</c> runs it: every
/// printer is imported from the settings file, added with its stale code first, falls back to the working one, and
/// comes online. Nothing is saved.</summary>
[Collection(nameof(Simulators))]
public class StoreFleetTests
{
    [Fact]
    public async Task AddAllBringsTheWholeFleetOnline()
    {
        Simulators.Require();
        using var context = new SerialContext("StoreFleetTests");
        var store = context.Invoke(() => new PrinterStore(new PrinterStore.Services
        {
            Settings = new AppSettings(null),
            History = new HistoryStore(null),
            Secrets = new MemorySecretStore(),
            Arguments = ["--emulator", Simulators.FleetSettings, "--add-all"],
            DiscoveryPorts = [Simulators.AnnouncementPort],
        }));
        try
        {
            context.Send(_ => store.Start(), null);
            var online = 0;
            for (var i = 0; i < 60; i++)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
                online = context.Invoke(() => store.Sessions.Count(s => s.Phase is PrinterConnection.Phase.Online && s.Status is not null));
                if (online == Simulators.FleetSize - 1) break;
            }
            Assert.Equal(Simulators.FleetSize - 1, online);
            Assert.Equal(Simulators.FleetSize, context.Invoke(() => store.Sessions.Count));
            Assert.True(context.Invoke(() => store.IsDemo));
            // Named from their announcements, not placeholders.
            Assert.All(context.Invoke(() => store.Sessions.Where(s => s.Id != Simulators.SwitchedOffPrinter).ToList()),
                s => Assert.False(s.Config.HasGeneratedName, s.Config.Name));
        }
        finally
        {
            context.Send(_ => store.Dispose(), null);
        }
    }
}
