using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BambuBar.Core.Camera;

// The camera protocols' parsing, apart from the network code so the unit tests can reach it on its own.
// A port of the Mac's CameraProtocol.swift.

/// <summary>One RTSP response (or a request from the server): its first line and header fields.</summary>
public sealed class RtspMessage : IEquatable<RtspMessage>
{
    public string StatusLine { get; }
    /// <summary>The status code of a response; 0 for a request.</summary>
    public int Status { get; }
    public bool IsResponse { get; }
    private readonly List<(string Name, string Value)> _fields = [];

    public RtspMessage(string head)
    {
        var lines = head.Split("\r\n");
        StatusLine = lines[0];
        IsResponse = StatusLine.StartsWith("RTSP/", StringComparison.Ordinal);
        if (IsResponse)
        {
            var words = StatusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Status = words.Length > 1 && int.TryParse(words[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var status)
                ? status : 0;
        }
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            _fields.Add((Spaces.Trim(line[..colon]), Spaces.Trim(line[(colon + 1)..])));
        }
    }

    /// <summary>The first field named <paramref name="name"/>, in any case.</summary>
    public string? Header(string name) =>
        _fields.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Value).FirstOrDefault();

    /// <summary>Every field named <paramref name="name"/>, in any case.</summary>
    public IReadOnlyList<string> Headers(string name) =>
        [.. _fields.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Value)];

    public bool Equals(RtspMessage? other) =>
        other is not null && StatusLine == other.StatusLine && _fields.SequenceEqual(other._fields);

    public override bool Equals(object? obj) => Equals(obj as RtspMessage);

    public override int GetHashCode() => HashCode.Combine(StatusLine, _fields.Count);

    public override string ToString() => StatusLine;
}

/// <summary>Why the RTSP connection's stream can't be followed any more.</summary>
public abstract record RtspStreamFailure
{
    public sealed record HeaderTooLong : RtspStreamFailure
    {
        public override string ToString() => "RTSP header too long";
    }

    public sealed record BadContentLength(string Value) : RtspStreamFailure
    {
        public override string ToString() => $"RTSP Content-Length \"{(Value.Length > 32 ? Value[..32] : Value)}\"";
    }

    /// <summary><see cref="long.MaxValue"/> when the length has more digits than any accepted size.</summary>
    public sealed record BodyTooLarge(long Size) : RtspStreamFailure
    {
        public override string ToString() => Size == long.MaxValue ? "RTSP body too large" : $"RTSP body of {Size} bytes";
    }
}

public sealed class RtspStreamException(RtspStreamFailure failure) : Exception(failure.ToString())
{
    public RtspStreamFailure Failure { get; } = failure;
}

/// <summary>
/// Splits what arrives on the RTSP connection into responses (or requests) and interleaved RTP/RTCP packets
/// (<c>$</c>, channel, 16-bit length, packet). Everything here comes from the network, so every length is checked
/// before it's used: a bad one is reported as an error instead of being trusted.
/// </summary>
public sealed class RtspStreamReader
{
    public abstract record Item
    {
        public sealed record Message(RtspMessage Head, byte[] Body) : Item
        {
            public bool Equals(Message? other) =>
                other is not null && Head.Equals(other.Head) && Body.AsSpan().SequenceEqual(other.Body);

            public override int GetHashCode() => HashCode.Combine(Head, Body.Length);
        }

        public sealed record Packet(byte Channel, byte[] Data) : Item
        {
            public bool Equals(Packet? other) =>
                other is not null && Channel == other.Channel && Data.AsSpan().SequenceEqual(other.Data);

            public override int GetHashCode() => HashCode.Combine(Channel, Data.Length);

            public override string ToString() => $"Packet {{ Channel = {Channel}, Data = [{string.Join(", ", Data)}] }}";
        }
    }

    /// <summary>The most a message's header may take before its blank line.</summary>
    public const int MaxHeader = 64 * 1024;
    /// <summary>The largest body accepted. Cameras send bodies only with DESCRIBE (an SDP, a few hundred bytes).</summary>
    public const int MaxBody = 64 * 1024;

    private byte[] _buffer = new byte[4096];
    private int _length;

