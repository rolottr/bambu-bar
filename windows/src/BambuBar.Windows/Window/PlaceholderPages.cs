using System.Windows;
using System.Windows.Automation;
using BambuBar.Core;
using BambuBar.Core.Protocol;
using BambuBar.Theme;

namespace BambuBar.AppWindow;

/// <summary>Where the setup flow (P3.6) goes: the Printer page without a printer, or "Add Printer…". Lists what the
/// network search has found so far, the panel's pick highlighted. The English note is deliberately not in the catalog:
/// the page is replaced before release.</summary>
internal sealed class SetupPlaceholderPage(WindowContext ctx, DiscoveredPrinter? candidate) : Page(ctx)
{
    private readonly Region _found = new();

    public override FrameworkElement Build()
    {
        var cancel = Store.IsConfigured ? W.Button(Loc.T("Cancel"), () => Ctx.Show(AppPage.Printer)) : null;
        var note = W.Card(new Stack
        {
            Spacing = 8,
            Children =
            {
                Icons.Glyph(AppPage.Printer.Glyph(), 28, ThemeKeys.TextTertiary),
                Ui.Text("Setup comes with task P3.6", 16, FontWeights.SemiBold),
                Ui.Paragraph("Discovery, Add All, the slicer's access codes, a typed code or a dropped file, and Open at sign-in. Until then, " +
                    "run with --demo or --connect <host> <serial> <code> to see a printer.", WRamp.Detail),
            },
        }, 20);
        return new Stack { Spacing = 22, Children = { W.PageHeader(Loc.T("Add a Printer"), null, cancel), note, _found.Host } };
    }

    public override void Refresh()
    {
        var found = Store.Discovered.ToList();
        _found.Show(string.Join("#", found.Select(p => p.Serial + p.Name + p.Host)), () =>
        {
            if (found.Count == 0) return W.Note(Loc.T("Looking for Bambu Lab printers on your network…"), ThemeKeys.TextSecondary, WRamp.Detail);
            var list = new Stack { Spacing = 8, Children = { Ui.SectionLabel(Loc.P("Found %lld printer(s) on your network.", found.Count), null) } };
            foreach (var printer in found)
            {
                var row = Ui.Columns("Auto", "*");
                var icon = Icons.Glyph(Icons.Printer, 16, null, Palette.Brush(Palette.Accent));
                icon.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(Ui.At(icon, 0));
                row.Children.Add(Ui.At(new Stack
                {
                    Spacing = 2,
                    Children =
                    {
                        Ui.Text(printer.Name, WRamp.Body, FontWeights.SemiBold),
                        Ui.Text(Loc.F("%@ · %@", printer.ModelName, printer.Host), WRamp.Note, null, ThemeKeys.TextSecondary),
                    },
                }, 1));
                var card = W.Card(row, 12, printer.Serial == candidate?.Serial ? Palette.Accent : null);
                AutomationProperties.SetName(card, printer.Name);
                list.Children.Add(card);
            }
            return list;
        });
    }
}

/// <summary>Where the Widgets page (P3.8) goes: the showcase, Add widget per size and printer, and the placed widgets.
/// The English note is deliberately not in the catalog: the page is replaced before release.</summary>
internal sealed class WidgetsPlaceholderPage(WindowContext ctx) : Page(ctx)
{
    public override FrameworkElement Build()
    {
        var note = W.Card(new Stack
        {
            Spacing = 8,
            Children =
            {
                Icons.Glyph(AppPage.Widgets.Glyph(), 28, ThemeKeys.TextTertiary),
                Ui.Text("The Widgets page comes with task P3.8", 16, FontWeights.SemiBold),
                Ui.Paragraph("A showcase of the Small, Medium and Large desktop widgets in every state, Add widget for each size and " +
                    "printer, and the list of placed widgets with Remove. The widgets themselves already run (Rainmeter, P2).", WRamp.Detail),
            },
        }, 20);
        return new Stack { Spacing = 22, Children = { W.PageHeader(Loc.T("Widgets"), null), note } };
    }
}
