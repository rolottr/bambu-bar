using System.Windows;
using BambuBar.Core;
using BambuBar.Theme;
using Microsoft.Win32;

namespace BambuBar.AppWindow;

/// <summary>Which print events post a notification, and a way to Windows Settings when Windows has them turned off
/// (the Mac's NotificationsPage).</summary>
internal sealed class NotificationsPage(WindowContext ctx) : Page(ctx)
{
    private readonly Region _banner = new();
    private DateTime _checkedAt = DateTime.MinValue;

    public override FrameworkElement Build()
    {
        var group = W.Group(Loc.T("Notify me when"), "", out var body);
        body.Children.Add(W.Switch(Loc.T("Print finished"), Store.NotifyFinished, on => Store.NotifyFinished = on));
        body.Children.Add(W.Switch(Loc.T("Print failed, canceled or an error"), Store.NotifyFailed, on => Store.NotifyFailed = on));
        body.Children.Add(W.Switch(Loc.T("Print paused (filament runout, user)"), Store.NotifyPaused, on => Store.NotifyPaused = on));
        body.Children.Add(W.Switch(Loc.T("Print started"), Store.NotifyStarted, on => Store.NotifyStarted = on));
        return new Stack
        {
            Spacing = 22,
            Children =
            {
                W.PageHeader(Loc.T("Notifications"), Loc.T("Bambu Bar tells you when a print needs you, even while its window is closed.")),
                _banner.Host, group,
            },
        };
    }

    /// <summary>Checks Windows' switches again now and then, so the banner goes once they're back on.</summary>
    public override void Refresh()
    {
        if (DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(2)) return;
        _checkedAt = DateTime.UtcNow;
        var off = !Ctx.Services.Offscreen && TurnedOff(Ctx.Services.ToastsEnabled);
        _banner.Show(off.ToString(), () => off
            ? W.InfoBar(Palette.Paused, "", Loc.T("Notifications for Bambu Bar are turned off in Windows Settings."), Ctx,
                W.Button(Loc.T("Open Settings"), () => W.Open("ms-settings:notifications"), small: true))
            : null);
    }

    /// <summary>Notifications are off for every app (Settings → System → Notifications), or for Bambu Bar's entry
    /// there. Asks Windows for Bambu Bar's AppUserModelID (<paramref name="toasts"/>, the app's toast notifier); where
    /// that can't answer, reads what the Settings page writes in the registry.</summary>
    internal static bool TurnedOff(Func<bool?>? toasts)
    {
        if (toasts?.Invoke() is { } allowed) return !allowed;
        try
        {
            using (var push = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
            {
                if (push?.GetValue("ToastEnabled") is int enabled && enabled == 0) return true;
            }
            using var app = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{Notifications.AppIdentity.Id}");
            return app?.GetValue("Enabled") is int on && on == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }
}
