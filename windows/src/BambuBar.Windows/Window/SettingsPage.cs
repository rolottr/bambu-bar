using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BambuBar.Core;
using BambuBar.Core.Updates;
using BambuBar.Platform;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>The app's preferences (the Mac's SettingsPage): opening at sign-in, the language, the camera, and updates.</summary>
internal sealed class SettingsPage(WindowContext ctx) : Page(ctx)
{
    private readonly Region _startupNote = new();
    private readonly Region _languageNote = new();
    private readonly Region _cameraMore = new();
    private readonly Region _updates = new();
    private ToggleSwitch _signIn = null!;
    private TextBlock _signInError = null!;
    private Dropdown _language = null!;

    public override FrameworkElement Build()
    {
        var general = W.Group(Loc.T("General"), "", out var generalBody);
        generalBody.Children.Add(W.Switch(Loc.T("Open at sign-in"), OpensAtSignIn(), SetOpensAtSignIn, out _signIn));
        generalBody.Children.Add(_startupNote.Host);
        _signInError = W.Note("");
        _signInError.Foreground = Ctx.Ink(Palette.Failed);
        _signInError.Visibility = Visibility.Collapsed;
        generalBody.Children.Add(_signInError);
        generalBody.Children.Add(W.Note(Loc.T("At sign-in Bambu Bar starts in the notification area only. Open it from the Start menu or the tray icon to see this window.")));

        var language = W.Group(Loc.T("Language"), "", out var languageBody);
        _language = new Dropdown(Loc.T("Language"), Ctx.Theme, LanguageEntries);
        languageBody.Children.Add(W.Row(Loc.T("Language"), _language));
        languageBody.Children.Add(_languageNote.Host);

        var camera = W.Group(Loc.T("Camera"), Icons.Camera, out var cameraBody);
        cameraBody.Children.Add(W.Switch(Loc.T("Show the printer camera"), Store.CameraEnabled, on => { Store.CameraEnabled = on; Refresh(); }));
        cameraBody.Children.Add(W.Note(Loc.T("Turn the camera on for each printer too, on the Printers page. While it's off, the camera is hidden everywhere: the Printer page, the tray panel and the menus.")));
        cameraBody.Children.Add(_cameraMore.Host);

        return new Stack { Spacing = 22, Children = { W.PageHeader(Loc.T("Settings"), null), general, language, camera, _updates.Host } };
    }

    public override void Refresh()
    {
        var disabled = !Ctx.Services.Offscreen && SafeRead(() => Registration.StartupDisabledByUser);
        _startupNote.Show(disabled.ToString(), () => disabled
            ? W.InfoBar(Palette.Paused, Icons.Warning, Loc.T("Bambu Bar is turned off in Startup apps, so it won't open at sign-in."), Ctx,
                W.Button(Loc.T("Open Startup Apps"), () => W.Open("ms-settings:startupapps"), small: true))
            : null);

        var chosen = Ctx.Settings.Get<string>(WindowSettings.Language);
        var system = SystemLanguage();
        _language.Text = chosen is null ? Loc.F("System (%@)", LanguageName(system)) : LanguageName(chosen);
        var target = chosen ?? system;
        _languageNote.Show($"{target}|{Loc.Language}", () =>
        {
            if (target == Loc.Language)
                return W.Note(Loc.T("Bambu Bar follows the language of Windows unless you pick another one here."));
            var row = Ui.Columns("*", "Auto");
            var note = W.Note(Loc.F("Bambu Bar switches to %@ when it restarts.", LanguageName(target)));
            note.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(Ui.At(note, 0));
            if (Ctx.Services.Restart is { } restart)
            {
                var button = W.Button(Loc.T("Restart Now"), restart, small: true);
                button.Margin = new Thickness(12, 0, 0, 0);
                row.Children.Add(Ui.At(button, 1));
            }
            return row;
        });

        var cameraOn = Store.CameraEnabled;
        _cameraMore.Show(cameraOn.ToString(), () => cameraOn
            ? new Stack
            {
                Spacing = 14,
                Children =
                {
                    W.Switch(Loc.T("Keep the camera connected during prints"), Store.CameraStaysOnWhilePrinting, on => Store.CameraStaysOnWhilePrinting = on),
                    W.Note(Loc.T("The picture shows at once when you open it, but the stream runs for the whole print and uses one of the printer's few camera connections, so Bambu Handy or Bambu Studio may not get the camera meanwhile.")),
                },
            }
            : null);

        var access = Ctx.Services.License?.HasAccess ?? false;
        _updates.Show(access.ToString(), () => Updates(access));
    }

    // MARK: Open at sign-in

    private bool OpensAtSignIn() => !Ctx.Services.Offscreen && SafeRead(() => Registration.OpensAtSignIn);

