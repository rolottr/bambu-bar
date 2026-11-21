using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Updates;
using BambuBar.Platform;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>The app's preferences (the Mac's SettingsPage): opening at sign-in, the language, the appearance, the camera,
/// the print controls, and updates.</summary>
internal sealed class SettingsPage(WindowContext ctx) : Page(ctx)
{
    private readonly Region _startupNote = new();
    private readonly Region _languageNote = new();
    private readonly Region _cameraMore = new();
    private readonly Region _updates = new();
    private ToggleSwitch _signIn = null!;
    private TextBlock _signInError = null!;
    private Dropdown _language = null!;
    private Dropdown _mode = null!;
    private Dropdown _accent = null!;
    private readonly Dictionary<AppearanceStyle, ChromeToggle> _styles = [];
    private AccentChoice? _accentShown;

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

        var appearance = AppearanceGroup();

        var camera = W.Group(Loc.T("Camera"), Icons.Camera, out var cameraBody);
        cameraBody.Children.Add(W.Switch(Loc.T("Show the printer camera"), Store.CameraEnabled, on => { Store.CameraEnabled = on; Refresh(); }));
        cameraBody.Children.Add(W.Note(Loc.T("Turn the camera on for each printer too, at the bottom of its Printer page. While it's off, the camera is hidden everywhere: the Printer page, the tray panel and the menus.")));
        cameraBody.Children.Add(_cameraMore.Host);

        var controls = W.Group(Loc.T("Controls"), "", out var controlsBody);
        controlsBody.Children.Add(W.Switch(Loc.T("Ask before stopping a print"), Store.ConfirmStop, on => Store.ConfirmStop = on));
        controlsBody.Children.Add(W.Note(Loc.T("Pause, stop and speed changes need **LAN Only** mode with **Developer Mode** on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.")));

        return new Stack { Spacing = 22, Children = { W.PageHeader(Loc.T("Settings"), null), general, language, appearance, camera, controls, _updates.Host } };
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

        _mode.Text = AppTheme.Mode.Title();
        foreach (var (style, toggle) in _styles) toggle.IsChecked = style == AppTheme.Style;
        if (_accentShown != AppTheme.AccentChoice)
        {
            _accentShown = AppTheme.AccentChoice;
            _accent.Text = _accentShown.Value.Title();
            _accent.Icon = Swatch(_accentShown.Value);
        }

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
        var u = Ctx.Services.Updates;
        _updates.Show($"{access}|{u?.State}|{u?.LastChecked}|{u?.Release?.DisplayVersion}|{u?.Error}", () => Updates(access));
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

    // MARK: Appearance

    /// <summary>Mode, style and accent (the Mac's AppearanceGroup). A change re-themes the panel and this window at once
    /// (AppTheme.Changed), this page included.</summary>
    private Stack AppearanceGroup()
    {
        var group = W.Group(Loc.T("Appearance"), Icons.ColorPalette, out var body);
        _mode = new Dropdown(Loc.T("Mode"), Ctx.Theme, () => Enum.GetValues<AppearanceMode>()
            .Select(mode => new MenuEntry(mode.Title(), () => { AppTheme.Mode = mode; Refresh(); }, mode == AppTheme.Mode)).ToList());
        body.Children.Add(W.Row(Loc.T("Mode"), _mode));

        body.Children.Add(W.Row(Loc.T("Style"), StylePicker()));
        body.Children.Add(W.Note(Loc.T("Windows looks like the apps that come with Windows 11: Mica, flat cards and the standard controls. Bambu Glass keeps the soft gradients of the Mac version.")));
        body.Children.Add(W.Divider());

        _accent = new Dropdown(Loc.T("Accent colour"), Ctx.Theme, () => Enum.GetValues<AccentChoice>()
            .Select(accent => new MenuEntry(accent.Title(), () => { AppTheme.AccentChoice = accent; Refresh(); }, accent == AppTheme.AccentChoice,
                _ => Swatch(accent))).ToList());
        body.Children.Add(W.Row(Loc.T("Accent colour"), _accent));
        body.Children.Add(W.Note(Loc.T("The accent colour marks buttons, switches, links and selections. Printer states keep their own colours.")));
        return group;
    }

