using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Core;
using BambuBar.Core.Model;
using BambuBar.Theme;

namespace BambuBar.AppWindow;

/// <summary>Every loaded spool, large, with material, colour, what's left and the AMS climate (the Mac's
/// FilamentPage). Units are rebuilt only when something about their spools changes.</summary>
internal sealed class FilamentPage(WindowContext ctx) : Page(ctx)
{
    private TextBlock _subtitle = null!;
    private readonly Region _body = new();

    public override FrameworkElement Build()
    {
        _subtitle = W.Subtitle("");
        return new Stack { Spacing = 20, Children = { W.PageHeader(Ui.Text(Loc.T("Filament"), WRamp.PageTitle, FontWeights.SemiBold, display: true), _subtitle, null), _body.Host } };
    }

    public override void Refresh()
    {
        var session = Store.Current;
        var s = session?.Status;
        _subtitle.Text = Subtitle(s);
        if (session is null || s is null || !session.IsOnline)
        {
            var configured = Store.IsConfigured;
            _body.Show($"empty|{configured}", () => W.Empty("",
                configured ? Loc.T("Printer offline") : Loc.T("No printer set up"),
                configured ? Loc.T("Filament appears once Bambu Bar is connected.") : Loc.T("Set up your printer on the Printer page.")));
            return;
        }
        var activeId = s.State.IsActive() ? s.ActiveSpoolId : null;
        var key = session.Id + "|" + string.Join("#", s.Ams.Select(u => $"{u.Id}{u.Kind}{u.Humidity}{u.HumidityLevel}{u.Temperature:0}{u.DryingMinutesLeft}|"
            + string.Join(",", u.Spools.Select(SpoolKey)))) + $"|{(s.ExternalSpool is { } e ? SpoolKey(e) : "")}|{activeId}|{Loc.Language}";
        _body.Show(key, () => Units(s, activeId));
    }

    private static string SpoolKey(Spool s) => $"{s.Id}:{s.Color}:{string.Join('-', s.Colors)}:{s.Remain}:{s.Material}:{s.Name}:{s.IsLoaded}:{s.Weight}:{s.Brand}";

    /// <summary>"4 spools loaded · ≈ 2.4 kg of filament on hand", or what it's printing with.</summary>
    private static string Subtitle(PrinterStatus? s)
    {
        if (s is null) return Loc.T("Spools in your AMS and on the external holder");
        var spools = s.AllSpools.ToList();
        var loaded = spools.Count(sp => sp.IsLoaded);
        var grams = spools.Select(sp => sp.GramsLeft).OfType<int>().ToList();
        var count = Loc.P("%lld spool(s) loaded", loaded);
        var active = s.State.IsActive() ? s.ActiveSpool : null;
        if (grams.Count > 0 && active is null) return count + " · " + Loc.F("%@ of filament on hand", Format.Grams(grams.Sum()));
        if (active is not null) return count + " · " + Loc.F("printing with %@ %@", active.SlotName, active.DisplayName);
        return count;
    }

    private UIElement Units(PrinterStatus s, int? activeId)
    {
        var stack = new Stack { Spacing = 22 };
        if (s.Ams.Count == 0 && s.ExternalSpool is null)
            stack.Children.Add(W.Empty("", Loc.T("No AMS connected"), Loc.T("Spools loaded in an AMS or on the external holder appear here.")));
        foreach (var unit in s.Ams)
        {
            var header = Ui.Columns("*", "Auto");
            var title = Ui.Text(unit.Title, 16, FontWeights.SemiBold);
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
            header.Children.Add(Ui.At(title, 0));
            header.Children.Add(Ui.At(Climate(unit), 1));
            var grid = new AdaptiveGrid { Columns = 4, Spacing = 12 };
            foreach (var spool in unit.Spools) grid.Children.Add(SpoolCard(spool, spool.Id == activeId));
            stack.Children.Add(new Stack { Spacing = 10, Children = { header, grid } });
        }
        if (s.ExternalSpool is { } ext)
        {
            var title = Ui.Text(Loc.T("External spool"), 16, FontWeights.SemiBold);
            var grid = new AdaptiveGrid { Columns = 4, Spacing = 12, Children = { SpoolCard(ext, ext.Id == activeId) } };
            stack.Children.Add(new Stack { Spacing = 10, Children = { title, grid } });
        }
        return stack;
    }

