using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.Licensing;
using BambuBar.Core.Model;
using BambuBar.Theme;
using BambuBar.Tray;
using Microsoft.Win32;

namespace BambuBar.AppWindow;

/// <summary>About the app (the Mac's AboutPage): version details for bug reports, links, the supporter license, how it
/// works, privacy and credits.</summary>
internal sealed class AboutPage(WindowContext ctx) : Page(ctx)
{
    private static readonly Uri NewIssue = new("https://github.com/rolottr/bambu-bar/issues/new");
    private readonly Region _licenseRow = new();
    private readonly Region _license = new();
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ChromeButton _copy = null!;
    private TextField _key = null!;
    private ChromeButton? _activate;
    private bool _revealed;
    private bool _keyCopied;

    public override FrameworkElement Build()
    {
        _key = new TextField(Loc.T("License key"), secret: true);
        _key.OnChange(() => { if (_activate is not null) _activate.IsEnabled = CanActivate; }).OnKeys(Activate);
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            SetCopyLabel(false);
        };

        var hero = Hero();
        var copy = Section(Loc.T("This copy"), out var copyBody);
        copyBody.Children.Add(W.DetailRow(Loc.T("Version"), AppVersion.Marketing));
        copyBody.Children.Add(W.DetailRow(Loc.T("System"), SystemInfo.Description));
        copyBody.Children.Add(_licenseRow.Host);
        var reports = Ui.Columns("*", "Auto");
        reports.Children.Add(Ui.At(Ui.Text(Loc.T("For bug reports"), WRamp.Note, null, ThemeKeys.TextSecondary), 0));
        _copy = W.Button(Loc.T("Copy Version Info"), CopyVersion, small: true);
        reports.Children.Add(Ui.At(_copy, 1));
        copyBody.Children.Add(reports);

        var links = Section(Loc.T("Links"), out var linksBody);
        linksBody.Spacing = 2;
        void Link(string title, string glyph, Uri url)
        {
            var link = W.Link(title, () => W.Open(url), glyph, WRamp.Detail);
            link.Margin = new Thickness(-6, 0, 0, 0);
            Ui.Tip(link, url.AbsoluteUri);
            linksBody.Children.Add(link);
        }
        Link(Loc.T("bambubar.app"), "", Website.Home);
        Link(Loc.T("Release Notes"), "", Website.Changelog);
        Link(Loc.T("Manage Purchase"), "", PolarConfiguration.PortalUrl);
        Link(Loc.T("Report an Issue"), "", NewIssue);

        var top = Ui.Columns("*", "20", "*");
        top.Children.Add(Ui.At(copy, 0));
        top.Children.Add(Ui.At(links, 2));

        var license = Section(Loc.T("Your license"), out var licenseBody);
        licenseBody.Children.Add(_license.Host);

        var how = Section(Loc.T("How it works"), out var howBody);
        howBody.Children.Add(Paragraph(Loc.T("Bambu Bar connects straight to your printer on the local network, the same way Bambu Studio does in LAN mode: an encrypted MQTT connection on port 8883, signed in with the printer's access code. The printer's certificate is checked against Bambu Lab's own certificate authority.")));
        howBody.Children.Add(Paragraph(Loc.T("The printer streams its status about once a second. Bambu Bar shows it in the tray, keeps a short history of your prints, and writes a snapshot for its desktop widgets on this PC.")));

        var privacy = Section(Loc.T("Privacy"), out var privacyBody);
        privacyBody.Children.Add(Paragraph(Loc.T("Printer data stays on your local network. No app analytics. Printer access codes are stored in Windows Credential Manager.")));
        privacyBody.Children.Add(Paragraph(Loc.T("Supporter licenses are validated online, with the key and installation ID stored in Credential Manager. In-app update checks use the latest GitHub release feed and require an active website supporter license. Update checks send no printer data, and updates must carry the developer's signature. Manage automatic updates in Settings → Updates.")));