    /// <summary>Windows or Bambu Glass: a segmented control, as the Widgets page picks a size.</summary>
    private AccessibleGroup StylePicker()
    {
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        _styles.Clear();
        foreach (var style in Enum.GetValues<AppearanceStyle>())
        {
            var toggle = new ChromeToggle
            {
                Content = Ui.Text(style.Title(), WRamp.Body), Height = 30, Padding = new Thickness(12, 0, 12, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center, CheckedLook = ButtonLook.Standard, CornerRadius = Metrics.Control,
            };
            AutomationProperties.SetName(toggle, style.Title());
            toggle.Click += (_, _) =>
            {
                // A click on the current one leaves it on.
                toggle.IsChecked = true;
                AppTheme.Style = style;
                Refresh();
            };
            _styles[style] = toggle;
            row.Children.Add(toggle);
        }
        var box = new AccessibleGroup { Child = row, CornerRadius = Metrics.Control, Padding = new Thickness(2), BorderThickness = new Thickness(1), Width = 240 };
        box.SetResourceReference(Border.BackgroundProperty, WindowKeys.OffFill);
        box.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.ControlStroke);
        AutomationProperties.SetName(box, Loc.T("Style"));
        return box;
    }

    /// <summary>A dot of the accent as its buttons fill in this theme (decoration: the name says it).</summary>
    private Ellipse Swatch(AccentChoice accent)
    {
        var dot = new Ellipse
        {
            Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1,
            Fill = Palette.Brush(Palette.Rgb(AppTheme.Shades(accent).Fill(Ctx.Dark))),
        };
        dot.SetResourceReference(Shape.StrokeProperty, ThemeKeys.ControlStroke);
        return dot;
    }

    // MARK: Updates

    /// <summary>What the last check found: a newer release, an error, up to date, or when it ran.</summary>
    private string UpdateLine(DateTimeOffset? last)
    {
        var updates = Ctx.Services.Updates;
        return updates?.State switch
        {
            UpdateState.Checking => Loc.T("Checking…"),
            UpdateState.Downloading => Loc.T("Downloading…"),
            UpdateState.Available or UpdateState.Ready when updates.Available is { } release =>
                Loc.F("Bambu Bar %@ is available", release.DisplayVersion ?? ""),
            UpdateState.Failed => updates.Error ?? Loc.T("Couldn’t check for updates"),
            UpdateState.UpToDate => Loc.T("Bambu Bar is up to date"),
            _ => last is { } when ? Loc.F("Version %@ · last checked %@", AppVersion.Marketing, PanelText.Ago(when)) : Loc.T("Not checked yet"),
        };
    }

    /// <summary>Automatic checks and downloads, which need the supporter license (the Mac's UpdatesGroup), the last
    /// check's result, Check Now, and Update… when a newer release is waiting.</summary>
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
            if (Ctx.Services.Updates is { } checker) checker.ChecksAutomatically = on;
            else Ctx.Settings.Set(UpdateChecker.Keys.AutomaticChecks, on);
            if (downloads is not null)
            {
                downloads.IsEnabled = on;
                downloads.IsChecked = on && Ctx.Settings.Get(UpdateChecker.Keys.AutomaticDownloads, false);
            }
        }, out var checksSwitch));
        checksSwitch.IsEnabled = access;
        body.Children.Add(W.Switch(Loc.T("Download and install updates automatically"), access && checks && Ctx.Settings.Get(UpdateChecker.Keys.AutomaticDownloads, false),
            on =>
            {
                if (Ctx.Services.Updates is { } checker) checker.DownloadsAutomatically = on;
                else Ctx.Settings.Set(UpdateChecker.Keys.AutomaticDownloads, on);
            }, out downloads));
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
                Ui.Text(UpdateLine(last), WRamp.Note, null, ThemeKeys.TextSecondary),
            },
        };
        status.Children.Add(Ui.At(titles, 1));
        var updates = Ctx.Services.Updates;
        UIElement action;
        if (updates?.Available is { } release && updates.State is UpdateState.Available or UpdateState.Ready)
            action = W.Button(Loc.T("Update…"), Ctx.Services.InstallUpdate, accent: true, small: true);
        else
        {
            var check = W.Button(updates?.State == UpdateState.Checking ? Loc.T("Checking…") : Loc.T("Check Now"),
                () => _ = updates?.CheckAsync(), small: true);
            check.IsEnabled = access && updates is { CanCheckForUpdates: true } && updates.State is not (UpdateState.Checking or UpdateState.Downloading);
            action = check;
        }
        status.Children.Add(Ui.At(action, 2));
        body.Children.Add(status);
        body.Children.Add(W.Note(Loc.T("When enabled, automatic checks run once a day. Updates show what’s new and install in place after their signature is verified.")));
        return group;
    }
}

/// <summary>The catalog's languages and Windows' choice among them. Loaded once, like the app's own <see cref="Strings"/>.</summary>
internal static class LanguageMatch
{
    /// <summary>The app's own catalog when it's loaded (its language list is the whole catalog's); otherwise read once.</summary>
    private static readonly Lazy<Strings> Catalog = new(() => Strings.Current ?? Strings.Load());

    public static IReadOnlyList<string> Available => Catalog.Value.Languages;

    public static string Current(CultureInfo culture) => Catalog.Value.Match(culture);
}
