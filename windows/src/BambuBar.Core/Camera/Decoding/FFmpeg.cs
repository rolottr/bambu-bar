using System.Runtime.InteropServices;

namespace BambuBar.Core.Camera.Decoding;

/// <summary>Whether FFmpeg loaded: its version, or what went wrong (in English, for logs and the probe).</summary>
public sealed record FFmpegStatus(string? Version, string? Problem, string? Folder)
{
    public bool IsAvailable => Problem is null;

    /// <summary>What the camera shows the person when FFmpeg is missing or the wrong version.</summary>
    public static string Message =>
        Loc.T("The camera needs FFmpeg, which Bambu Bar couldn't load. Reinstall Bambu Bar to repair it.");
}

/// <summary>
/// Finds and loads FFmpeg's shared libraries (ledger D17: LGPL, dynamically linked): libavcodec 63, libavutil 61 and
/// libswscale 10, which FFmpeg 9 ships; libswresample 7 is loaded only when present (general builds link it into
/// libavcodec; ours, with audio off, doesn't). On Windows they come with the app in <c>&lt;app&gt;\ffmpeg\</c>, our
/// minimal build (windows/ffmpeg/build.sh, pins in version.txt); on a Mac, for development,
/// Homebrew's; on Linux the system's. <c>BAMBUBAR_FFMPEG</c> or <see cref="Folder"/> picks another folder. Loading
/// happens once, on first use, from any thread; libraries that are missing or of another major version leave
/// <see cref="Status"/> with the problem, so the camera can say so instead of crashing.
/// </summary>
public static class FFmpeg
{
    public const int AvcodecMajor = 63;
    public const int AvutilMajor = 61;
    public const int SwscaleMajor = 10;
    public const int SwresampleMajor = 7;

    private static readonly Lock Gate = new();
    private static string? _folder;
    private static FFmpegStatus? _status;
    private static FFmpegApi? _api;

    /// <summary>Where the libraries are looked for: <c>BAMBUBAR_FFMPEG</c>, else <c>&lt;app&gt;\ffmpeg</c> on Windows,
    /// Homebrew's lib folder on a Mac, and null (the system's search) on Linux.</summary>
    public static string? DefaultFolder
    {
        get
        {
            if (Environment.GetEnvironmentVariable("BAMBUBAR_FFMPEG") is { Length: > 0 } custom) return custom;
            if (OperatingSystem.IsWindows()) return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (OperatingSystem.IsMacOS())
            {
                string[] homebrew = ["/opt/homebrew/opt/ffmpeg/lib", "/usr/local/opt/ffmpeg/lib"];
                return homebrew.FirstOrDefault(Directory.Exists) ?? homebrew[0];
            }
            return null;
        }
    }

    /// <summary>The folder to load from; set it before the first use (afterwards the libraries are loaded for good).</summary>
    public static string? Folder
    {
        get { lock (Gate) return _folder ?? DefaultFolder; }
        set
        {
            lock (Gate)
            {
                if (_status is not null) throw new InvalidOperationException("FFmpeg is loaded already.");
                _folder = value;
            }
        }
    }

    /// <summary>Loads the libraries if that hasn't been tried yet, and says how it went.</summary>
    public static FFmpegStatus Status
    {
        get
        {
            lock (Gate)
            {
                if (_status is null) (_status, _api) = Load(_folder ?? DefaultFolder);
                return _status;
            }
        }
    }

    /// <summary>The functions; throws <see cref="FFmpegUnavailableException"/> when they couldn't be loaded.</summary>
    internal static FFmpegApi Api
    {
        get
        {
            var status = Status;
            lock (Gate) return _api ?? throw new FFmpegUnavailableException(status);
        }
    }

    /// <summary>A library's file name on this system: <c>avcodec-63.dll</c>, <c>libavcodec.63.dylib</c>,
    /// <c>libavcodec.so.63</c>.</summary>
    public static string FileName(string library, int major) =>
        OperatingSystem.IsWindows() ? $"{library}-{major}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{library}.{major}.dylib"
        : $"lib{library}.so.{major}";

