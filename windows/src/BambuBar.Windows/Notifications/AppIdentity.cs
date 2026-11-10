using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BambuBar.Notifications;

/// <summary>
/// Bambu Bar's AppUserModelID (P3.1): the name Windows files its toasts, notification settings and taskbar button
/// under. An unpackaged app has none until it registers one: per user, under
/// <c>HKCU\Software\Classes\AppUserModelId\&lt;id&gt;</c> with the name and icon toasts show, and the process says it
/// is that app. The same id as the Mac's bundle identifier; the installer's Start menu shortcut must carry it too
/// (System.AppUserModel.ID, P5.1) so Windows ties the shortcut, the taskbar button and the toasts together.
/// </summary>
public static partial class AppIdentity
{
    public const string Id = "dev.rolottr.BambuBar";
    public const string DisplayName = "Bambu Bar";

    /// <summary>The app icon toasts show: <c>AppIcon.png</c> next to the exe (copied there by the build).</summary>
    public static string? IconPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "AppIcon.png");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Registers the id for this user and takes it for this process. Failures are ignored: toasts then don't
    /// show, and nothing else depends on it.</summary>
    public static void Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Id}");
            key.SetValue("DisplayName", DisplayName);
            if (IconPath is { } icon) key.SetValue("IconUri", icon);
            else key.DeleteValue("IconUri", throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // A locked-down registry: no toasts.
        }
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Id);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Not on Windows.
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
