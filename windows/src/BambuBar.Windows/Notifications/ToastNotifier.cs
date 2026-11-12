using System.Security;
using BambuBar.Core.App;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BambuBar.Notifications;

/// <summary>
/// Print notifications as Windows toasts (P3.7; the Mac's Notifier). Core decides when and what (started, paused,
/// finished, failed, canceled, a new serious HMS error) and names each one <c>&lt;kind&gt;.&lt;serial&gt;</c>; that id is the
/// toast's tag and the serial its group, so a newer toast of the same kind for the same printer replaces the older one
/// in Notification Center instead of piling up. A click opens <c>bambubar://printer/&lt;serial&gt;</c>, which Windows hands
/// to the running copy (or starts one): the Printer page of that printer, whether or not Bambu Bar was running.
/// </summary>
public sealed class ToastNotifier : INotifier
{
    private Windows.UI.Notifications.ToastNotifier? _system;

    public void RequestAuthorization()
    {
        // Windows doesn't ask: toasts are on unless the person turned them off (the Notifications page says so).
    }

    public void Post(string title, string body, string id)
    {
        var serial = SerialOf(id);
        var link = serial is null ? "bambubar://open" : $"bambubar://printer/{serial}";
        var xml =
            $"<toast launch=\"{Escape(link)}\" activationType=\"protocol\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            $"<text hint-maxLines=\"1\">{Escape(title)}</text><text>{Escape(body)}</text>" +
            "</binding></visual></toast>";
        try
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            var toast = new ToastNotification(document) { Tag = Clip(id), Group = Clip(serial ?? "bambubar") };
            WindowsNotifier()?.Show(toast);
        }
        catch (Exception)
        {
            // Turned off, the id not registered, or no notification platform (Windows N, Server Core): nothing to show.
        }
    }

    /// <summary>Whether Windows lets Bambu Bar's toasts through; null when it can't be read.</summary>
    public static bool? Enabled()
    {
        try
        {
            return ToastNotificationManager.CreateToastNotifier(AppIdentity.Id).Setting == NotificationSetting.Enabled;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The serial at the end of a Core notification id (<c>end.01P00A123456789</c>).</summary>
    internal static string? SerialOf(string id)
    {
        var dot = id.LastIndexOf('.');
        var serial = dot >= 0 ? id[(dot + 1)..] : "";
        return serial.Length > 0 && serial.All(char.IsAsciiLetterOrDigit) ? serial.ToUpperInvariant() : null;
    }

    /// <summary>Tags and groups are at most 64 characters.</summary>
    private static string Clip(string text) => text.Length <= 64 ? text : text[..64];

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    private Windows.UI.Notifications.ToastNotifier? WindowsNotifier()
    {
        try
        {
            return _system ??= ToastNotificationManager.CreateToastNotifier(AppIdentity.Id);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
