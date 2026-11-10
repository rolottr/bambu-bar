using System.Net;
using System.Net.Sockets;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

public class DiscoveryTests
{
    /// <summary>Ledger §4.4: Bambu Studio listens on UDP 2021 too. With the port already taken (reuse on, as slicers
    /// open it), Bambu Bar must still bind it rather than give up on discovery.</summary>
    [Fact]
    public void SharesAPortAnotherProgramListensOn()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Dispose();

        using var slicer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        slicer.ExclusiveAddressUse = false;
        slicer.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        slicer.Bind(new IPEndPoint(IPAddress.Any, port));

        using var discovery = new Discovery([port]);
        discovery.Start();
        Assert.Contains(port, discovery.ListeningPorts);
        discovery.Stop();
    }

    [Fact]
    public void SearchIsBambuStudiosMSearch()
    {
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", Discovery.SearchMessage);
        Assert.Contains("ST: urn:bambulab-com:device:3dprinter:1\r\n", Discovery.SearchMessage);
        Assert.EndsWith("\r\n\r\n", Discovery.SearchMessage);
    }
}