    /// <summary>Loads from <paramref name="folder"/> (null: the system's search). Loading the same libraries again is harmless.</summary>
    internal static (FFmpegStatus Status, FFmpegApi? Api) Load(string? folder)
    {
        if (IntPtr.Size != 8) return (new FFmpegStatus(null, "FFmpeg is only used in 64-bit processes.", folder), null);
        if (folder is not null && !Directory.Exists(folder))
            return (new FFmpegStatus(null, $"The FFmpeg folder {folder} doesn't exist.", folder), null);
        // Dependencies first, so each library finds the ones it links already loaded (Windows looks for them by name).
        (string Name, int Major, bool Required)[] libraries =
            [("avutil", AvutilMajor, true), ("swresample", SwresampleMajor, false), ("avcodec", AvcodecMajor, true), ("swscale", SwscaleMajor, true)];
        var handles = new Dictionary<string, IntPtr>();
        foreach (var (name, major, required) in libraries)
        {
            var file = FileName(name, major);
            var path = folder is null ? file : Path.Combine(folder, file);
            if (folder is not null && !File.Exists(path))
            {
                if (!required) continue;
                return (new FFmpegStatus(null, Missing(folder, name, major), folder), null);
            }
            try
            {
                handles[name] = folder is null
                    ? NativeLibrary.Load(file)
                    : NativeLibrary.Load(path, typeof(FFmpeg).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
            {
                if (!required) continue;
                return (new FFmpegStatus(null, $"{file} couldn't be loaded: {e.Message}", folder), null);
            }
        }
        try
        {
            var api = new FFmpegApi(handles["avutil"], handles["avcodec"], handles["swscale"]);
            var problem = api.VersionProblem();
            return problem is null ? (new FFmpegStatus(api.VersionInfo(), null, folder), api) : (new FFmpegStatus(null, problem, folder), null);
        }
        catch (EntryPointNotFoundException e)
        {
            return (new FFmpegStatus(null, $"FFmpeg lacks a function Bambu Bar uses: {e.Message}", folder), null);
        }
    }

    /// <summary>"avcodec-63.dll isn't in C:\…\ffmpeg (it has avcodec-62.dll: FFmpeg 9 is needed)".</summary>
    private static string Missing(string folder, string name, int major)
    {
        var pattern = OperatingSystem.IsWindows() ? $"{name}-*.dll" : OperatingSystem.IsMacOS() ? $"lib{name}.*.dylib" : $"lib{name}.so.*";
        var others = Directory.GetFiles(folder, pattern).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var text = $"{FileName(name, major)} isn't in {folder}";
        return others.Count > 0 ? $"{text} (it has {string.Join(", ", others)}: FFmpeg 9 is needed)" : text;
    }
}

/// <summary>FFmpeg couldn't be loaded; <see cref="Exception.Message"/> is for the person, <see cref="Status"/> says why.</summary>
public sealed class FFmpegUnavailableException(FFmpegStatus status) : CameraDecodeException(FFmpegStatus.Message)
{
    public FFmpegStatus Status { get; } = status;
}

/// <summary>A camera's pictures can't be decoded (no decoder for its codec, or FFmpeg is missing). The message is for the
/// person.</summary>
public class CameraDecodeException(string message) : Exception(message);

/// <summary>
/// The few FFmpeg functions the camera uses, bound by hand (no generated bindings): find a decoder, open it with the
/// stream's parameter sets, send packets, receive frames, convert them to BGRA. Struct fields are touched only where
/// FFmpeg has no function for them, at offsets that have been stable across major versions (see
/// <see cref="FFmpegDecoder"/>). The calling convention is the platform's C one (x64 and ARM64 have just one).
/// </summary>
internal sealed unsafe class FFmpegApi
{
    // libavutil
    public readonly delegate* unmanaged<uint> avutil_version;
    public readonly delegate* unmanaged<byte*> av_version_info;
    public readonly delegate* unmanaged<nuint, void*> av_mallocz;
    public readonly delegate* unmanaged<void*, void> av_free;
    public readonly delegate* unmanaged<void*> av_frame_alloc;
    public readonly delegate* unmanaged<void**, void> av_frame_free;
    public readonly delegate* unmanaged<void*, void> av_frame_unref;
    public readonly delegate* unmanaged<int, byte*, nuint, int> av_strerror;
    public readonly delegate* unmanaged<byte*, int> av_get_pix_fmt;
    public readonly delegate* unmanaged<void*, byte*, byte*, int, int> av_opt_set;
    public readonly delegate* unmanaged<int, void> av_log_set_level;
    // libavcodec
    public readonly delegate* unmanaged<uint> avcodec_version;
    public readonly delegate* unmanaged<byte*, void*> avcodec_find_decoder_by_name;
    public readonly delegate* unmanaged<void*, void*> avcodec_alloc_context3;
    public readonly delegate* unmanaged<void**, void> avcodec_free_context;
    public readonly delegate* unmanaged<void*> avcodec_parameters_alloc;
    public readonly delegate* unmanaged<void**, void> avcodec_parameters_free;
    public readonly delegate* unmanaged<void*, void*, int> avcodec_parameters_to_context;
    public readonly delegate* unmanaged<void*, void*, void**, int> avcodec_open2;
    public readonly delegate* unmanaged<void*, void*, int> avcodec_send_packet;
    public readonly delegate* unmanaged<void*, void*, int> avcodec_receive_frame;
    public readonly delegate* unmanaged<void*, void> avcodec_flush_buffers;
    public readonly delegate* unmanaged<void*> av_packet_alloc;
    public readonly delegate* unmanaged<void**, void> av_packet_free;
    public readonly delegate* unmanaged<void*, byte*, int, int> av_packet_from_data;
    public readonly delegate* unmanaged<void*, void> av_packet_unref;
    // libswscale
    public readonly delegate* unmanaged<uint> swscale_version;
    public readonly delegate* unmanaged<void*> sws_alloc_context;
    public readonly delegate* unmanaged<void**, void> sws_free_context;
    public readonly delegate* unmanaged<void*, void*, void*, int> sws_scale_frame;

    /// <summary>AVERROR(EAGAIN): EAGAIN is 11 on Windows (UCRT) and Linux, 35 on macOS.</summary>
    public readonly int ErrorAgain = OperatingSystem.IsMacOS() ? -35 : -11;
    /// <summary>AVERROR_EOF, FFERRTAG('E','O','F',' ').</summary>
    public const int ErrorEof = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));
    /// <summary>AV_LOG_QUIET: FFmpeg writes nothing to stderr (a WinExe has none; tests would fill up).</summary>
    public const int LogQuiet = -8;
    public const int LogWarning = 24;