    /// <summary>Adds received bytes and returns the items they completed. Throws <see cref="RtspStreamException"/> on a
    /// length that makes no sense; after that the stream can't be trusted any more and the reader shouldn't be used
    /// again.</summary>
    public List<Item> Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
        var items = new List<Item>();
        var offset = 0;
        try
        {
            while (offset < _length)
            {
                var first = _buffer[offset];
                if (first == 0x24) // '$': an interleaved RTP or RTCP packet
                {
                    if (_length - offset < 4) break;
                    var channel = _buffer[offset + 1];
                    var length = _buffer[offset + 2] << 8 | _buffer[offset + 3];
                    if (_length - offset < 4 + length) break;
                    items.Add(new Item.Packet(channel, _buffer.AsSpan(offset + 4, length).ToArray()));
                    offset += 4 + length;
                }
                else if (first is >= 0x41 and <= 0x5A) // 'A'…'Z': "RTSP/1.0 200 OK" or a request
                {
                    var end = _buffer.AsSpan(offset, _length - offset).IndexOf("\r\n\r\n"u8);
                    if (end < 0)
                    {
                        if (_length - offset > MaxHeader) throw new RtspStreamException(new RtspStreamFailure.HeaderTooLong());
                        break;
                    }
                    var headerEnd = offset + end;
                    var message = new RtspMessage(Encoding.UTF8.GetString(_buffer, offset, headerEnd - offset));
                    var length = ContentLength(message);
                    var bodyStart = headerEnd + 4;
                    if (_length - bodyStart < length) break;
                    items.Add(new Item.Message(message, _buffer.AsSpan(bodyStart, length).ToArray()));
                    offset = bodyStart + length;
                }
                else
                {
                    offset += 1; // resynchronise on the next '$' or message
                }
            }
        }
        finally
        {
            if (offset > 0)
            {
                offset = Math.Min(offset, _length);
                _buffer.AsSpan(offset, _length - offset).CopyTo(_buffer);
                _length -= offset;
            }
        }
        return items;
    }

    /// <summary>The body's size: digits only (no sign, no spaces inside), at most <see cref="MaxBody"/>.</summary>
    public static int ContentLength(RtspMessage message)
    {
        if (message.Header("Content-Length") is not { } value) return 0;
        if (value.Length == 0 || !value.All(c => c is >= '0' and <= '9'))
        {
            throw new RtspStreamException(new RtspStreamFailure.BadContentLength(value));
        }
        // More digits than any accepted size: reject before converting, which could overflow.
        if (value.Length > 6) throw new RtspStreamException(new RtspStreamFailure.BodyTooLarge(long.MaxValue));
        var length = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (length > MaxBody) throw new RtspStreamException(new RtspStreamFailure.BodyTooLarge(length));
        return length;
    }
}

/// <summary>Basic or Digest (RFC 2617, MD5) credentials for whichever the camera asks for; Digest preferred.</summary>
public sealed class RtspAuth
{
    private readonly bool _digest;
    private readonly string _realm = "", _nonce = "";
    private readonly string? _qop, _opaque;
    private readonly string _username;
    private readonly string _password;
    private int _count;

    public bool HasFailed { get; set; }

    /// <summary>The client nonce for each Digest response; fixed in tests.</summary>
    public Func<string> MakeCnonce { get; set; } = () => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private RtspAuth(string username, string password, bool digest, string realm = "", string nonce = "", string? qop = null,
        string? opaque = null)
    {
        _username = username;
        _password = password;
        _digest = digest;
        _realm = realm;
        _nonce = nonce;
        _qop = qop;
        _opaque = opaque;
    }

    /// <summary>Credentials answering one of <paramref name="challenges"/> (the WWW-Authenticate fields), or null when
    /// none is a scheme this client speaks or a Digest challenge lacks its realm or nonce.</summary>
    public static RtspAuth? Create(IEnumerable<string> challenges, string username, string password)
    {
        var list = challenges.ToList();
        if (list.FirstOrDefault(c => c.StartsWith("digest", StringComparison.OrdinalIgnoreCase)) is { } digest)
        {
            var p = Parameters(digest["digest".Length..]);
            if (!p.TryGetValue("realm", out var realm) || !p.TryGetValue("nonce", out var nonce)) return null;
            var offered = (p.GetValueOrDefault("qop") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Spaces.Trim);
            var qop = offered.Contains("auth") ? "auth" : null;
            return new RtspAuth(username, password, digest: true, realm, nonce, qop, p.GetValueOrDefault("opaque"));
        }
        if (list.Any(c => c.StartsWith("basic", StringComparison.OrdinalIgnoreCase))) return new RtspAuth(username, password, digest: false);
        return null;
    }

