using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BambuBar.Core;
using BambuBar.Core.Model;
using BambuBar.Theme;

namespace BambuBar.TrayPanel;

/// <summary>
/// The job card (the Mac's JobHero, compact): the printer beside the state pill, the job's name and subtitle, the big
/// percentage with layers, the filament-coloured bar and the timing line; or, while idle, "Ready to print" with how
/// the last print went. The chamber light switch sits in the corner. The printer is Core's composited art; the Mac's
/// 3D model comes with P3.10.
/// </summary>
internal sealed class JobSection : PanelSection
{
    private readonly Border _root;
    private readonly Image _art;
    private readonly Border _pill;
    private readonly Ellipse _pillDot;
    private readonly TextBlock _pillText;
    private readonly Stack _jobTitles;
    private readonly TextBlock _name;
    private readonly TextBlock _subtitle;
    private readonly Grid _preparing;
    private readonly Grid _numbers;
    private readonly Run _percent;
    private readonly Run _percentSign;
    private readonly StackPanel _layers;
    private readonly TextBlock _layerCount;
    private readonly ProgressCapsule _bar;
    private readonly Grid _timing;
    private readonly Stack _idle;
    private readonly ChromeToggle _light;
    private bool _lightShown;

    public JobSection(PanelContext ctx) : base(ctx)
    {
        _art = ArtImage.Element(Metrics.ArtWidth, Metrics.ArtHeight);
        _art.VerticalAlignment = VerticalAlignment.Center;

        _pill = Ui.Pill(out _pillDot, out _pillText);
        _name = Ui.Text("", Ramp.Title, FontWeights.SemiBold);
        _subtitle = Ui.Text("", Ramp.Body, null, ThemeKeys.TextSecondary);
        _jobTitles = new Stack { Spacing = 2, Children = { _name, _subtitle } };

        _preparing = Ui.Columns("Auto", "*");
        var spinner = new Spinner(16, ctx.Animate) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _preparing.Children.Add(Ui.At(spinner, 0));
        _preparing.Children.Add(Ui.At(Ui.Text(Loc.T("Getting ready"), Ramp.Lead, FontWeights.SemiBold, display: true), 1));
        _preparing.Margin = new Thickness(0, 4, 0, 4);

        _percent = new Run { FontSize = Ramp.Hero };
        _percentSign = new Run("%") { FontSize = Ramp.Hero / 2 };
        _percentSign.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextSecondary);
        var number = Ui.Tabular(Ui.Text("", Ramp.Hero, FontWeights.SemiBold, display: true));
        number.Inlines.Add(_percent);
        number.Inlines.Add(_percentSign);
        number.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        number.LineHeight = 40;
        number.VerticalAlignment = VerticalAlignment.Bottom;
        _layerCount = Ui.Tabular(Ui.Text("", Ramp.Footnote, FontWeights.SemiBold, display: true));
        _layerCount.HorizontalAlignment = HorizontalAlignment.Right;
        var layersWord = Ui.Text(Loc.T("layers"), Ramp.Micro, null, ThemeKeys.TextTertiary);
        layersWord.HorizontalAlignment = HorizontalAlignment.Right;
        _layers = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 6), Children = { _layerCount, layersWord } };
        _numbers = Ui.Columns("*", "Auto");
        _numbers.Children.Add(Ui.At(number, 0));
        _numbers.Children.Add(Ui.At(_layers, 1));
        _numbers.Margin = new Thickness(0, -4, 0, 0);

        _bar = new ProgressCapsule(7);
        _timing = Ui.Columns("*", "Auto");
        _idle = new Stack { Spacing = 6 };

        var details = new Stack { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var e in new UIElement[] { _pill, _jobTitles, _preparing, _numbers, _bar, _timing, _idle }) details.Children.Add(e);

        var grid = Ui.Columns(Metrics.ArtWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), "12", "*");
        grid.Children.Add(Ui.At(_art, 0));
        grid.Children.Add(Ui.At(details, 2));

        _light = LightToggle();
        var layered = new Grid { Children = { grid, _light } };
        _root = Ui.Card(layered, Metrics.Hero, new Thickness(12));
    }

    public override FrameworkElement Root => _root;

    /// <summary>The chamber light as a small switch in the corner of the card (the Mac's LightToggle).</summary>
    private ChromeToggle LightToggle()
    {
        var toggle = new ChromeToggle
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0),
            Padding = new Thickness(8, 3, 9, 4), CornerRadius = Metrics.Pill, Look = ButtonLook.Standard,
            CheckedLook = ButtonLook.Tinted(Palette.Lamp, 0.16, 0.22, 0.28) with { Stroke = null },
        };
        AutomationProperties.SetName(toggle, Loc.T("Chamber light"));
        toggle.Click += (_, _) => Store.ToggleLight();
        return toggle;
    }

    private void FillLight(bool on)
    {
        var icon = Icons.Draw(on ? Icons.Vector.BulbLit : Icons.Vector.Bulb, 13, on ? Palette.Brush(Palette.Lamp) : Ctx.Brush(ThemeKeys.TextSecondary));
        if (on) icon.Effect = new DropShadowEffect { Color = Palette.Lamp, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 };
        var label = Ui.Text(Loc.T("Light"), Ramp.Caption, FontWeights.SemiBold, on ? ThemeKeys.TextPrimary : ThemeKeys.TextSecondary);
        label.Margin = new Thickness(5, 0, 0, 0);
        _light.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
        _light.IsChecked = on;
        Ui.Tip(_light, on ? Loc.T("Turn the chamber light off") : Loc.T("Turn the chamber light on"));
    }

    protected override void Fill()
    {
        var session = Store.Current!;
        var s = session.Status!;
        var lightOn = session.ChamberLightOn;
        ArtImage.Show(_art, ArtImage.Scene(s, session.Config.ModelName, lightOn, dimmed: false, Store.LastJob?.FilamentColor));

        _light.Visibility = s.ChamberLightOn is null ? Visibility.Collapsed : Visibility.Visible;
        if (s.ChamberLightOn is not null && (_light.Content is null || _lightShown != lightOn || _light.IsChecked != lightOn))
        {
            _lightShown = lightOn;
            FillLight(lightOn);
        }

        Ui.SetPill(_pill, _pillDot, _pillText, Palette.For(s), PillText(s), Ctx.Dark);
        // Leaves room for the light switch beside the pill.
        _pill.Margin = new Thickness(0, 0, s.ChamberLightOn is null ? 0 : 70, 0);

        var idle = s.State == JobState.Idle;
        _idle.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _jobTitles.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        var preparing = s.State == JobState.Preparing && s.Progress == 0;
        _preparing.Visibility = !idle && preparing ? Visibility.Visible : Visibility.Collapsed;
        _numbers.Visibility = !idle && !preparing ? Visibility.Visible : Visibility.Collapsed;
        _bar.Visibility = _numbers.Visibility;
        _timing.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;

        if (idle)
        {
            FillIdle();
            return;
        }
        _name.Text = s.JobName.Length == 0 ? Loc.T("Untitled print") : s.JobName;
        Ui.Tip(_name, _name.Text);
        _subtitle.Text = Subtitle(s);
        _percent.Text = s.Progress.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _layers.Visibility = s.TotalLayers > 0 ? Visibility.Visible : Visibility.Collapsed;
        _layerCount.Text = $"{s.Layer}/{s.TotalLayers}";
        _bar.Tint = Tint(s);
        _bar.SetProgress(s.Progress / 100.0, Ctx.Animate && _root.IsVisible);
        AutomationProperties.SetName(_numbers, $"{s.Progress}%");
        FillTiming(s);
    }

    private string PillText(PrinterStatus s)
    {
        if (s.State == JobState.Printing && s.Speed != SpeedLevel.Standard) return $"{s.State.Title()} · {s.Speed.Title()}";
        if (s.State == JobState.Idle && s.DoorOpen == true) return Loc.F("%@ · Door open", s.State.Title());
        return s.StateTitle;
    }

    /// <summary>The stage while it's doing something special, otherwise the filament it's printing with.</summary>
    private string Subtitle(PrinterStatus s)
    {
        if (s.State == JobState.Printing && (s.StageId is null || s.StageId == 0))
        {
            if (s.ActiveSpool is { IsLoaded: true } spool) return $"{spool.DisplayName} · {spool.SlotName}";
            return s.Speed == SpeedLevel.Standard ? s.State.Title() : Loc.F("%@ speed", s.Speed.Title());
        }
        if (s.State == JobState.Finished && Store.LastJob is { Outcome: JobOutcome.Finished, EndedAt: { } end } job)
        {
            var finished = Loc.F("Finished %@", PanelText.Ago(end));
            return job.Duration is { } d ? finished + " · " + Loc.F("took %@", Format.Duration(d)) : finished;
        }
        if (s.State == JobState.Failed)
            return s.TotalLayers > 0 ? Loc.F("Stopped at layer %lld of %lld", s.Layer, s.TotalLayers) : Loc.T("Stopped");
        return s.DetailText;
    }

    /// <summary>The bar's colour: the state's when it says something, else the filament's, unless that would vanish
    /// against the track.</summary>
    private static Color Tint(PrinterStatus s)
    {
        if (s.State == JobState.Paused) return Palette.Paused;
        if (s.State == JobState.Failed) return s.Canceled ? Palette.Idle : Palette.Failed;
        if (s.State == JobState.Finished) return Palette.Finished;
        if (s.ActiveSpool is { } spool && Palette.FromBambu(spool.Color) is { } color)
        {
            var l = Palette.Luminance(spool.Color);
            if (l is <= 0.88 and >= 0.12) return Color.FromRgb(color.R, color.G, color.B);
        }
        return Palette.Accent;
    }

    private void FillTiming(PrinterStatus s)
    {
        _timing.Children.Clear();
        void Label(string glyph, string text, Brush? color = null)
        {
            var icon = Icons.Glyph(glyph, 11, color is null ? ThemeKeys.TextSecondary : null, color);
            var label = Ui.Text(text, Ramp.Body, FontWeights.Medium);
            if (color is not null) label.Foreground = color;
            _timing.Children.Add(Ui.At(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, Spaced(label) } }, 0));
        }
        if (s.State == JobState.Finished) Label(Icons.Done, Loc.T("Ready to remove"));
        else if (s.State == JobState.Failed && s.Canceled) Label(Icons.Close, Loc.T("Clear the plate to print again"));
        else if (s.State == JobState.Failed)
            Label(Icons.Warning, s.Issues.Count == 0 ? Loc.T("Clear the plate to print again") : Loc.T("Needs attention"), Ctx.Ink(Palette.Failed));
        else if (s.EstimatedEnd is { } end)
        {
            Label(Icons.Clock, Loc.F("%@ left", Format.Duration(s.RemainingMinutes)));
            if (s.State != JobState.Paused)
            {
                var done = Ui.Tabular(Ui.Text(Loc.F("Done %@", Format.Finish(end)), Ramp.Body, FontWeights.Medium, ThemeKeys.TextSecondary));
                done.Margin = new Thickness(6, 0, 0, 0);
                _timing.Children.Add(Ui.At(done, 1));
            }
        }
        else Label(Icons.Clock, Loc.T("Estimating…"));
    }

    private static TextBlock Spaced(TextBlock text)
    {
        text.Margin = new Thickness(4, 0, 0, 0);
        return Ui.Tabular(text);
    }

    private void FillIdle()
    {
        _idle.Children.Clear();
        var titles = new Stack { Spacing = 2 };
        titles.Children.Add(Ui.Text(Loc.T("Ready to print"), Ramp.CardTitle, FontWeights.SemiBold));
        if (Store.LastJob is { } last && last.Outcome != JobOutcome.Running)
        {
            titles.Children.Add(Ui.Text(Loc.F("Last: %@", last.Name), Ramp.Body, null, ThemeKeys.TextSecondary));
            _idle.Children.Add(titles);
            var finished = last.Outcome == JobOutcome.Finished;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Icons.Glyph(finished ? Icons.Done : Icons.Failed, 12, null, Ctx.Ink(finished ? Palette.Finished : Palette.Failed)));
            row.Children.Add(Spaced(Ui.Text(last.Outcome.Title(), Ramp.Body, FontWeights.Medium)));
            if (last.EndedAt is { } ended) row.Children.Add(Spaced(Ui.Text(Loc.F("· %@", PanelText.Ago(ended)), Ramp.Body, FontWeights.Medium, ThemeKeys.TextSecondary)));
            if (last.Duration is { } d) row.Children.Add(Spaced(Ui.Text(Loc.F("· %@", Format.Duration(d)), Ramp.Body, FontWeights.Medium, ThemeKeys.TextSecondary)));
            _idle.Children.Add(row);
        }
        else
        {
            titles.Children.Add(Ui.Paragraph(Loc.T("Send a job from Bambu Studio or the printer and it shows up here.")));
            _idle.Children.Add(titles);
        }
    }
}
