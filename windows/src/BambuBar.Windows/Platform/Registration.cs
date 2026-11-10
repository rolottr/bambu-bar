using Microsoft.Win32;

namespace BambuBar.Platform;

/// <summary>Per-user registry entries (no admin): opening at sign-in and the <c>bambubar://</c> links.</summary>
public static class Registration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Bambu Bar";

    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("No process path");

    /// <summary>Opens at sign-in, in the tray only (<c>--background</c>), like the Mac's login item.</summary>
    public static bool OpensAtSignIn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Exe}\" --background");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Turned off in Task Manager → Startup apps (or Settings → Apps → Startup), which overrides the Run key:
    /// the app then says so and links to <c>ms-settings:startupapps</c>.</summary>
    public static bool StartupDisabledByUser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            // 12 bytes; the first is 2 or 6 when enabled, 3 or 7 when the person disabled it.
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
    }

    /// <summary>Registers <c>bambubar://</c> for this user, pointing at this copy of the app (widgets' clicks, links).</summary>
    public static void RegisterUriScheme()
    {
        using var scheme = Registry.CurrentUser.CreateSubKey(@"Software\Classes\bambubar");
        scheme.SetValue("", "URL:Bambu Bar");
        scheme.SetValue("URL Protocol", "");
        using (var icon = scheme.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
        using var command = scheme.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{Exe}\" \"%1\"");
    }
}