    /// <summary>The Authorization field's value for one request.</summary>
    public string Header(string method, string uri)
    {
        if (!_digest) return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        var ha1 = Md5($"{_username}:{_realm}:{_password}");
        var ha2 = Md5($"{method}:{uri}");
        var parts = new List<string> { $"username=\"{_username}\"", $"realm=\"{_realm}\"", $"nonce=\"{_nonce}\"", $"uri=\"{uri}\"" };
        if (_qop is not null)
        {
            _count++;
            var nc = ((uint)_count).ToString("x8", CultureInfo.InvariantCulture);
            var cnonce = MakeCnonce();
            parts.Add($"response=\"{Md5($"{ha1}:{_nonce}:{nc}:{cnonce}:{_qop}:{ha2}")}\"");
            parts.AddRange([$"qop={_qop}", $"nc={nc}", $"cnonce=\"{cnonce}\""]);
        }
        else
        {
            parts.Add($"response=\"{Md5($"{ha1}:{_nonce}:{ha2}")}\"");
        }
        if (_opaque is not null) parts.Add($"opaque=\"{_opaque}\"");
        return "Digest " + string.Join(", ", parts);
    }

    // RFC 2617 Digest is MD5 by definition; it isn't used for anything else.
#pragma warning disable CA5351
    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

    /// <summary><c>realm="x", nonce="y", stale=FALSE</c> → a dictionary (quoted values may contain commas).</summary>
    public static Dictionary<string, string> Parameters(string text)
    {
        var result = new Dictionary<string, string>();
        var rest = text.AsSpan();
        while (true)
        {
            rest = rest.TrimStart(" ,");
            var eq = rest.IndexOf('=');
            if (eq < 0) break;
            var key = Spaces.Trim(rest[..eq].ToString()).ToLowerInvariant();
            rest = rest[(eq + 1)..];
            ReadOnlySpan<char> value;
            if (rest.Length > 0 && rest[0] == '"')
            {
                var afterQuote = rest[1..];
                var close = afterQuote.IndexOf('"');
                value = close < 0 ? afterQuote : afterQuote[..close];
                rest = close < 0 ? [] : afterQuote[(close + 1)..];
            }
            else
            {
                var comma = rest.IndexOf(',');
                value = comma < 0 ? rest : rest[..comma];
                rest = comma < 0 ? [] : rest[comma..];
            }
            result[key] = Spaces.Trim(value.ToString());
        }
        return result;
    }
}

/// <summary>The video track a camera's DESCRIBE answer offers.</summary>
public static class Sdp
{
    public sealed record Video(VideoCodec Codec, int PayloadType, string? Control, IReadOnlyList<byte[]> ParameterSets);

