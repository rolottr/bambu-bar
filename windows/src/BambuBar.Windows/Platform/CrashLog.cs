using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace BambuBar.Platform;

/// <summary>
/// Writes what went wrong to <c>%LOCALAPPDATA%\Bambu Bar\Logs\crash-…txt</c> (the newest ten are kept), so a crash on
/// someone's PC comes back with its stack. While the app is quitting, an exception is logged and swallowed: the person
/// asked it to go, and a crash dialog on the way out helps nobody.
/// </summary>
public static class CrashLog
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bambu Bar", "Logs");

    /// <summary>Set when the app starts letting go of everything.</summary>
    public static bool Quitting { get; set; }

    public static void Install(Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("unhandled", e.ExceptionObject as Exception);
        app.DispatcherUnhandledException += OnDispatcher;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write(Quitting ? "while quitting (ignored)" : "UI thread", e.Exception);
        if (Quitting) e.Handled = true;
    }

    /// <summary>Runs one step of quitting; a failure is logged and the next step still runs.</summary>
    public static void Step(string name, Action step)
    {
        try { step(); }
        catch (Exception e) { Write($"quitting: {name}", e); }
    }

    public static void Write(string where, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var text = new StringBuilder()
                .AppendLine($"Bambu Bar {typeof(CrashLog).Assembly.GetName().Version} · {Environment.OSVersion.VersionString} · " +
                            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {where}")
                .AppendLine(exception?.ToString() ?? "(no exception)")
                .ToString();
            File.WriteAllText(Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"), text);
            foreach (var old in new DirectoryInfo(Folder).GetFiles("crash-*.txt").OrderByDescending(f => f.Name).Skip(10)) old.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write: nothing more to do.
        }
    }
}
