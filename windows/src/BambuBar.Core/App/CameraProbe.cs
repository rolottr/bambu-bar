using System.Diagnostics;
using System.Globalization;
using BambuBar.Core.Camera;
using BambuBar.Core.Camera.Decoding;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Core.App;

/// <summary>
/// <c>--probe-camera &lt;host&gt; &lt;serial&gt; &lt;access-code&gt; [seconds] [--jpeg|--rtsp] [--model &lt;name&gt;]
/// [--insecure-tls] [--out &lt;file.png&gt;]</c>: opens the camera like the app does, logs the handshake (certificate,
/// RTSP replies, SDP) and the frames, decodes them with FFmpeg as the app does and saves the first picture as a PNG
/// (<c>bambubar-camera.png</c> in the temporary folder unless <c>--out</c> says otherwise). Without
/// <c>--jpeg</c>/<c>--rtsp</c> the model decides (<c>--model</c> takes a name or a model code; else the serial says;
/// a P2S if unknown). A port of the Mac's Probe.runCamera, which decodes with VideoToolbox. Exits 0 once a picture was
/// decoded and saved.
/// </summary>
public static class CameraProbe
{
    public sealed record Arguments(string Host, string Serial, string Code, double Seconds, CameraTransport Transport, bool Insecure, string Output);

    /// <param name="codeFor">Looks up a saved code when the code given is "keychain" (as on the Mac).</param>
    public static Arguments? Parse(IReadOnlyList<string> args, Func<string, string?>? codeFor = null)
    {
        var list = args.ToList();
        var i = list.IndexOf("--probe-camera");
        if (i < 0 || i + 3 >= args.Count) return null;
        var seconds = i + 4 < args.Count && double.TryParse(args[i + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 15;
        var m = list.IndexOf("--model");
        var name = m >= 0 && m + 1 < args.Count ? args[m + 1] : null;
        var model = PrinterModel.Named(name) ?? (name is null ? null : PrinterModel.Find(name)) ?? PrinterModel.Find("", args[i + 2]);
        var transport = CameraTransports.ForModel(model?.Name ?? "P2S");
        if (args.Contains("--jpeg")) transport = CameraTransport.Jpeg;
        if (args.Contains("--rtsp")) transport = CameraTransport.Rtsps;
        var code = args[i + 3] == "keychain" ? codeFor?.Invoke(args[i + 2]) ?? "" : args[i + 3];
        var o = list.IndexOf("--out");
        var output = o >= 0 && o + 1 < args.Count ? args[o + 1] : Path.Combine(Path.GetTempPath(), "bambubar-camera.png");
        return new(args[i + 1], args[i + 2], code, seconds, transport, args.Contains("--insecure-tls"), output);
    }

    /// <summary>Runs the probe; returns the exit code (0 when a picture was decoded and saved).</summary>
    public static async Task<int> RunAsync(Arguments a, TextWriter output)
    {
        var clock = Stopwatch.StartNew();
        void Log(string text)
        {
            lock (output)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds,5:0.0}s] ") + text);
                output.Flush();
            }
        }

        var ffmpeg = FFmpeg.Status;
        Log(ffmpeg.IsAvailable ? $"FFmpeg {ffmpeg.Version} from {ffmpeg.Folder ?? "the system"}" : $"FFmpeg unavailable: {ffmpeg.Problem}");
        using var decoder = ffmpeg.IsAvailable ? new FFmpegDecoder() : null;

        var gate = new Lock();
        int frames = 0, keyframes = 0, decoded = 0;
        long bytes = 0;
        var saved = false;
        string? failure = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CameraClient.Create(new CameraConfig { Host = a.Host, Serial = a.Serial, AccessCode = a.Code, Transport = a.Transport, Insecure = a.Insecure });
        client.Log = Log;
        client.OnEvent = e =>
        {
            // Events come one at a time from the client's reader (no SynchronizationContext here).
            CameraFrame? picture = null;
            var timer = Stopwatch.StartNew();
            lock (gate)
            {
                switch (e)
                {
                    case CameraEvent.Started started:
                        Log($"stream: {started.Description}");
                        return;
                    case CameraEvent.Video video:
                        frames++;
                        bytes += video.Frame.Data.Length;
                        if (video.IsKeyframe) keyframes++;
                        picture = Decode(() => decoder?.Decode(video.Frame));
                        break;
                    case CameraEvent.Image image:
                        frames++;
                        bytes += image.Jpeg.Length;
                        picture = Decode(() => decoder?.DecodeJpeg(image.Jpeg));
                        break;
                    case CameraEvent.Failed failed:
                        failure = failed.Failure.Message;
                        Log($"failed: {failure}");
                        done.TrySetResult();
                        return;
                }
                if (picture is null) return;
                decoded++;
                if (saved) return;
                saved = true;
                var ms = timer.Elapsed.TotalMilliseconds;
                try
                {
                    var folder = Path.GetDirectoryName(Path.GetFullPath(a.Output));
                    if (folder is not null) Directory.CreateDirectory(folder);
                    File.WriteAllBytes(a.Output, Png.Encode(picture.ToRgba()));
                    var (r, g, b) = picture.Pixel(picture.Width / 2, picture.Height / 2);
                    Log(string.Create(CultureInfo.InvariantCulture,
                        $"decoded a {picture.Width}×{picture.Height} frame in {ms:0.0} ms (centre #{r:X2}{g:X2}{b:X2}): {Path.GetFullPath(a.Output)}"));
                }
                catch (IOException x)
                {
                    saved = false;
                    Log($"couldn't save the frame: {x.Message}");
                }
            }
        };
        CameraFrame? Decode(Func<CameraFrame?> decode)
        {
            try
            {
                return decode();
            }
            catch (CameraDecodeException x)
            {
                Log($"decode failed: {x.Message}");
                return null;
            }
        }

        Log($"opening {(a.Transport == CameraTransport.Rtsps ? "RTSPS on port 322" : "the JPEG stream on port 6000")} at {a.Host}");
        client.Start();
        using var progress = new Timer(_ =>
        {
            lock (gate)
                Log(a.Transport == CameraTransport.Jpeg ? $"{frames} frames so far" : $"{frames} frames, {keyframes} keyframes, {bytes / 1024} KB so far");
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(a.Seconds))).ConfigureAwait(false);
        client.Stop();
        lock (gate)
        {
            var elapsed = clock.Elapsed.TotalSeconds;
            Log(string.Create(CultureInfo.InvariantCulture, $"done: {frames} frames ({frames / elapsed:0.0} fps), {decoded} decoded"));
            return frames > 0 && saved && failure is null ? 0 : 1;
        }
    }
}
