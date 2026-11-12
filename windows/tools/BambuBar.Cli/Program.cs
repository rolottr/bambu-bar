using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

// bambubar --probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]
// bambubar --probe-camera <host> <serial> <access-code> [seconds] [--jpeg|--rtsp] [--model <name>] [--insecure-tls] [--out <file.png>]
if (CameraProbe.Parse(args) is { } camera) return await CameraProbe.RunAsync(camera, Console.Out);
if (Probe.Parse(args) is { } probe) return await Probe.RunAsync(probe, Console.Out);
// bambubar --art <out.png> <width> <height>: the installer's printer (installer/art/make-art.py), a P2S printing at 64 %
if (args is ["--art", var output, var w, var h])
{
    var scene = new PrinterArt.Scene
    {
        Shape = PrinterShapes.FromModel("P2S"), State = JobState.Printing, Progress = 0.64, Filament = "F4A925FF",
        Spools = ["E8D7B0FF", "161616FF", "3F8E43FF", "F4A925FF"], LightOn = true, Dimmed = false,
    };
    var art = PrinterArt.Render(scene, int.Parse(w, System.Globalization.CultureInfo.InvariantCulture), int.Parse(h, System.Globalization.CultureInfo.InvariantCulture));
    if (art is null) { Console.Error.WriteLine("The printer renders aren't there (shared/Printers)."); return 1; }
    File.WriteAllBytes(output, Png.Encode(art));
    return 0;
}
Console.Error.WriteLine("""
    usage: bambubar --probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]
           bambubar --probe-camera <host> <serial> <access-code> [seconds] [--jpeg|--rtsp] [--model <name>] [--insecure-tls] [--out <file.png>]
           bambubar --art <out.png> <width> <height>
    """);
return 2;
