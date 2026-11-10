using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using BambuBar.Theme;
using BambuBar.TrayPanel;

namespace BambuBar.AppWindow;

/// <summary>Windows 11's content dialog over the window (the Mac's .alert and .confirmationDialog): a title, why, a
/// destructive action and Cancel, which has the focus.</summary>
internal static class Dialogs
{
    /// <summary>Fluent's critical red, dark enough for white text.</summary>
    private static readonly Color Critical = Color.FromRgb(0xC4, 0x2B, 0x1C);

    public static bool Confirm(FrameworkElement from, WindowContext ctx, string title, string detail, string action, string cancel)
    {
        var owner = Window.GetWindow(from);
        var dialog = new Window
        {
            Title = "Bambu Bar", ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, Owner = owner,
            UseLayoutRounding = true,
        };
        dialog.Resources.MergedDictionaries.Add(ctx.Theme);
        dialog.SetResourceReference(Control.BackgroundProperty, WindowKeys.WindowBackground);
        dialog.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.FontText);

        var heading = Ui.Paragraph(title, 20, ThemeKeys.TextPrimary, FontWeights.SemiBold);
        heading.MaxWidth = 420;
        var body = Ui.Paragraph(detail, 14, ThemeKeys.TextPrimary);
        body.MaxWidth = 420;
        var top = new Border { Padding = new Thickness(24, 22, 24, 24), Child = new Stack { Spacing = 12, Children = { heading, body } } };
        top.SetResourceReference(Border.BackgroundProperty, ThemeKeys.FlyoutFill);

        var yes = new ChromeButton
        {
            Height = 32, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Stretch,
            Look = new ButtonLook(null, null, null)
            {
                RestBrush = Palette.Brush(Critical), HoverBrush = Palette.Brush(Palette.WithAlpha(Critical, 0.9)),
                PressedBrush = Palette.Brush(Palette.WithAlpha(Critical, 0.8)),
            },
            Content = new TextBlock { Text = action, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
        };
        AutomationProperties.SetName(yes, action);
        yes.Click += (_, _) => dialog.DialogResult = true;
        var no = new ChromeButton
        {
            Height = 32, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Stretch, Look = ButtonLook.Standard,
            IsDefault = true, IsCancel = true, Content = Ui.Text(cancel, 14),
        };
        AutomationProperties.SetName(no, cancel);
        var buttons = Ui.Columns("*", "8", "*");
        buttons.Children.Add(Ui.At(yes, 0));
        buttons.Children.Add(Ui.At(no, 2));
        var footer = new Border { Padding = new Thickness(24), Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Divider);
        dialog.Content = new StackPanel { Children = { top, footer } };
        dialog.Loaded += (_, _) => no.Focus();
        dialog.SourceInitialized += (_, _) =>
            Native.SetAttribute(new WindowInteropHelper(dialog).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ctx.Dark ? 1 : 0);
        return dialog.ShowDialog() == true;
    }
}