        var credits = Section(Loc.T("Credits"), out var creditsBody);
        creditsBody.Children.Add(Paragraph(Loc.T("Protocol details and the HMS error texts come from the open-source ha-bambulab project (MIT), with thanks to Bambuddy and bambu-cli for documenting the newer printers. What each model can do (chamber sensor, fans, heated chamber, nozzles, build volume) follows the printer profiles Bambu Lab publishes with Bambu Studio.")));
        creditsBody.Children.Add(Paragraph(Loc.T("The printer models were built for this project in Blender. They are visual approximations, not manufacturer CAD.")));
        creditsBody.Children.Add(Paragraph(Loc.T("Desktop widgets are drawn by Rainmeter (GPL-2.0), shipped unmodified with its license and source.")));
        creditsBody.Children.Add(Paragraph(Loc.T("Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab.")));

        return new Stack { Spacing = 22, Children = { hero, top, license, how, privacy, credits } };
    }

    public override void Refresh() => Fill();

    private LicenseStore? License => Ctx.Services.License;

    private void Fill()
    {
        var license = License;
        var access = license?.HasAccess ?? false;
        _licenseRow.Show(access.ToString(), () =>
        {
            if (!access) return W.DetailRow(Loc.T("License"), Loc.T("Free · Community edition"));
            var star = Icons.Glyph("", 14, null, Ctx.Ink(Palette.Accent));
            var text = Ui.Paragraph(Loc.T("Hero Supporter · Thanks for supporting Bambu Bar"), WRamp.Detail, ThemeKeys.TextPrimary, FontWeights.SemiBold);
            text.Foreground = Ctx.Ink(Palette.Accent);
            text.Margin = new Thickness(8, 0, 0, 0);
            var row = Ui.Columns("Auto", "*");
            row.Children.Add(Ui.At(star, 0));
            row.Children.Add(Ui.At(text, 1));
            return row;
        });
        var key = string.Join("|", access, license?.IsInitializing, license?.IsBusy, license?.Message, license?.Record?.ActivationId,
            _revealed, _keyCopied);
        _license.Show(key, LicenseForm);
    }

    // MARK: Header

    /// <summary>The app's mark, name, maker, version and what it is.</summary>
    private Grid Hero()
    {
        var glyph = new Image
        {
            Source = TrayGlyph.Render(new TrayGlyph.Look(JobState.Printing, 0.6, false, false), 64, lightTaskbar: false),
            Width = 56, Height = 56,
        };
        RenderOptions.SetBitmapScalingMode(glyph, BitmapScalingMode.HighQuality);
        var icon = new Border
        {
            Width = 96, Height = 96, CornerRadius = new CornerRadius(22), Child = glyph,
            Background = new LinearGradientBrush(Palette.Accent, Palette.AccentDeep, 90),
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = 0.25 },
        };
        var titles = new Stack
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text("Bambu Bar", 30, FontWeights.Bold, display: true),
                Ui.Text(Loc.T("NINA LABS, LLC"), WRamp.Detail, null, ThemeKeys.TextSecondary),
                Ui.Text(Loc.F("Version %@", AppVersion.Marketing), WRamp.Body, null, ThemeKeys.TextSecondary),
                Ui.Text(Loc.T("Your Bambu Lab printer, one click away in the tray."), WRamp.Body, null, ThemeKeys.TextTertiary),
            },
        };
        AutomationProperties.SetHeadingLevel(titles.Children[0], AutomationHeadingLevel.Level1);
        var grid = Ui.Columns("Auto", "20", "*");
        grid.Margin = new Thickness(0, 0, 0, 4);
        grid.Children.Add(Ui.At(icon, 0));
        grid.Children.Add(Ui.At(titles, 2));
        return grid;
    }

    private static Stack Section(string title, out Stack body)
    {
        body = new Stack { Spacing = 10 };
        return new Stack { Spacing = 8, Children = { Ui.SectionLabel(title, null), W.Card(body, 16) } };
    }

    private static TextBlock Paragraph(string text) => W.Note(text, ThemeKeys.TextSecondary, WRamp.Detail);

    private void CopyVersion()
    {
        try { Clipboard.SetText($"Bambu Bar {AppVersion.Marketing} · {SystemInfo.Description}"); }
        catch (ExternalException) { return; }
        SetCopyLabel(true);
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void SetCopyLabel(bool copied)
    {
        var text = copied ? Loc.T("Copied") : Loc.T("Copy Version Info");
        ((TextBlock)_copy.Content).Text = text;
        AutomationProperties.SetName(_copy, text);
    }

    // MARK: License (the Mac's LicenseView)

    private UIElement LicenseForm()
    {
        var license = License;
        if (license is { IsInitializing: true })
        {
            var loading = new Stack { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new Spinner(16, Ctx.Animate), Ui.Text(Loc.T("Opening Bambu Bar…"), WRamp.Body) } };
            return loading;
        }
        var access = license?.HasAccess ?? false;
        var stack = new Stack { Spacing = 14 };
        var title = Ui.Paragraph(access ? Loc.T("Hero Supporter · Thank you for your support!") : Loc.T("Support Bambu Bar"), 20, ThemeKeys.TextPrimary, FontWeights.SemiBold);
        var titleIcon = Icons.Glyph(access ? "" : "", 18, null, Ctx.Ink(Palette.Accent));
        titleIcon.Margin = new Thickness(0, 2, 10, 0);
        titleIcon.VerticalAlignment = VerticalAlignment.Top;
        var titleRow = Ui.Columns("Auto", "*");
        titleRow.Children.Add(Ui.At(titleIcon, 0));
        titleRow.Children.Add(Ui.At(title, 1));
        stack.Children.Add(titleRow);
        stack.Children.Add(W.Note(access
            ? Loc.T("Your supporter license enables automatic updates on this PC.")
            : Loc.T("Bambu Bar is free to use. Activate a supporter license to enable automatic updates and receive the Hero Supporter badge."), ThemeKeys.TextPrimary, WRamp.Body));
        stack.Children.Add(W.Note(Loc.T("Manage your active installations in your account. Deactivate an old installation to make room for another."), ThemeKeys.TextSecondary, WRamp.Detail));

        var busy = license?.IsBusy ?? false;
        if (license?.Record is { } saved)
        {
            stack.Children.Add(SavedKey(saved.Key));
            stack.Children.Add(W.Note(Loc.F("Installation: Windows · %@", saved.InstallationId.Length > 8 ? saved.InstallationId[..8] : saved.InstallationId)));
            var check = W.Button(Loc.T("Check License"), () => _ = license.RefreshAsync(), small: true);
            var deactivate = W.Button(Loc.T("Deactivate This Installation"), Deactivate, small: true);
            deactivate.Look = ButtonLook.Tinted(Palette.Failed);
            ((TextBlock)deactivate.Content).Foreground = Ctx.Ink(Palette.Failed);
            check.IsEnabled = deactivate.IsEnabled = !busy;
            stack.Children.Add(new Stack { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, deactivate } });
        }
        else
        {
            if (_key.Parent is Panel old) old.Children.Remove(_key);
            var activate = W.Button(Loc.T("Activate"), Activate, accent: true);
            _activate = activate;
            activate.IsEnabled = CanActivate;
            var row = Ui.Columns("*", "8", "Auto");
            row.Children.Add(Ui.At(_key, 0));
            row.Children.Add(Ui.At(activate, 2));
            stack.Children.Add(row);
        }
        if (busy) stack.Children.Add(new Spinner(16, Ctx.Animate) { HorizontalAlignment = HorizontalAlignment.Left });
        if (license?.Message is { } message)
        {
            var text = new TextBox
            {
                Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), FontSize = WRamp.Detail,
            };
            text.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextSecondary);
            text.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontText);
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
            stack.Children.Add(text);
        }
        var links = Ui.Columns("Auto", "*", "Auto");
        var support = W.Link(Loc.T("Support for $9.99"), () => W.Open(PolarConfiguration.SupportUrl(PolarConfiguration.SupportPlacement.License)));
        support.Margin = new Thickness(-6, 0, 0, 0);
        links.Children.Add(Ui.At(support, 0));
        links.Children.Add(Ui.At(W.Link(Loc.T("Manage Purchase"), () => W.Open(PolarConfiguration.PortalUrl)), 2));
        stack.Children.Add(links);
        stack.Children.Add(W.Note(Loc.T("NINA LABS, LLC")));
        stack.Children.Add(W.Note(Loc.T("Supporter activation needs an internet connection. Supporter benefits stay active for up to seven days offline after validation. Printer monitoring always remains available.")));
        return stack;
    }

    /// <summary>The saved key, masked until shown, with Copy.</summary>
    private Stack SavedKey(string key)
    {
        var header = Ui.Columns("*", "Auto", "Auto");
        header.Children.Add(Ui.At(Ui.Text(Loc.T("License key"), WRamp.Note, null, ThemeKeys.TextSecondary), 0));
        var reveal = W.Button(_revealed ? Loc.T("Hide License Key") : Loc.T("Show License Key"), () =>
        {
            _revealed = !_revealed;
            Fill();
        }, small: true);
        var copy = W.Button(_keyCopied ? Loc.T("Copied") : Loc.T("Copy"), () =>
        {
            try { Clipboard.SetText(key); _keyCopied = true; }
            catch (ExternalException) { _keyCopied = false; }
            Fill();
        }, small: true);
        copy.Margin = new Thickness(8, 0, 0, 0);
        header.Children.Add(Ui.At(reveal, 1));
        header.Children.Add(Ui.At(copy, 2));
        var shown = new TextBox
        {
            Text = _revealed ? key : $"•••• •••• {(key.Length > 4 ? key[^4..] : key)}", IsReadOnly = true, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0), FontSize = WRamp.Detail, TextWrapping = TextWrapping.Wrap,
        };
        shown.SetResourceReference(Control.FontFamilyProperty, ThemeKeys.FontMono);
        shown.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextPrimary);
        AutomationProperties.SetName(shown, Loc.T("License key"));
        return new Stack { Spacing = 8, Children = { header, shown } };
    }

    private async void Activate()
    {
        if (License is not { IsBusy: false } license || _key.Text.Trim().Length == 0) return;
        await license.ActivateAsync(_key.Text);
        if (license.HasAccess) _key.Text = "";
        Fill();
    }

    private bool CanActivate => License is { IsBusy: false } && _key.Text.Trim().Length > 0;

    private void Deactivate()
    {
        if (License is not { } license) return;
        if (!Dialogs.Confirm(_license.Host, Ctx, Loc.T("Deactivate this installation?"),
            Loc.T("This frees an activation for another installation and turns off automatic updates. You can keep using Bambu Bar."),
            Loc.T("Deactivate"), Loc.T("Cancel"))) return;
        _revealed = false;
        _keyCopied = false;
        _ = license.DeactivateAsync();
    }

    public override void Dispose()
    {
        _copiedTimer.Stop();
        if (_key.Parent is Panel old) old.Children.Remove(_key);
    }
}

/// <summary>"Windows 11 24H2 (26100.2033) · ARM64", for bug reports (the Mac's AppInfo.system and architecture).</summary>
internal static class SystemInfo
{
    public static string Description { get; } = Describe();

    private static string Describe()
    {
        var version = Environment.OSVersion.Version;
        var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        string? display = null;
        int? revision = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string;
            revision = key?.GetValue("UBR") as int?;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        var build = revision is { } r ? $"{version.Build}.{r}" : $"{version.Build}";
        var os = string.IsNullOrEmpty(display) ? $"{name} ({build})" : $"{name} {display} ({build})";
        var arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "ARM64", Architecture.X64 => "x64", var a => a.ToString() };
        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
            arch += $" ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} app)";
        return $"{os} · {arch}";
    }
}