    /// <summary>AV_PIX_FMT_BGRA, looked up by name rather than counted from the enum.</summary>
    public readonly int PixelFormatBgra;

    public FFmpegApi(IntPtr avutil, IntPtr avcodec, IntPtr swscale)
    {
        avutil_version = (delegate* unmanaged<uint>)NativeLibrary.GetExport(avutil, nameof(avutil_version));
        av_version_info = (delegate* unmanaged<byte*>)NativeLibrary.GetExport(avutil, nameof(av_version_info));
        av_mallocz = (delegate* unmanaged<nuint, void*>)NativeLibrary.GetExport(avutil, nameof(av_mallocz));
        av_free = (delegate* unmanaged<void*, void>)NativeLibrary.GetExport(avutil, nameof(av_free));
        av_frame_alloc = (delegate* unmanaged<void*>)NativeLibrary.GetExport(avutil, nameof(av_frame_alloc));
        av_frame_free = (delegate* unmanaged<void**, void>)NativeLibrary.GetExport(avutil, nameof(av_frame_free));
        av_frame_unref = (delegate* unmanaged<void*, void>)NativeLibrary.GetExport(avutil, nameof(av_frame_unref));
        av_strerror = (delegate* unmanaged<int, byte*, nuint, int>)NativeLibrary.GetExport(avutil, nameof(av_strerror));
        av_get_pix_fmt = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(avutil, nameof(av_get_pix_fmt));
        av_opt_set = (delegate* unmanaged<void*, byte*, byte*, int, int>)NativeLibrary.GetExport(avutil, nameof(av_opt_set));
        av_log_set_level = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(avutil, nameof(av_log_set_level));

        avcodec_version = (delegate* unmanaged<uint>)NativeLibrary.GetExport(avcodec, nameof(avcodec_version));
        avcodec_find_decoder_by_name = (delegate* unmanaged<byte*, void*>)NativeLibrary.GetExport(avcodec, nameof(avcodec_find_decoder_by_name));
        avcodec_alloc_context3 = (delegate* unmanaged<void*, void*>)NativeLibrary.GetExport(avcodec, nameof(avcodec_alloc_context3));
        avcodec_free_context = (delegate* unmanaged<void**, void>)NativeLibrary.GetExport(avcodec, nameof(avcodec_free_context));
        avcodec_parameters_alloc = (delegate* unmanaged<void*>)NativeLibrary.GetExport(avcodec, nameof(avcodec_parameters_alloc));
        avcodec_parameters_free = (delegate* unmanaged<void**, void>)NativeLibrary.GetExport(avcodec, nameof(avcodec_parameters_free));
        avcodec_parameters_to_context = (delegate* unmanaged<void*, void*, int>)NativeLibrary.GetExport(avcodec, nameof(avcodec_parameters_to_context));
        avcodec_open2 = (delegate* unmanaged<void*, void*, void**, int>)NativeLibrary.GetExport(avcodec, nameof(avcodec_open2));
        avcodec_send_packet = (delegate* unmanaged<void*, void*, int>)NativeLibrary.GetExport(avcodec, nameof(avcodec_send_packet));
        avcodec_receive_frame = (delegate* unmanaged<void*, void*, int>)NativeLibrary.GetExport(avcodec, nameof(avcodec_receive_frame));
        avcodec_flush_buffers = (delegate* unmanaged<void*, void>)NativeLibrary.GetExport(avcodec, nameof(avcodec_flush_buffers));
        av_packet_alloc = (delegate* unmanaged<void*>)NativeLibrary.GetExport(avcodec, nameof(av_packet_alloc));
        av_packet_free = (delegate* unmanaged<void**, void>)NativeLibrary.GetExport(avcodec, nameof(av_packet_free));
        av_packet_from_data = (delegate* unmanaged<void*, byte*, int, int>)NativeLibrary.GetExport(avcodec, nameof(av_packet_from_data));
        av_packet_unref = (delegate* unmanaged<void*, void>)NativeLibrary.GetExport(avcodec, nameof(av_packet_unref));

        swscale_version = (delegate* unmanaged<uint>)NativeLibrary.GetExport(swscale, nameof(swscale_version));
        sws_alloc_context = (delegate* unmanaged<void*>)NativeLibrary.GetExport(swscale, nameof(sws_alloc_context));
        sws_free_context = (delegate* unmanaged<void**, void>)NativeLibrary.GetExport(swscale, nameof(sws_free_context));
        sws_scale_frame = (delegate* unmanaged<void*, void*, void*, int>)NativeLibrary.GetExport(swscale, nameof(sws_scale_frame));

        // BAMBUBAR_FFMPEG_LOG=1 lets FFmpeg's warnings through to stderr (the probe, debugging a stream).
        av_log_set_level(Environment.GetEnvironmentVariable("BAMBUBAR_FFMPEG_LOG") == "1" ? LogWarning : LogQuiet);
        fixed (byte* bgra = "bgra"u8) PixelFormatBgra = av_get_pix_fmt(bgra);
    }

