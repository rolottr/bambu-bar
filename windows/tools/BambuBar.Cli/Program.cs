using BambuBar.Core.App;

// bambubar --probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]
// bambubar --probe-camera <host> <serial> <access-code> [seconds] [--jpeg|--rtsp] [--model <name>] [--insecure-tls] [--out <file.png>]
if (CameraProbe.Parse(args) is { } camera) return await CameraProbe.RunAsync(camera, Console.Out);
if (Probe.Parse(args) is { } probe) return await Probe.RunAsync(probe, Console.Out);
Console.Error.WriteLine("""
    usage: bambubar --probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]
           bambubar --probe-camera <host> <serial> <access-code> [seconds] [--jpeg|--rtsp] [--model <name>] [--insecure-tls] [--out <file.png>]
    """);
return 2;
