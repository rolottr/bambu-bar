using BambuBar.Core.Camera;

namespace BambuBar.Core.Tests.Camera;

// Ported from the Mac's Tests/BambuBarTests/CameraProtocolTests.swift.

public class SdpTests
{
    [Fact]
    public void H264TrackWithParameterSets()
    {
        var sdp = string.Join("\r\n",
            "v=0", "o=- 1 1 IN IP4 0.0.0.0", "s=Live", "t=0 0", "a=control:*",
            "m=audio 0 RTP/AVP 97", "a=rtpmap:97 MPEG4-GENERIC/16000", "a=control:track0",
            "m=video 0 RTP/AVP 96", "c=IN IP4 0.0.0.0", "a=rtpmap:96 H264/90000",
            "a=fmtp:96 packetization-mode=1;profile-level-id=42C00A;sprop-parameter-sets=Z0LACtkJbARAAAADAEAAAAMBA8SJkg==,aMuDyyA=",
            "a=control:track1");
        var video = Assert.IsType<Sdp.Video>(Sdp.VideoTrack(sdp));
        Assert.Equal(VideoCodec.H264, video.Codec);
        Assert.Equal(96, video.PayloadType);
        Assert.Equal("track1", video.Control);
        Assert.Equal([Fixtures.H264Sps, Fixtures.H264Pps], video.ParameterSets);
    }

    [Fact]
    public void H265TrackWithParameterSets()
    {
        var sdp = """
            v=0
            m=video 0 RTP/AVP 98
            a=rtpmap:98 H265/90000
            a=fmtp:98 sprop-vps=QAEMAf//AWAAAAMAkAAAAwAAAwAekoCQ; sprop-sps=QgEBAWAAAAMAkAAAAwAAAwAeoCCBBZZKkkyvAWgIAAADAAgAAAMAEEA; sprop-pps=RAHBcrQiQA==
            """;
        var video = Assert.IsType<Sdp.Video>(Sdp.VideoTrack(sdp));
        Assert.Equal(VideoCodec.H265, video.Codec);
        Assert.Equal(98, video.PayloadType);
        Assert.Null(video.Control);
        // Unpadded base64 is accepted too.
        Assert.Equal([Fixtures.H265Vps, Fixtures.H265Sps, Fixtures.H265Pps], video.ParameterSets);
    }

    [Fact]
    public void SessionControlWhenTheTrackHasNone()
    {
        var sdp = "v=0\na=control:rtsps://printer/streaming/live/1\nm=video 0 RTP/AVP 96\na=rtpmap:96 H264/90000\n";
        Assert.Equal("rtsps://printer/streaming/live/1", Assert.IsType<Sdp.Video>(Sdp.VideoTrack(sdp)).Control);
    }

    [Fact]
    public void NoVideo()
    {
        Assert.Null(Sdp.VideoTrack("v=0\nm=audio 0 RTP/AVP 97\na=rtpmap:97 PCMU/8000\n"));
    }

    [Fact]
    public void AnotherCodecIsNotTakenForH264()
    {
        Assert.Null(Sdp.VideoTrack("v=0\nm=video 0 RTP/AVP 96\na=rtpmap:96 VP8/90000\n"));
        Assert.Null(Sdp.VideoTrack("v=0\nm=video 0 RTP/AVP 26 96\na=rtpmap:26 JPEG/90000\na=rtpmap:96 VP9/90000\n"));
    }

    [Fact]
    public void ThePlayableTrackIsPickedOverAnotherCodec()
    {
        var video = Assert.IsType<Sdp.Video>(Sdp.VideoTrack("v=0\nm=video 0 RTP/AVP 96 97\na=rtpmap:96 VP8/90000\na=rtpmap:97 H264/90000\n"));
        Assert.Equal(VideoCodec.H264, video.Codec);
        Assert.Equal(97, video.PayloadType);
    }

    /// <summary>Some cameras send no rtpmap at all; their video is H.264.</summary>
    [Fact]
    public void NoRtpmapAtAllMeansH264()
    {
        var video = Assert.IsType<Sdp.Video>(Sdp.VideoTrack("v=0\nm=video 0 RTP/AVP 96\na=control:track1\n"));
        Assert.Equal(VideoCodec.H264, video.Codec);
        Assert.Equal(96, video.PayloadType);
    }

    [Fact]
    public void Resolve()
    {
        const string @base = "rtsps://192.168.0.5:322/streaming/live/1";
        Assert.Equal(@base + "/track1", Sdp.Resolve("track1", @base));
        Assert.Equal(@base + "/track1", Sdp.Resolve("track1", @base + "/"));
        Assert.Equal("rtsps://other/track2", Sdp.Resolve("rtsps://other/track2", @base));
        Assert.Equal(@base, Sdp.Resolve("*", @base));
        Assert.Equal(@base, Sdp.Resolve(null, @base));
    }
}

public class RtspMessageTests
{
    [Fact]
    public void Response()
    {
        var message = new RtspMessage(string.Join("\r\n",
            "RTSP/1.0 401 Unauthorized", "CSeq: 2",
            "WWW-Authenticate: Digest realm=\"x\", nonce=\"y\"", "www-authenticate: Basic realm=\"x\"",
            "Session: 1234ABCD;timeout=60"));
        Assert.True(message.IsResponse);
        Assert.Equal(401, message.Status);
        Assert.Equal("RTSP/1.0 401 Unauthorized", message.StatusLine);
        Assert.Equal("2", message.Header("cseq"));
        Assert.Equal("1234ABCD;timeout=60", message.Header("Session"));
        Assert.Equal(2, message.Headers("WWW-Authenticate").Count);
        Assert.Null(message.Header("Content-Length"));
    }

