using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>
/// Core's stop confirmation (the Mac's NSAlert in PrinterStore.stopPrint): "Stop “Benchy”?", why it can't be undone,
/// Stop Print or Keep Printing. Over the panel when the panel asked, which stays open meanwhile.
/// </summary>
public sealed class StopConfirmer(Func<PanelWindow?> panel) : IConfirmer
{
    public bool ConfirmStop(string title, string detail)
    {
        var owner = panel() is { IsVisible: true } p ? p : null;
        using var hold = owner?.HoldOpen();
        var dialog = new ConfirmDialog(title, detail, owner?.Variant ?? AppTheme.Current);
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return dialog.ShowDialog() == true;
    }
}

/// <summary>A small Fluent-style content dialog: title, text, a destructive button and a safe default.</summary>
internal sealed class ConfirmDialog : Window
{
    /// <summary>Fluent's critical red, dark enough for white text.</summary>
    private static readonly Color Critical = Color.FromRgb(0xC4, 0x2B, 0x1C);

    public ConfirmDialog(string title, string detail, ThemeVariant variant)
    {
        var theme = ThemeResources.Create(variant);
        Resources.MergedDictionaries.Add(theme);
        Title = "Bambu Bar";
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, ThemeKeys.PanelBackground);
        SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontText);
        UseLayoutRounding = true;

        var heading = Ui.Paragraph(title, 20, ThemeKeys.TextPrimary, FontWeights.SemiBold);
        heading.MaxWidth = 400;
        var body = Ui.Paragraph(detail, 14, ThemeKeys.TextPrimary);
        body.MaxWidth = 400;
        var top = new Border { Padding = new Thickness(24, 22, 24, 24), Child = new Stack { Spacing = 12, Children = { heading, body } } };
        top.SetResourceReference(Border.BackgroundProperty, ThemeKeys.FlyoutFill);

        var stop = new ChromeButton
        {
            Height = 32, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Stretch,
            Look = new ButtonLook(null, null, null)
            {
                RestBrush = Palette.Brush(Critical), HoverBrush = Palette.Brush(Palette.WithAlpha(Critical, 0.9)),
                PressedBrush = Palette.Brush(Palette.WithAlpha(Critical, 0.8)),
            },
            Content = Ui.Text(Loc.T("Stop Print"), 14, FontWeights.SemiBold),
        };
        ((TextBlock)stop.Content).Foreground = Brushes.White;
        AutomationProperties.SetName(stop, Loc.T("Stop Print"));
        stop.Click += (_, _) => DialogResult = true;
        var keep = new ChromeButton
        {
            Height = 32, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Stretch, Look = ButtonLook.Standard,
            IsDefault = true, IsCancel = true, Content = Ui.Text(Loc.T("Keep Printing"), 14),
        };
        AutomationProperties.SetName(keep, Loc.T("Keep Printing"));
        var buttons = Ui.Columns("*", "8", "*");
        buttons.Children.Add(Ui.At(stop, 0));
        buttons.Children.Add(Ui.At(keep, 2));
        var footer = new Border { Padding = new Thickness(24), Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Divider);

        Content = new StackPanel { Children = { top, footer } };
        // The safe answer has the focus, as Windows does for destructive confirmations.
        Loaded += (_, _) => keep.Focus();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, variant == ThemeVariant.Dark ? 1 : 0);
        };
    }
}