    private static bool SafeRead(Func<bool> read)
    {
        try { return read(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SetOpensAtSignIn(bool on)
    {
        if (Ctx.Services.Offscreen) return;
        try
        {
            Registration.OpensAtSignIn = on;
            _signInError.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException or InvalidOperationException)
        {
            _signInError.Text = e.Message;
            _signInError.Visibility = Visibility.Visible;
            _signIn.IsChecked = OpensAtSignIn();
        }
        Refresh();
    }

    // MARK: Language

    /// <summary>The catalog language Windows asks for, as the app picked it at launch without an override.</summary>
    private static string SystemLanguage() => LanguageMatch.Current(CultureInfo.CurrentUICulture);

    /// <summary>"Deutsch", "Português (Brasil)", "中文（简体）": each language by its own name, capitalised.</summary>
    public static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            var name = culture.NativeName;
            return name.Length == 0 ? code : char.ToUpper(name[0], culture) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private IReadOnlyList<MenuEntry> LanguageEntries()
    {
        var chosen = Ctx.Settings.Get<string>(WindowSettings.Language);
        var entries = new List<MenuEntry>
        {
            new(Loc.F("System (%@)", LanguageName(SystemLanguage())), () => Choose(null), chosen is null),
            MenuEntry.Separator,
        };
        entries.AddRange(LanguageMatch.Available
            .OrderBy(LanguageName, StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase))
            .Select(code => new MenuEntry(LanguageName(code), () => Choose(code), code == chosen)));
        return entries;
    }

    private void Choose(string? code)
    {
        Ctx.Settings.Set(WindowSettings.Language, code);
        Refresh();
    }

    // MARK: Updates

    /// <summary>Automatic checks and downloads, which need the supporter license (the Mac's UpdatesGroup). The Windows
    /// updater itself comes with P5.4: until then the switches keep their settings and "Check Now" waits.</summary>
    private FrameworkElement Updates(bool access)
    {
        var group = W.Group(Loc.T("Updates"), Icons.Download, out var body);
        if (!access)
        {
            body.Children.Add(W.Note(Loc.T("Activate your website supporter license to check GitHub for updates and enable automatic updates."), ThemeKeys.TextSecondary, WRamp.Body));
            body.Children.Add(W.Button(Loc.T("Activate Supporter License…"), () => Ctx.Show(AppPage.About), small: true));
        }
        var checks = Ctx.Settings.Get(UpdateChecker.Keys.AutomaticChecks, true);
        ToggleSwitch? downloads = null;
        body.Children.Add(W.Switch(Loc.T("Check for updates automatically"), access && checks, on =>
        {
            Ctx.Settings.Set(UpdateChecker.Keys.AutomaticChecks, on);
            if (downloads is not null)
            {
                downloads.IsEnabled = on;
                downloads.IsChecked = on && Ctx.Settings.Get(UpdateChecker.Keys.AutomaticDownloads, false);
            }
        }, out var checksSwitch));
        checksSwitch.IsEnabled = access;
        body.Children.Add(W.Switch(Loc.T("Download and install updates automatically"), access && checks && Ctx.Settings.Get(UpdateChecker.Keys.AutomaticDownloads, false),
            on => Ctx.Settings.Set(UpdateChecker.Keys.AutomaticDownloads, on), out downloads));
        downloads.IsEnabled = access && checks;
        body.Children.Add(W.Divider());

        var status = Ui.Columns("Auto", "*", "Auto");
        var icon = Icons.Glyph(Icons.Clock, 14);
        icon.Margin = new Thickness(0, 0, 10, 0);
        status.Children.Add(Ui.At(icon, 0));
        var last = Ctx.Settings.Get<DateTimeOffset?>(UpdateChecker.Keys.LastCheck);
        var titles = new Stack
        {
            Spacing = 2,
            Children =
            {
                Ui.Text(Loc.F("Version %@", AppVersion.Marketing), WRamp.Body, FontWeights.Medium),
                Ui.Text(last is { } when ? Loc.F("Version %@ · last checked %@", AppVersion.Marketing, PanelText.Ago(when)) : Loc.T("Not checked yet"), WRamp.Note, null, ThemeKeys.TextSecondary),
            },
        };
        status.Children.Add(Ui.At(titles, 1));
        var check = W.Button(Loc.T("Check Now"), null, small: true);
        check.IsEnabled = false;
        status.Children.Add(Ui.At(check, 2));
        body.Children.Add(status);
        body.Children.Add(W.Note(Loc.T("When enabled, automatic checks run once a day. Updates show what’s new and install in place after their signature is verified.")));
        return group;
    }
}

/// <summary>The catalog's languages and Windows' choice among them. Loaded once, like the app's own <see cref="Strings"/>.</summary>
internal static class LanguageMatch
{
    private static readonly Lazy<Strings> Catalog = new(() => Strings.Load());

    public static IReadOnlyList<string> Available => Catalog.Value.Languages;

    public static string Current(CultureInfo culture) => Catalog.Value.Match(culture);
}