    /// <summary>Null when every library is the major version the bindings were written for.</summary>
    public string? VersionProblem()
    {
        (string Name, uint Version, int Major)[] found =
            [("libavutil", avutil_version(), FFmpeg.AvutilMajor), ("libavcodec", avcodec_version(), FFmpeg.AvcodecMajor), ("libswscale", swscale_version(), FFmpeg.SwscaleMajor)];
        var wrong = found.Where(f => (int)(f.Version >> 16) != f.Major)
            .Select(f => $"{f.Name} {f.Version >> 16}.{(f.Version >> 8) & 0xFF}.{f.Version & 0xFF} (Bambu Bar needs {f.Major})").ToList();
        return wrong.Count == 0 ? null : $"FFmpeg is another version than Bambu Bar's: {string.Join(", ", wrong)}";
    }

    /// <summary>"n9.0.2-39-g7adfbe40dc" or "9.0.2".</summary>
    public string VersionInfo() => Marshal.PtrToStringUTF8((IntPtr)av_version_info()) ?? "?";

    /// <summary>FFmpeg's text for an error code ("Invalid data found when processing input").</summary>
    public string Describe(int error)
    {
        var buffer = stackalloc byte[128];
        return av_strerror(error, buffer, 128) == 0 ? Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {error}" : $"error {error}";
    }
}
