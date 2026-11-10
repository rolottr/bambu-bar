using System.Collections.Concurrent;
using BambuBar.Core;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Integration.Tests;

/// <summary>
/// The mock fleet with SERIAL_CASE=mixed: its Bambu Studio settings keep a stale code under each uppercase serial and
/// the working one under the lowercase serial, and its printers announce themselves in both spellings, in turn. Each
/// must still be one printer that comes online with its working code. Ported from the Mac's
/// FleetIntegrationTests.swift, plus the Windows traps (ledger §4): a full handshake on every connection, and the
/// shared UDP port.
/// </summary>
public class FleetTests
{
    private static List<SlicerPrinter> SlicerPrinters()
    {
        Simulators.Require();
        var printers = SlicerImport.Read(Simulators.FleetSettings, "Bambu Studio");
        Assert.SkipWhen(printers.Count == 0, $"No fleet settings at {Simulators.FleetSettings}");
        return printers;
    }

    // MARK: Import

    [Fact]
    public void SettingsGiveOnePrinterPerSerialWithBothCodes()
    {
        var printers = SlicerPrinters();
        Assert.Equal(Simulators.FleetSize, printers.Count);
        Assert.Equal(Simulators.FleetSize, printers.Select(p => p.Serial).Distinct().Count());
        foreach (var printer in printers)
        {
            Assert.Equal(printer.Serial.ToUpperInvariant(), printer.Serial);
            var number = printer.Serial[^5..];
            Assert.Equal([$"OLD{number}", $"FLT{number}"], printer.AccessCodes);
            // The address sits under the other spelling, scrambled with the slicer's UUID by fleet.py.
            Assert.StartsWith("127.0.0.1:188", printer.Host);
        }
    }

    [Fact]
    public void ImportingTwiceAddsNoPrinter()
    {
        var printers = SlicerPrinters();
        Assert.Equal(printers, SlicerImport.Merge([.. printers, .. printers]));
    }

    // MARK: Connecting

    /// <summary>One MQTT connection: null once connected, else why not.</summary>
    private static async Task<MqttClient.Failure?> ConnectAsync(string host, int port, string serial, string code, bool insecure,
        Action<System.Security.Cryptography.X509Certificates.X509Certificate2>? onCertificate = null)
    {
        var done = new TaskCompletionSource<MqttClient.Failure?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MqttClient(new()
        {
            Host = host, Port = port, Password = code, Serial = serial, ConnectTimeout = TimeSpan.FromSeconds(8),
            Insecure = insecure, OnCertificate = onCertificate,
        });
        client.OnEvent = e =>
        {
            switch (e)
            {
                case MqttClient.Event.Connected: done.TrySetResult(null); break;
                case MqttClient.Event.Disconnected d: done.TrySetResult(d.Failure); break;
            }
        };
        client.Connect();
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        client.OnEvent = null;
        client.Disconnect();
        return result;
    }

    /// <summary>Tries the codes in turn the way the app does: the next one after the printer refuses one.</summary>
    private static async Task<string> ConnectAsync(SlicerPrinter printer)
    {
        var refused = new List<string>();
        var (host, port) = new PrinterConfig { Host = printer.Host ?? "", Serial = printer.Serial, Name = "" }.Endpoint;
        foreach (var code in printer.AccessCodes)
        {
            switch (await ConnectAsync(host, port, printer.Serial, code, insecure: true))
            {
                case null: return $"online with {code}, refused [{string.Join(",", refused)}]";
                case { IsAuthentication: true }: refused.Add(code); break;
                case var failure: return $"not online: {failure}";
            }
        }
        return $"not online: every code refused [{string.Join(",", refused)}]";
    }

    [Fact]
    public async Task EveryPrinterComesOnlineWithItsWorkingCode()
    {
        var printers = SlicerPrinters();
        var outcomes = (await Task.WhenAll(printers.Select(async p => (p.Serial, Outcome: await ConnectAsync(p)))))
            .ToDictionary(o => o.Serial, o => o.Outcome);
        foreach (var printer in printers.Where(p => p.Serial != Simulators.SwitchedOffPrinter))
        {
            var number = printer.Serial[^5..];
            Assert.Equal($"online with FLT{number}, refused [OLD{number}]", outcomes[printer.Serial]);
        }
        Assert.StartsWith("not online", outcomes[Simulators.SwitchedOffPrinter]);
    }

    /// <summary>The fleet's certificates are self-signed: without <c>insecure</c> they must be refused, before any code is
    /// sent, even right after a connection to the same printer (a resumed TLS session has no certificate to check).</summary>
    [Fact]
    public async Task FleetCertificatesAreRefusedUnlessInsecure()
    {
        var printer = SlicerPrinters().First(p => p.Serial != Simulators.SwitchedOffPrinter);
        var (host, port) = new PrinterConfig { Host = printer.Host!, Serial = printer.Serial, Name = "" }.Endpoint;
        var code = printer.AccessCodes[^1];
        Assert.Null(await ConnectAsync(host, port, printer.Serial, code, insecure: true));
        var failure = await ConnectAsync(host, port, printer.Serial, code, insecure: false);
        Assert.IsType<MqttClient.Failure.Certificate>(failure);
    }

