using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Licensing;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>A newer release, one line above the footer (the Mac's UpdateRow). Shown when the app sets
/// <see cref="PanelView.UpdateOffer"/>; the Windows updater's UI (P5.4) doesn't exist yet.</summary>
internal sealed class UpdateSection : PanelSection
{
    private readonly Border _root;
    private readonly TextBlock _text = Ui.Text("", Ramp.Body, FontWeights.Medium);

    public UpdateSection(PanelContext ctx) : base(ctx)
    {
        var icon = Icons.Glyph(Icons.Download, 14, null, Palette.Brush(Palette.Accent));
        icon.Margin = new Thickness(0, 0, 8, 0);
        var later = new ChromeButton { Padding = new Thickness(8, 3, 8, 4), Content = Ui.Text(Loc.T("Later"), Ramp.Footnote, null, ThemeKeys.TextSecondary) };
        AutomationProperties.SetName(later, Loc.T("Later"));
        later.Click += (_, _) => Offer?.Later();
        var update = new ChromeButton { Look = ButtonLook.Standard, Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(4, 0, 0, 0), Content = Ui.Text(Loc.T("Update…"), Ramp.Footnote, FontWeights.Medium) };
        AutomationProperties.SetName(update, Loc.T("Update…"));
        update.Click += (_, _) => Offer?.Install();
        var grid = Ui.Columns("Auto", "*", "Auto", "Auto");
        grid.Children.Add(Ui.At(icon, 0));
        grid.Children.Add(Ui.At(_text, 1));
        grid.Children.Add(Ui.At(later, 2));
        grid.Children.Add(Ui.At(update, 3));
        _root = Ui.Card(grid, Metrics.Tile, new Thickness(10, 7, 8, 7));
    }

    public UpdateOffer? Offer { get; set; }

    public override FrameworkElement Root => _root;

    protected override void Fill() => _text.Text = Loc.F("Bambu Bar %@ is available", Offer?.Version ?? "");
}

/// <summary>A short message after a command ("Pausing…", "The printer refused…"), sliding in above the footer and
/// announced to screen readers.</summary>
internal sealed class ToastSection : PanelSection
{
    private readonly Toast _root;
    private readonly ContentControl _icon = new() { Focusable = false, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _message = Ui.Paragraph("", Ramp.Body, ThemeKeys.TextPrimary, FontWeights.Medium);
    private readonly TranslateTransform _slide = new();
    private Guid _shown;

    public ToastSection(PanelContext ctx) : base(ctx)
    {
        var grid = Ui.Columns("Auto", "*");
        grid.Children.Add(Ui.At(_icon, 0));
        grid.Children.Add(Ui.At(_message, 1));
        _root = new Toast { Child = grid, CornerRadius = Metrics.Tile, Padding = new Thickness(10), BorderThickness = new Thickness(1), RenderTransform = _slide };
        Ui.Tint(_root, null);
        AutomationProperties.SetLiveSetting(_root, AutomationLiveSetting.Polite);
    }

    public override FrameworkElement Root => _root;

    protected override void Fill()
    {
        if (Store.Toast is not { } toast || toast.Id == _shown) return;
        _shown = toast.Id;
        _icon.Content = Icons.Glyph(toast.IsError ? Icons.Alert : Icons.Done, 14, null, Ctx.Ink(toast.IsError ? Palette.Paused : Palette.Accent));
        _message.Text = toast.Message;
        AutomationProperties.SetName(_root, toast.Message);
        Announce.Changed(_root);
        if (!Ctx.Animate) return;
        _root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Metrics.Fast));
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Metrics.Fast)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private sealed class Toast : AccessibleCard;
}

/// <summary>"Support this project" and "Manage License…", for people without a supporter license (the Mac shows them
/// outside the App Store build).</summary>
internal sealed class SupportSection : PanelSection
{
    private readonly Grid _root;

    public SupportSection(PanelContext ctx) : base(ctx)
    {
        var support = new ChromeButton { Padding = new Thickness(4, 2, 4, 3), Content = Ui.Text(Loc.T("Support this project"), Ramp.Footnote, null, ThemeKeys.TextLink) };
        AutomationProperties.SetName(support, Loc.T("Support this project"));
        support.Click += (_, _) => Shell.Open(PolarConfiguration.SupportUrl(PolarConfiguration.SupportPlacement.MenuPanel));
        var manage = new ChromeButton { Padding = new Thickness(4, 2, 4, 3), Content = Ui.Text(Loc.T("Manage License…"), Ramp.Footnote, null, ThemeKeys.TextSecondary) };
        AutomationProperties.SetName(manage, Loc.T("Manage License…"));
        manage.Click += (_, _) => Ctx.Navigate(PanelDestination.License, null);
        _root = Ui.Columns("Auto", "*", "Auto");
        _root.Margin = new Thickness(0, -2, 0, -2);
        _root.Children.Add(Ui.At(support, 0));
        _root.Children.Add(Ui.At(manage, 2));
    }

    public override FrameworkElement Root => _root;

    protected override void Fill() { }
}

/// <summary>Open Bambu Bar (Ctrl+O) and Quit (Ctrl+Q); the window handles the shortcuts.</summary>
internal sealed class FooterSection : PanelSection
{
    private readonly Grid _root;

    public FooterSection(PanelContext ctx) : base(ctx)
    {
        var icon = Icons.Glyph(Icons.OpenWindow, 13, ThemeKeys.TextPrimary);
        var label = Ui.Text(Loc.T("Open Bambu Bar"), Ramp.Control, FontWeights.Medium);
        label.Margin = new Thickness(7, 0, 0, 1);
        var open = new ChromeButton { Padding = new Thickness(10, 6, 10, 6), Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } } };
        AutomationProperties.SetName(open, Loc.T("Open Bambu Bar"));
        AutomationProperties.SetAcceleratorKey(open, "Ctrl+O");
        open.Click += (_, _) => Ctx.Navigate(PanelDestination.Overview, null);
        var quit = new ChromeButton { Padding = new Thickness(10, 6, 10, 6), Content = Ui.Text(Loc.T("Quit"), Ramp.Control, FontWeights.Medium) };
        AutomationProperties.SetName(quit, Loc.T("Quit"));
        AutomationProperties.SetAcceleratorKey(quit, "Ctrl+Q");
        quit.Click += (_, _) => Ctx.Quit();
        _root = Ui.Columns("Auto", "*", "Auto");
        _root.Margin = new Thickness(-2, 0, -2, -2);
        _root.Children.Add(Ui.At(open, 0));
        _root.Children.Add(Ui.At(quit, 2));
    }

    public override FrameworkElement Root => _root;

    protected override void Fill() { }
}