    /// <summary>The first H.264 or H.265 video track. A track whose <c>rtpmap</c> names another codec (VP8, MJPEG…) is
    /// not one; only an SDP that maps no payload type at all is taken to be H.264, as some cameras leave it out.</summary>
    public static Video? VideoTrack(string sdp)
    {
        var inVideo = false;
        var inMedia = false;
        var payloadTypes = new List<int>();
        var codecs = new Dictionary<int, VideoCodec>();
        var mapped = new HashSet<int>();
        var fmtp = new Dictionary<int, Dictionary<string, string>>();
        string? control = null;
        string? sessionControl = null;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inVideo) break; // only the first video section
                inMedia = true;
                inVideo = line.StartsWith("m=video", StringComparison.Ordinal);
                payloadTypes = inVideo
                    ? [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(3).Select(ParseInt).OfType<int>()]
                    : [];
                continue;
            }
            if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                var value = line["a=control:".Length..];
                if (inVideo) control = value;
                else if (!inMedia) sessionControl = value;
                continue;
            }
            if (!inVideo) continue;
            if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                var parts = line["a=rtpmap:".Length..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || ParseInt(parts[0]) is not { } pt) continue;
                mapped.Add(pt);
                var name = (parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").ToUpperInvariant();
                if (name == "H264") codecs[pt] = VideoCodec.H264;
                else if (name is "H265" or "HEVC") codecs[pt] = VideoCodec.H265;
            }
            else if (line.StartsWith("a=fmtp:", StringComparison.Ordinal))
            {
                var parts = line["a=fmtp:".Length..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || ParseInt(parts[0]) is not { } pt) continue;
                var parameters = new Dictionary<string, string>();
                foreach (var item in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = item.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (pair.Length != 2) continue;
                    parameters[Spaces.Trim(pair[0]).ToLowerInvariant()] = Spaces.Trim(pair[1]);
                }
                fmtp[pt] = parameters;
            }
        }
        int? chosen = payloadTypes.Where(codecs.ContainsKey).Select(p => (int?)p).FirstOrDefault()
            ?? (mapped.Count == 0 && payloadTypes.Count > 0 ? payloadTypes[0] : null);
        if (chosen is not { } payloadType) return null;
        var codec = codecs.GetValueOrDefault(payloadType, VideoCodec.H264);
        var fmt = fmtp.GetValueOrDefault(payloadType) ?? [];
        IEnumerable<string> encoded = codec == VideoCodec.H264
            ? (fmt.GetValueOrDefault("sprop-parameter-sets") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "sprop-vps", "sprop-sps", "sprop-pps" }.Select(fmt.GetValueOrDefault).OfType<string>();
        var sets = encoded.Select(Base64).OfType<byte[]>().Where(s => s.Length > 0).ToList();
        return new Video(codec, payloadType, control ?? sessionControl, sets);
    }

    /// <summary>A track's <c>a=control</c> made absolute against the session's base URL.</summary>
    public static string Resolve(string? control, string @base)
    {
        if (string.IsNullOrEmpty(control) || control == "*") return @base;
        if (control.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) return control;
        return @base.EndsWith('/') ? @base + control : @base + "/" + control;
    }

    private static int? ParseInt(string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static byte[]? Base64(string text)
    {
        var padded = Spaces.Trim(text);
        while (padded.Length % 4 != 0) padded += "=";
        var bytes = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, bytes, out var written) ? bytes[..written] : null;
    }
}

/// <summary>Splits the port-6000 stream into JPEG payloads. Each one comes behind a 16-byte header whose first three
/// bytes give its size (little-endian).</summary>
public sealed class JpegFrameReader
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;

    /// <summary>Adds received bytes; returns the payloads they completed, or null if a header makes no sense.</summary>
    public List<byte[]>? Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
        var frames = new List<byte[]>();
        var offset = 0;
        while (_length - offset >= 16)
        {
            var size = _buffer[offset] | _buffer[offset + 1] << 8 | _buffer[offset + 2] << 16;
            if (size <= 0) return null;
            if (_length - offset < 16 + size) break;
            frames.Add(_buffer.AsSpan(offset + 16, size).ToArray());
            offset += 16 + size;
        }
        if (offset > 0)
        {
            _buffer.AsSpan(offset, _length - offset).CopyTo(_buffer);
            _length -= offset;
        }
        return frames;
    }

    /// <summary>A JPEG's picture size from its frame header (SOFn), without decoding it; null if
    /// <paramref name="jpeg"/> isn't a JPEG or has no frame header. Stands in for the Mac's decode check.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // no length
            if (marker is 0xD9 or 0xDA) return null; // end of image, or scan data before any frame header
            var length = jpeg[i + 2] << 8 | jpeg[i + 3];
            if (length < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (i + 9 > jpeg.Length) return null;
                var height = jpeg[i + 5] << 8 | jpeg[i + 6];
                var width = jpeg[i + 7] << 8 | jpeg[i + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }
            i += 2 + length;
        }
        return null;
    }
}

internal static class Spaces
{
    /// <summary>Trims spaces and tabs at both ends, like Swift's <c>trimmingCharacters(in: .whitespaces)</c>.</summary>
    public static string Trim(string text)
    {
        static bool IsSpace(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }
}