    /// <summary>Ledger §4.1: SChannel resumes TLS sessions by default and a resumed handshake skips the certificate
    /// callback. Every connection must show the printer's certificate again.</summary>
    [Fact]
    public async Task EveryConnectionChecksTheCertificate()
    {
        var printer = SlicerPrinters().First(p => p.Serial != Simulators.SwitchedOffPrinter);
        var (host, port) = new PrinterConfig { Host = printer.Host!, Serial = printer.Serial, Name = "" }.Endpoint;
        var seen = new ConcurrentBag<string>();
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await ConnectAsync(host, port, printer.Serial, printer.AccessCodes[^1], insecure: true,
                onCertificate: c => seen.Add(BambuTrust.CommonName(c) ?? "")));
        }
        Assert.Equal(3, seen.Count);
        Assert.All(seen, name => Assert.Equal(printer.Serial, name, ignoreCase: true));
    }

    /// <summary>A printer added by its address alone: the handshake names it, no code is sent.</summary>
    [Fact]
    public async Task IdentityReadsTheSerialFromTheCertificate()
    {
        var printer = SlicerPrinters().First(p => p.Serial != Simulators.SwitchedOffPrinter);
        var (host, port) = new PrinterConfig { Host = printer.Host!, Serial = printer.Serial, Name = "" }.Endpoint;
        var (serial, failure) = await PrinterIdentity.SerialAsync(host, port, insecure: true, cancellation: TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(printer.Serial, serial);
        // Without `insecure` the fleet's own CA isn't Bambu's.
        var (_, refused) = await PrinterIdentity.SerialAsync(host, port, insecure: false, cancellation: TestContext.Current.CancellationToken);
        Assert.IsType<PrinterIdentity.Failure.NotBambu>(refused);
    }

    /// <summary>The whole session: connect, subscribe, pushall, the first full report, a status the parser made.</summary>
    [Fact]
    public async Task ConnectionComesOnlineAndReportsStatus()
    {
        var printer = SlicerPrinters().First(p => p.Serial != Simulators.SwitchedOffPrinter);
        using var connection = new PrinterConnection();
        var online = new TaskCompletionSource<PrinterStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phases = new ConcurrentQueue<PrinterConnection.Phase>();
        connection.Context.Send(_ =>
        {
            connection.OnPhase = phases.Enqueue;
            connection.OnStatus = (status, _) => online.TrySetResult(status);
            connection.Start(new PrinterConfig { Host = printer.Host!, Serial = printer.Serial, Name = "Fleet" },
                printer.AccessCodes[^1], insecure: true);
        }, null);
        var status = await online.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.True(status.State.IsActive() || status.State is JobState.Idle or JobState.Finished or JobState.Failed);
        Assert.Equal([typeof(PrinterConnection.Phase.Connecting), typeof(PrinterConnection.Phase.Online)],
            phases.Select(p => p.GetType()).Take(2));
        connection.Context.Send(_ => connection.Stop(), null);
    }

    // MARK: Discovery

    /// <summary>The announcements alternate between the two spellings of each serial: still one printer each, with the
    /// serial the settings file gives it.</summary>
    [Fact]
    public async Task AnnouncementsInEitherCaseAreOnePrinterEach()
    {
        var imported = SlicerPrinters().Select(p => p.Serial).ToHashSet();
        var found = new ConcurrentBag<DiscoveredPrinter>();
        using var discovery = new Discovery([Simulators.AnnouncementPort]);
        discovery.OnFound = found.Add;
        discovery.Start();
        // The fleet announces every printer every two seconds, the serial's case changing each time.
        await Task.Delay(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        discovery.Stop();
        var fleet = found.Where(p => p.Host.StartsWith("127.0.0.1:188", StringComparison.Ordinal)).ToList();
        const int online = Simulators.FleetSize - 1;
        Assert.True(fleet.Count >= 2 * online, $"each printer heard in both spellings ({fleet.Count})");
        var serials = fleet.Select(p => p.Serial).ToHashSet();
        Assert.Equal(online, serials.Count);
        Assert.All(serials, s => Assert.Equal(s.ToUpperInvariant(), s));
        Assert.Subset(imported, serials);
        Assert.All(fleet, p => Assert.NotEqual(p.Serial, p.Name));
    }

    [Fact]
    public void FleetWroteItsBambuStudioSettings()
    {
        Simulators.Require();
        Assert.True(File.Exists(Simulators.FleetSettings));
    }
}
