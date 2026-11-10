using System.Runtime.InteropServices;
using BambuBar.Core.App;

namespace BambuBar;

public static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Command-line runs from a terminal: print there and exit without starting WPF.
        if (Probe.Parse(args) is { } probe)
        {
            AttachConsole(AttachParentProcess);
            return Probe.RunAsync(probe, Console.Out).GetAwaiter().GetResult();
        }
        if (Snapshot.Folder(args) is { } folder) return Snapshot.Run(folder);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
