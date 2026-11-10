using BambuBar.Core.App;

// bambubar --probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]
if (Probe.Parse(args) is { } probe) return await Probe.RunAsync(probe, Console.Out);
Console.Error.WriteLine("usage: bambubar --probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]");
return 2;
