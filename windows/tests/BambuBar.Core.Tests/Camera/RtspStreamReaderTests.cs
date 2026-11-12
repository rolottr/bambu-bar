using System.Text;
using BambuBar.Core.Camera;

namespace BambuBar.Core.Tests.Camera;

/// <summary>What arrives on the RTSP connection comes from the network: no length in it may crash the app (review
/// finding P1: <c>Content-Length: -1</c> trapped on an inverted range). Ported from the Mac's
/// RTSPStreamReaderTests.swift.</summary>
public class RtspStreamReaderTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Interleaved(byte channel, params byte[] packet) =>
        [0x24, channel, (byte)(packet.Length >> 8), (byte)(packet.Length & 0xFF), .. packet];

    private static RtspStreamFailure? Failure(string response)
    {
        var reader = new RtspStreamReader();
        try
        {
            reader.Append(Bytes(response));
            return null;
        }
        catch (RtspStreamException e)
        {
            return e.Failure;
        }
    }

    [Fact]
    public void ResponseWithBody()
    {
        var reader = new RtspStreamReader();
        var items = reader.Append(Bytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 5\r\n\r\nv=0\r\n"));
        var message = Assert.IsType<RtspStreamReader.Item.Message>(Assert.Single(items));
        Assert.Equal(200, message.Head.Status);
        Assert.Equal(Bytes("v=0\r\n"), message.Body);
    }

    [Fact]
    public void ResponseWithoutContentLength()
    {
        var reader = new RtspStreamReader();
        var items = reader.Append(Bytes("RTSP/1.0 200 OK\r\nCSeq: 3\r\n\r\n"));
        var message = Assert.IsType<RtspStreamReader.Item.Message>(items.FirstOrDefault());
        Assert.Equal("3", message.Head.Header("CSeq"));
        Assert.Empty(message.Body);
    }

    [Fact]
    public void MessagesAndPacketsSplitAtEveryByte()
    {
        byte[] packet = [0x80, 0xE0, 0, 1, 0, 0, 0, 9, 0, 0, 0, 1, 0x65, 0x88];
        byte[] stream = [.. Bytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 3\r\n\r\nabc"),
            .. Interleaved(0, packet), .. Interleaved(1, 1, 2, 3),
            .. Bytes("RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n")];
        for (var boundary = 0; boundary <= stream.Length; boundary++)
        {
            var reader = new RtspStreamReader();
            var items = reader.Append(stream.AsSpan(0, boundary));
            items.AddRange(reader.Append(stream.AsSpan(boundary)));
            Assert.True(items.Count == 4, $"split at byte {boundary}: {items.Count} items");
            Assert.Equal(new RtspStreamReader.Item.Packet(0, packet), items[1]);
            Assert.Equal(new RtspStreamReader.Item.Packet(1, [1, 2, 3]), items[2]);
        }
    }

    [Fact]
    public void GarbageBetweenItemsIsSkipped()
    {
        var reader = new RtspStreamReader();
        var items = reader.Append([0x00, 0xFF, 0x0A, .. Interleaved(0, 9, 9), 0x01]);
        Assert.Equal([new RtspStreamReader.Item.Packet(0, [9, 9])], items);
    }

    [Fact]
    public void NegativeContentLengthIsAnError()
    {
        Assert.Equal(new RtspStreamFailure.BadContentLength("-1"), Failure("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: -1\r\n\r\n"));
    }

    [Fact]
    public void ContentLengthThatOverflowsIsAnError()
    {
        Assert.Equal(new RtspStreamFailure.BodyTooLarge(long.MaxValue),
            Failure("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 9223372036854775807\r\n\r\n"));
        Assert.Equal(new RtspStreamFailure.BodyTooLarge(long.MaxValue),
            Failure("RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 99999999999999999999999\r\n\r\n"));
    }

    [Fact]
    public void OversizedBodyIsAnError()
    {
        Assert.Equal(new RtspStreamFailure.BodyTooLarge(70000), Failure("RTSP/1.0 200 OK\r\nContent-Length: 70000\r\n\r\n"));
    }

    [Fact]
    public void ContentLengthThatIsNotANumberIsAnError()
    {
        Assert.Equal(new RtspStreamFailure.BadContentLength("abc"), Failure("RTSP/1.0 200 OK\r\nContent-Length: abc\r\n\r\n"));
        Assert.Equal(new RtspStreamFailure.BadContentLength("+5"), Failure("RTSP/1.0 200 OK\r\nContent-Length: +5\r\n\r\n"));
        Assert.Equal(new RtspStreamFailure.BadContentLength("1 2"), Failure("RTSP/1.0 200 OK\r\nContent-Length: 1 2\r\n\r\n"));
        Assert.Equal(new RtspStreamFailure.BadContentLength(""), Failure("RTSP/1.0 200 OK\r\nContent-Length:\r\n\r\n"));
    }

    [Fact]
    public void EndlessHeaderIsAnError()
    {
        var reader = new RtspStreamReader();
        reader.Append(Bytes("RTSP/1.0 200 OK\r\n"));
        var filler = Bytes("X-Filler: " + new string('a', 1000) + "\r\n");
        RtspStreamException? thrown = null;
        for (var i = 0; i < 80 && thrown is null; i++)
        {
            try { reader.Append(filler); }
            catch (RtspStreamException e) { thrown = e; }
        }
        Assert.Equal(new RtspStreamFailure.HeaderTooLong(), thrown?.Failure);
    }

    [Fact]
    public void WaitsForTheWholeBody()
    {
        var reader = new RtspStreamReader();
        Assert.Empty(reader.Append(Bytes("RTSP/1.0 200 OK\r\nContent-Length: 4\r\n\r\nab")));
        var items = reader.Append(Bytes("cd"));
        var message = Assert.IsType<RtspStreamReader.Item.Message>(items.FirstOrDefault());
        Assert.Equal(Bytes("abcd"), message.Body);
    }

    [Fact]
    public void FailureDescriptionIsReadable()
    {
        Assert.Equal("RTSP Content-Length \"-1\"", new RtspStreamFailure.BadContentLength("-1").ToString());
        Assert.Equal("RTSP body of 70000 bytes", new RtspStreamFailure.BodyTooLarge(70000).ToString());
    }
}