    /// <summary>Drying time, humidity (amber when it's high) and temperature.</summary>
    private StackPanel Climate(AmsUnit unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Add(FrameworkElement icon, string text, Color? tint)
        {
            var label = Ui.Text(text, WRamp.Note, FontWeights.SemiBold, ThemeKeys.TextSecondary);
            if (tint is { } t) label.Foreground = Ctx.Ink(t);
            label.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), Children = { icon, label } });
        }
        if (unit.DryingMinutesLeft > 0)
            Add(Icons.Glyph("", 12, null, Ctx.Ink(Palette.Bed)), Loc.F("Drying · %@ left", Format.Duration(unit.DryingMinutesLeft)), Palette.Bed);
        if (unit.Humidity is { } h)
        {
            var high = h > 40;
            Add(Icons.Glyph("", 12, high ? null : ThemeKeys.TextSecondary, high ? Ctx.Ink(Palette.Paused) : null), Loc.F("%lld%% humidity", h), high ? Palette.Paused : null);
        }
        else if (unit.HumidityLevel is { } level)
        {
            var high = level >= 4;
            Add(Icons.Glyph("", 12, high ? null : ThemeKeys.TextSecondary, high ? Ctx.Ink(Palette.Paused) : null), Loc.F("Humidity %lld of 5", level), high ? Palette.Paused : null);
        }
        if (unit.Temperature is { } t) Add(Icons.Draw(Icons.Vector.Thermometer, 12, Ctx.Brush(ThemeKeys.TextSecondary)), Format.Temperature(t), null);
        return row;
    }

    /// <summary>One slot (the Mac's SpoolCard): the spool with its weight on the hub, the slot label, the name and
    /// details, and what's left. Every card keeps the same proportions, measured or not.</summary>
    private FrameworkElement SpoolCard(Spool spool, bool active)
    {
        var swatch = new Grid { Width = 58, Height = 58, HorizontalAlignment = HorizontalAlignment.Left };
        var hubText = spool.IsLoaded && spool.Weight is { } weight ? Format.Grams(weight, approximate: false) : null;
        swatch.Children.Add(new SpoolSwatch(spool, active, 58, hubText));
        var label = new Border
        {
            CornerRadius = Metrics.Pill, Padding = new Thickness(7, 1, 7, 2), HorizontalAlignment = HorizontalAlignment.Right,
            Child = Ui.Text(spool.Label, 12, FontWeights.Bold, display: true),
        };
        Ui.Capsule(label);
        label.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Track);
        var badges = new Stack { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Children = { label } };
        if (active)
        {
            var inUse = Ui.Text(Loc.T("In use"), Ramp.Caption, FontWeights.SemiBold);
            inUse.Foreground = Ctx.Ink(Palette.Accent);
            inUse.HorizontalAlignment = HorizontalAlignment.Right;
            badges.Children.Add(inUse);
        }
        var top = Ui.Columns("Auto", "*");
        top.Children.Add(Ui.At(swatch, 0));
        top.Children.Add(Ui.At(badges, 1));

        var name = Ui.Text(spool.IsLoaded ? spool.DisplayName : Loc.T("Empty slot"), 14, FontWeights.SemiBold,
            spool.IsLoaded ? ThemeKeys.TextPrimary : ThemeKeys.TextSecondary);
        var detail = Ui.Text(spool.IsLoaded ? Detail(spool) : Loc.T("Load a spool to see it here"), WRamp.Note, null, ThemeKeys.TextSecondary);

        var remainingTop = Ui.Columns("*", "Auto");
        remainingTop.Children.Add(Ui.At(Ui.Text(Loc.T("Remaining"), Ramp.Caption, null, ThemeKeys.TextSecondary), 0));
        if (spool.GramsLeft is { } grams) remainingTop.Children.Add(Ui.At(Ui.Tabular(Ui.Text(Format.Grams(grams), Ramp.Caption, FontWeights.SemiBold)), 1));
        var barRow = Ui.Columns("*", "36");
        var tint = Palette.FromBambu(spool.Color) is { } c && Palette.Luminance(spool.Color) <= 0.88 ? Color.FromRgb(c.R, c.G, c.B) : Palette.Idle;
        var bar = new ProgressCapsule(5) { Tint = tint, VerticalAlignment = VerticalAlignment.Center };
        bar.SetProgress((spool.Remain ?? 0) / 100.0, false);
        barRow.Children.Add(Ui.At(bar, 0));
        var percent = Ui.Tabular(Ui.Text($"{spool.Remain ?? 0}%", Ramp.Caption, FontWeights.SemiBold));
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        barRow.Children.Add(Ui.At(percent, 1));
        var measured = spool.IsLoaded && spool.Remain is not null;
        var remaining = new Stack { Spacing = 5, Children = { remainingTop, barRow }, Opacity = measured ? 1 : 0 };

        var content = new Stack { Spacing = 12, Children = { top, new Stack { Spacing = 2, Children = { name, detail } }, remaining } };
        var card = new AccessibleCard { Child = content, CornerRadius = Metrics.Card, Padding = new Thickness(14), BorderThickness = new Thickness(active ? 1.2 : 1) };
        Ui.Tint(card, active ? Palette.Accent : null);
        if (active) card.BorderBrush = Palette.Brush(Palette.WithAlpha(Palette.Accent, 0.6));
        var help = Help(spool);
        Ui.Tip(card, help);
        AutomationProperties.SetName(card, help.Replace('\n', ',') + (measured ? $", {spool.Remain}%" : ""));
        return card;
    }

    /// <summary>"Bambu Lab · Grass Green", with the material when the name doesn't say it and the hex code when the
    /// colour has no Bambu name.</summary>
    private static string Detail(Spool spool)
    {
        var parts = new List<string>();
        if (spool.Brand is { } brand) parts.Add(brand);
        if (!spool.DisplayName.Contains(spool.Material, StringComparison.CurrentCultureIgnoreCase)) parts.Add(spool.Material);
        parts.Add(spool.ColorName ?? "#" + Hex(spool.Color));
        return string.Join(" · ", parts);
    }

    private static string Hex(string color) => color.Length >= 6 ? color[..6] : color;

    private static string Help(Spool spool)
    {
        if (!spool.IsLoaded) return Loc.F("%@: empty", spool.SlotName);
        var lines = new List<string>
        {
            string.Join(" ", new[] { spool.Brand, spool.DisplayName }.Where(p => !string.IsNullOrEmpty(p))),
            string.Join(" · ", new[] { spool.ColorName, "#" + Hex(spool.Color) }.Where(p => !string.IsNullOrEmpty(p))),
        };
        if (spool.NozzleRange is { } range) lines.Add(Loc.F("Prints at %lld–%lld °C", range.Low, range.High));
        if (spool.Remain is null)
            lines.Add(spool.IsExternal ? Loc.T("The external holder can’t weigh the spool.") : Loc.T("Only Bambu Lab spools with an RFID tag report what’s left."));
        return string.Join("\n", lines);
    }
}
