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
        // Ended outright: a thread the checks leave behind (a printer's connection, UI Automation's) mustn't keep
        // the process, and with it CI, waiting.
        if (A11yCheck.Folder(args) is { } report) Environment.Exit(A11yCheck.Run(report));
        // WinForms (the tray icon and its menu) would show its own "Unhandled exception… Continue" dialog: let its
        // exceptions through to the app's handlers and the crash log instead.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
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