    [Fact]
    public void RequestFromTheServer()
    {
        var message = new RtspMessage("OPTIONS * RTSP/1.0\r\nCSeq: 7");
        Assert.False(message.IsResponse);
        Assert.Equal(0, message.Status);
    }
}

public class RtspAuthTests
{
    /// <summary>The worked example in RFC 2617, section 3.5.</summary>
    [Fact]
    public void DigestWithQopMatchesRfc2617()
    {
        var auth = Assert.IsType<RtspAuth>(RtspAuth.Create(
            ["Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""],
            "Mufasa", "Circle Of Life"));
        auth.MakeCnonce = () => "0a4f113b";
        var header = auth.Header("GET", "/dir/index.html");
        Assert.StartsWith("Digest ", header);
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header);
        Assert.Contains("qop=auth", header);
        Assert.Contains("nc=00000001", header);
        Assert.Contains("cnonce=\"0a4f113b\"", header);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header);
        Assert.Contains("nc=00000002", auth.Header("GET", "/dir/index.html"));
    }

    [Fact]
    public void DigestWithoutQop()
    {
        var auth = Assert.IsType<RtspAuth>(RtspAuth.Create(["Basic realm=\"Bambu\"", "Digest realm=\"Bambu\", nonce=\"abc123\""],
            "bblp", "12345678"));
        var header = auth.Header("DESCRIBE", "rtsps://192.168.0.5:322/streaming/live/1");
        Assert.True(header.StartsWith("Digest ", StringComparison.Ordinal), "Digest is preferred over Basic");
        Assert.Contains("response=\"2c512312cf09c400ef91e4c0e78c36ad\"", header);
        Assert.DoesNotContain("qop=", header);
    }

    [Fact]
    public void Basic()
    {
        var auth = Assert.IsType<RtspAuth>(RtspAuth.Create(["Basic realm=\"Bambu\""], "bblp", "12345678"));
        Assert.Equal("Basic YmJscDoxMjM0NTY3OA==", auth.Header("DESCRIBE", "rtsps://x"));
    }

    [Fact]
    public void UnknownOrIncompleteChallenge()
    {
        Assert.Null(RtspAuth.Create(["Bearer realm=\"x\""], "bblp", "1"));
        Assert.Null(RtspAuth.Create(["Digest realm=\"x\""], "bblp", "1"));
        Assert.Null(RtspAuth.Create([], "bblp", "1"));
    }

    [Fact]
    public void ParametersKeepCommasInQuotes()
    {
        Assert.Equal(new Dictionary<string, string> { ["realm"] = "a, b", ["nonce"] = "xyz", ["stale"] = "FALSE" },
            RtspAuth.Parameters(" realm=\"a, b\", nonce=xyz, stale=FALSE"));
    }
}

public class JpegFrameReaderTests
{
    private static byte[] Frame(params byte[] payload)
    {
        var size = payload.Length;
        return [(byte)(size & 0xFF), (byte)(size >> 8 & 0xFF), (byte)(size >> 16 & 0xFF), 0, .. new byte[12], .. payload];
    }

    [Fact]
    public void FramesInOneChunk()
    {
        var reader = new JpegFrameReader();
        byte[] bytes = [.. Frame(0xFF, 0xD8, 1, 2), .. Frame(0xFF, 0xD8, 3)];
        Assert.Equal([[0xFF, 0xD8, 1, 2], [0xFF, 0xD8, 3]], reader.Append(bytes)!);
    }

    [Fact]
    public void FrameSplitAcrossChunks()
    {
        var reader = new JpegFrameReader();
        var payload = Enumerable.Repeat((byte)0xAB, 300).ToArray();
        var bytes = Frame(payload);
        Assert.Empty(reader.Append(bytes[..10])!); // half a header
        Assert.Empty(reader.Append(bytes[10..200])!);
        Assert.Equal([payload], reader.Append([.. bytes[200..], .. Frame(7)[..5]])!);
        Assert.Equal([[7]], reader.Append(Frame(7)[5..])!);
    }

    [Fact]
    public void EmptyFrameIsMalformed()
    {
        var reader = new JpegFrameReader();
        Assert.Null(reader.Append(new byte[16]));
    }

    /// <summary>Windows only: the Mac decodes each frame to learn its size, Windows reads it from the frame header.
    /// The mock camera's 64×48 JPEG (shared/scripts/camera/camera.py).</summary>
    [Fact]
    public void SizeComesFromTheFrameHeader()
    {
        var jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAyAP/bAEMACAQEBAQEBQUFBQUFBgYGBgYGBgYGBgYGBgcHBwgICAcHBwYGBwcICAgI"
            + "CQkJCAgICAkJCgoKDAwLCw4ODhERFP/EAEwAAQEAAAAAAAAAAAAAAAAAAAAGAQEBAAAAAAAAAAAAAAAAAAAABhABAAAAAAAAAAAAAAAAAAAAABEB"
            + "AAAAAAAAAAAAAAAAAAAAAP/AABEIADAAQAMBIgACEQADEQD/2gAMAwEAAhEDEQA/AIsBKIkAAAAAAAAAAAAAAAAAAAAAB//Z");
        Assert.Equal((64, 48), JpegFrameReader.Size(jpeg));
        Assert.Null(JpegFrameReader.Size([0xFF, 0xD8, 1, 2]));
        Assert.Null(JpegFrameReader.Size(jpeg[2..]));
        Assert.Null(JpegFrameReader.Size(jpeg[..40]));
    }
}
