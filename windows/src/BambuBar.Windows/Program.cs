using System.Runtime.InteropServices;
using BambuBar.Core.App;

namespace BambuBar;

public static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Command-line runs from a terminal: print there and exit without starting WPF. "keychain" as the code means
        // the one saved in Credential Manager, as on the Mac.
        if (CameraProbe.Parse(args, SavedCode) is { } camera)
        {
            AttachConsole(AttachParentProcess);
            return CameraProbe.RunAsync(camera, Console.Out).GetAwaiter().GetResult();
        }
        if (Probe.Parse(args, SavedCode) is { } probe)
        {
            AttachConsole(AttachParentProcess);
            return Probe.RunAsync(probe, Console.Out).GetAwaiter().GetResult();
        }
        if (Snapshot.Folder(args) is { } folder) return Snapshot.Run(folder);
        if (A11yCheck.Folder(args) is { } report) return A11yCheck.Run(report);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static string? SavedCode(string serial) => new Platform.CredentialStore().AccessCode(serial.ToUpperInvariant());

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
