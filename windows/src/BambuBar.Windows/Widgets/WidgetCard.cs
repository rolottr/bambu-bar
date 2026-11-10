using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuBar.Core.Imaging;
using BambuBar.Core.Widgets;
using BambuBar.Theme;

namespace BambuBar.Widgets;

/// <summary>
/// A desktop widget drawn in WPF from the same <see cref="WidgetState"/> the Rainmeter skins read, laid out by the
/// skins' own rules (<c>rainmeter/Skins/BambuBar/@Resources/Scripts/widget.lua</c>: <c>BB.model</c>, <c>viewSmall</c>,
/// <c>viewMedium</c>, <c>viewLarge</c>): the always-dark card, the progress ring and bar, the printer art, the spools,
/// the temperature tiles. For the Widgets page's showcase; the real widgets stay Rainmeter's. Not pixel-identical to
/// Rainmeter (Direct2D vs WPF text), but the same positions, sizes, fonts and colours.
/// </summary>
public sealed class WidgetCard : FrameworkElement
{
    /// <summary>The card in logical pixels (the skins' CardW × CardH, without the shadow margin).</summary>
    public static Size CardSize(WidgetHost.Size size) => size switch
    {
        WidgetHost.Size.Small => new Size(180, 180),
        WidgetHost.Size.Medium => new Size(380, 180),
        _ => new Size(380, 380),
    };

    private WidgetState? _state;
    private PrinterArt.Scene? _scene;
    private DateTimeOffset _now = DateTimeOffset.Now;
    private bool _hover;
    private double _ppd = 1;

    public WidgetCard(WidgetHost.Size size)
    {
        Size = size;
        var card = CardSize(size);
        Width = card.Width;
        Height = card.Height;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    public WidgetHost.Size Size { get; }

    /// <summary>The spinner's angle while connecting (degrees); the showcase turns it.</summary>
    public double SpinAngle { get; set; }

    /// <summary>Draws <paramref name="state"/> as it reads at <paramref name="now"/> (countdown, projected progress).
    /// <paramref name="art"/> is the printer drawing; the card renders it at its own slot size.</summary>
    public void Show(WidgetState state, PrinterArt.Scene? art, DateTimeOffset now)
    {
        _state = state;
        _scene = art;
        _now = now;
        System.Windows.Automation.AutomationProperties.SetName(this, $"{state.Name}: {state.Caption}, {state.Headline}");
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = false; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var card = CardSize(Size);
        var outline = new RectangleGeometry(new Rect(card), 14, 14);
        // The card (always dark, ledger O6): the skins' gradient, a white hairline, both lighter on hover.
        var fill = new LinearGradientBrush(_hover ? Rgb(0x31, 0x33, 0x37) : Rgb(0x29, 0x2B, 0x2E), _hover ? Rgb(0x19, 0x1B, 0x1E) : Rgb(0x14, 0x17, 0x19), 90);
        dc.DrawGeometry(fill, null, outline);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(_hover ? 56 : 20), 255, 255, 255)), 1),
            new Rect(0.5, 0.5, card.Width - 1, card.Height - 1), 13.5, 13.5);
        if (_state is null) return;
        dc.PushClip(outline);
        var m = new Model(_state, _now);
        switch (Size)
        {
            case WidgetHost.Size.Small: DrawSmall(dc, m); break;
            case WidgetHost.Size.Medium: DrawMediumRow(dc, m, 16, 148, inLarge: false); break;
            default: DrawLarge(dc, m); break;
        }
        dc.Pop();
    }

    // MARK: Model (BB.model)

    /// <summary>What the widget shows at a moment: the file plus the countdown and projected progress.</summary>
    private sealed class Model
    {
        public readonly WidgetState S;
        public readonly bool Live, Active;
        public readonly string State;
        public readonly double Progress;
        public readonly int Percent;
        public readonly string? Countdown, LeftWord, Finish;
        public readonly string StateLine, DetailFirst;
        public readonly WidgetState.SpoolView? ActiveSpool;
        public readonly double Heat;
        public readonly Color StateColor, Tint, CaptionColor;

        public Model(WidgetState s, DateTimeOffset now)
        {
            S = s;
            var t = now.ToUnixTimeSeconds();
            Live = s.Live;
            State = s.State;
            Active = Live && State is "printing" or "preparing" or "paused";
            Progress = Live ? Projected(s, t) : 0;
            Percent = (int)Math.Floor(Progress * 100 + 1e-9);
            int? left = !Live ? null
                : State == "printing" && s.EndsAt is { } end ? Math.Max(0, (int)Math.Ceiling((end - t) / 60.0))
                : State == "paused" ? s.RemainingMinutes : null;
            Countdown = left is { } l ? Duration(l, s.Strings) : null;
            StateLine = Live && State == "printing" && Countdown is not null ? Fill(s.Strings.Left, Countdown) : s.StateLine;
            LeftWord = s.Strings.LeftWord.Length > 0 ? s.Strings.LeftWord : null;
            var dot = s.Detail.IndexOf(" · ", StringComparison.Ordinal);
            DetailFirst = dot >= 0 ? s.Detail[..dot] : s.Detail;
            if (Live && State == "printing" && s.Finish is { } finish) Finish = Fill(s.Strings.Done, finish);
            else if (Live && State == "printing" && DetailFirst.Length > 0 && DetailFirst != s.Layers) Finish = DetailFirst;
            ActiveSpool = s.Spools.FirstOrDefault(sp => sp.Active);
            if (Live && State == "preparing" && s.Temps.Count > 0 && Num(s.Temps[0].Value) is { } v && Num(s.Temps[0].Target) is { } target && target > 0)
                Heat = Math.Clamp(v / target, 0, 1);
            StateColor = Hex(s.StateColor);
            Tint = Hex(s.Tint);
            CaptionColor = Live ? StateColor : s.Link == "unauthorized" ? Hex(WidgetPalette.Paused) : SecondaryText;
        }

        private static double Projected(WidgetState s, long now)
        {
            var p = s.Progress / 100.0;
            if (s.State != "printing" || s.UpdatedAt is not { } u || s.EndsAt is not { } e || e <= u) return p;
            var f = Math.Clamp((now - u) / (double)(e - u), 0, 1);
            return Math.Min(0.99, p + (1 - p) * f);
        }

        /// <summary>The ring's value (BB.dialValue): heat while preparing and full when finished, in the Small size.</summary>
        public double DialValue(bool small)
        {
            if (!Live) return 0;
            if (small && State == "preparing") return Heat;
            if (small && State == "finished") return 1;
            return State == "idle" ? 0 : Progress;
        }
    }

    private static string Fill(string pattern, string a, string? b = null)
    {
        var text = pattern.Length == 0 ? "{0}" : pattern;
        text = text.Replace("{0}", a, StringComparison.Ordinal);
        return b is null ? text : text.Replace("{1}", b, StringComparison.Ordinal);
    }

    private static string Duration(int minutes, WidgetState.StringsView strings)
    {
        if (minutes <= 0) return strings.LessThanMinute.Length > 0 ? strings.LessThanMinute : "<1m";
        var h = minutes / 60;
        if (h == 0) return Fill(strings.Minutes, minutes.ToString(CultureInfo.InvariantCulture));
        if (h >= 24) return Fill(strings.DaysHours, (h / 24).ToString(CultureInfo.InvariantCulture), (h % 24).ToString(CultureInfo.InvariantCulture));
        return Fill(strings.HoursMinutes, h.ToString(CultureInfo.InvariantCulture), (minutes % 60).ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>"164°" → 164.</summary>
    private static double? Num(string? text)
    {
        if (text is null) return null;
        var digits = new string(text.SkipWhile(c => !char.IsAsciiDigit(c) && c != '-').TakeWhile(c => char.IsAsciiDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // MARK: Small (BB.viewSmall)

    private void DrawSmall(DrawingContext dc, Model m)
    {
        const double P = 16, colX = 88, colW = 76, fullW = 148, cx = 47, cy = 47;
        var ringed = m.Live && m.State != "idle";
        if (ringed)
        {
            var tint = m.State is "preparing" or "finished" ? m.StateColor : m.Tint;
            Ring(dc, cx, cy, 62, 7, m.DialValue(small: true), tint);
            if (m.State is "printing" or "paused")
            {
                var up = m.State == "paused" ? 5 : 0;
                Text(dc, $"{m.Percent}%", St.DialPct, cx, cy - up - 18 * 1.1 / 2, new() { Align = TextAlignment.Center });
                if (m.State == "paused") Icon(dc, "pause", cx - 4.5, cy + 6, 9, Hex(WidgetPalette.Paused));
            }
            else if (DialGlyph(m) is { } g)
            {
                Icon(dc, g.Kind, cx - g.Size / 2, cy - g.Size / 2, g.Size, g.Color);
            }
        }
        else
        {
            Art(dc, P + 2, P + 2, 58, 58);
            if (m.S.Link == "connecting") Spinner(dc, cx, cy, 62, 3, Color.FromArgb(0x8C, 0xEB, 0xEB, 0xF5));
        }

        var y = P;
        var reflow = false;
        if (m.Live && m.State is "paused" or "preparing" && m.S.Reason is { Length: > 0 } reason)
            reflow = reason.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => Measure(word, St.Reason) > colW);

        if (m.Live)
        {
            y += Text(dc, m.S.Caption, St.Cap, colX, y, new() { W = colW, Fit = 0.8, Color = m.CaptionColor }).Used + 1;
            switch (m.State)
            {
                case "printing":
                    y += Text(dc, m.Countdown ?? "", St.Big, colX, y, new() { W = colW, Fit = 0.7 }).Used + 1;
                    if (m.LeftWord is { } word) y += Text(dc, word, St.Sub, colX, y, new() { W = colW }).Used + 1;
                    break;
                case "preparing" or "paused":
                    if (!reflow) y += Text(dc, m.S.Reason ?? "", St.Reason, colX, y, new() { W = colW, Lines = 3 }).Used + 1;
                    else if (m.State == "paused" && m.S.Layers is not null) y += Text(dc, m.DetailFirst, St.SubNum, colX, y, new() { W = colW }).Used + 1;
                    break;
                case "finished" or "failed":
                    y += Text(dc, m.S.LastJob?.Ago ?? "", St.BigAgo, colX, y, new() { W = colW, Fit = 0.7 }).Used + 1;
                    if (m.S.LastJob?.Took is { } took) y += Text(dc, took, St.Sub, colX, y, new() { W = colW }).Used + 1;
                    break;
                case "idle":
                    y += 2;
                    foreach (var t in m.S.Temps.Take(3))
                    {
                        var hot = Num(t.Target) is { } target && Num(t.Value) is { } value && value < target;
                        var used = Text(dc, t.Value, St.TempRow, colX + 14, y, new()).Used;
                        Icon(dc, t.Kind, colX, y + (used - 10) / 2, 10, hot ? Hex(t.Color) : SecondaryIcon, hot ? 1 : SecondaryAlpha);
                        y += used + 2;
                    }
                    break;
            }
        }
        else
        {
            y += Text(dc, m.S.Headline.Length > 0 ? m.S.Headline : m.S.Caption, St.CapTwo, colX, y,
                new() { W = colW, Lines = 2, Fit = 0.8, FitWords = true, Color = m.CaptionColor }).Used + 1;
            if (m.S.Detail.Length > 0) y += Text(dc, m.S.Detail, St.NoLive, colX, y + 1, new() { W = colW, Lines = 3 }).Used + 2;
        }
        var topBottom = Math.Max(P + 62, y);

        const double bottom = 180 - P;
        double footTop;
        if (m.Live && m.State == "idle")
        {
            var hd = Text(null, m.StateLine, St.FootLine, P, 0, new() { W = fullW }).Used;
            footTop = bottom - hd - 4 - 21;
            Text(dc, m.StateLine, St.FootLine, P, bottom - hd, new() { W = fullW });
            var x = P;
            foreach (var sp in m.S.Spools.Where(sp => !sp.Empty).Take(5))
            {
                Spool(dc, x, footTop, 21, sp, active: false);
                x += 26;
            }
        }
        else if (m.Live)
        {
            var detail = reflow ? null : m.S.Detail;
            double hd = 0;
            if (detail is { Length: > 0 })
            {
                hd = Text(null, detail, St.FootDetail, P, 0, new() { W = fullW }).Used;
                Text(dc, detail, St.FootDetail, P, bottom - hd, new() { W = fullW });
            }
            var ht = Text(null, m.S.Headline, St.FootTitle, P, 0, new() { W = fullW }).Used;
            footTop = bottom - hd - ht;
            Text(dc, m.S.Headline, St.FootTitle, P, footTop, new() { W = fullW });
        }
        else
        {
            var hd = Text(null, m.StateLine, St.FootLine, P, 0, new() { W = fullW, Lines = 2 }).Used;
            Text(dc, m.StateLine, St.FootLine, P, bottom - hd, new() { W = fullW, Lines = 2 });
            var ht = Text(null, m.S.Name, St.FootTitle, P, 0, new() { W = fullW }).Used;
            footTop = bottom - hd - ht;
            Text(dc, m.S.Name, St.FootTitle, P, footTop, new() { W = fullW });
        }

        if (reflow)
        {
            Text(dc, m.S.Reason ?? "", St.Reason, P, topBottom + 8, new() { W = fullW, Lines = 3 });
        }
        else if (m.Active && m.ActiveSpool is { } spool && m.S.ActiveSpoolLine is { } line)
        {
            var ys = topBottom + Math.Max(0, (footTop - topBottom - 16) / 2);
            Spool(dc, P, ys, 16, spool, active: true);
            var used = Text(null, line, St.SpoolLine, P + 21, 0, new() { W = fullW - 21 }).Used;
            Text(dc, line, St.SpoolLine, P + 21, ys + (16 - used) / 2, new() { W = fullW - 21 });
        }
    }

    private static (string Kind, double Size, Color Color)? DialGlyph(Model m) => m.State switch
    {
        "preparing" => ("nozzle", 19, Hex(WidgetPalette.Nozzle)),
        "finished" => ("check", 22, m.StateColor),
        "failed" when m.S.Canceled => ("cross", 19, m.Tint),
        "failed" => ("bang", 19, m.Tint),
        _ => null,
    };

    // MARK: Medium and Large (mediumInner, viewLarge)

    /// <summary>The top row: Medium's whole card (180 high), Large's first 125 px. Returns the row's bottom.</summary>
    private double DrawMediumRow(DrawingContext dc, Model m, double top, double rowH, bool inLarge)
    {
        const double P = 16, colX = 128, colW = 236, right = 364;
        Art(dc, P, top + (rowH - 116) / 2, 98, 116);
        var bottom = top + rowH;

        var y = top;
        const double hh = 15;
        double rightW = 0;
        if (m.Live)
        {
            var (used, w, _) = Text(null, m.S.Caption, St.Status, right, 0, new());
            Text(dc, m.S.Caption, St.Status, right, y + (hh - used) / 2, new() { Align = TextAlignment.Right, Color = m.CaptionColor });
            var dotCx = right - w - 5 - 3;
            Dot(dc, dotCx, y + hh / 2, m.StateColor);
            rightW = w + 5 + 6 + 8;
        }
        else if (m.S.Link == "connecting")
        {
            Spinner(dc, right - 6, y + hh / 2, 12, 2, Color.FromArgb(0xB3, 0xEB, 0xEB, 0xF5));
            rightW = 20;
        }
        else if (m.S.Link == "unauthorized")
        {
            Icon(dc, "key", right - 13, y + 1, 13, Hex(WidgetPalette.Paused));
            rightW = 21;
        }
        var nameUsed = Text(null, m.S.Name, St.Name, colX, 0, new()).Used;
        Text(dc, m.S.Name, St.Name, colX, y + (hh - nameUsed) / 2, new() { W = colW - rightW });
        y += hh + 3;
        y += Text(dc, m.S.Headline, St.Headline, colX, y, new() { W = colW }).Used;

        if (m.Active)
        {
            var pctUsed = Text(dc, $"{m.Percent}%", St.Pct, colX, y, new()).Used;
            if (m.S.Layers is { } layers)
            {
                var lyUsed = Text(null, layers, St.Layers, right, 0, new()).Used;
                Text(dc, layers, St.Layers, right, y + pctUsed - lyUsed - 3, new() { Align = TextAlignment.Right });
            }
            y += pctUsed + 5;
            Bar(dc, colX - 2, y - 2, colW, m.Progress, m.Tint);
            y += 6 + 5;
            double rw = 0;
            if (m.Finish is { } finish) rw = Text(dc, finish, St.SlineRight, right, y, new() { Align = TextAlignment.Right }).Width + 8;
            Text(dc, m.StateLine, St.Sline, colX, y, new() { W = colW - rw });
            if (!inLarge)
            {
                var tx = colX;
                foreach (var t in m.S.Temps.Take(3))
                {
                    var ty = bottom - 11.5 * 1.25;
                    Icon(dc, t.Kind, tx, ty + 2.2, 10, t.Target is null ? SecondaryIcon : Hex(t.Color), t.Target is null ? SecondaryAlpha : 1);
                    var w = Text(dc, t.Value, St.TempInline, tx + 13, ty, new()).Width;
                    tx += 13 + w + 12;
                }
            }
        }
        else
        {
            y += 2;
            Text(dc, m.StateLine, St.Line2, colX, y, new() { W = colW, Lines = 2 });
            if (m.Live)
            {
                var x = colX;
                foreach (var sp in m.S.Spools.Take(8))
                {
                    Spool(dc, x, bottom - 23, 23, sp, active: false);
                    x += 29;
                }
            }
        }
        return bottom;
    }

    private void DrawLarge(DrawingContext dc, Model m)
    {
        const double P = 16, W = 348, bottom = 380 - P;
        var rowBottom = DrawMediumRow(dc, m, P, 125, inLarge: true);
        if (!m.Live)
        {
            // An info box that says what to do.
            var text = m.S.Info ?? m.StateLine;
            const double tx = P + 12 + 14 + 8, tw = W - 24 - 22;
            var used = Text(null, text, St.Info, tx, 0, new() { W = tw, Lines = 4 }).Used;
            var boxH = 24 + used;
            var by = bottom - boxH;
            dc.DrawRoundedRectangle(Brush(Tile), null, new Rect(P, by, W, boxH), 12, 12);
            var unauthorized = m.S.Link == "unauthorized";
            var glyph = m.S.Link == "unconfigured" ? "printer" : unauthorized ? "key" : "wifiOff";
            Icon(dc, glyph, P + 12, by + 13, 14, unauthorized ? Hex(WidgetPalette.Paused) : SecondaryIcon, unauthorized ? 1 : SecondaryAlpha);
            Text(dc, text, St.Info, tx, by + 12, new() { W = tw, Lines = 4 });
            return;
        }

        // Temperature tiles.
        var y = rowBottom + 14;
        var temps = m.S.Temps.Take(3).ToList();
        const double tileW = (W - 8 * 2) / 3, tileH = 7 + 11 * 1.25 + 2 + 19 * 1.15 + 7;
        for (var i = 0; i < temps.Count; i++)
        {
            var t = temps[i];
            var x = P + i * (tileW + 8);
            dc.DrawRoundedRectangle(Brush(Tile), null, new Rect(x, y, tileW, tileH), 11, 11);
            Icon(dc, t.Kind, x + 9, y + 7 + 2, 10, t.Target is null ? SecondaryIcon : Hex(t.Color), t.Target is null ? SecondaryAlpha : 1);
            Text(dc, t.Label, St.TileLabel, x + 9 + 13, y + 7, new() { W = tileW - 18 - 13 });
            var vy = y + 7 + 11 * 1.25 + 2;
            var w = Text(dc, t.Value, St.TileValue, x + 9, vy, new()).Width;
            if (Num(t.Value) is { } tv && Num(t.Target) is { } tt && Math.Abs(tv - tt) > 2)
            {
                var target = "→ " + t.Target;
                var tu = Text(null, target, St.TileTarget, 0, 0, new()).Used;
                Text(dc, target, St.TileTarget, x + 9 + w + 4, vy + 19 * 1.15 - tu - 2, new() { Color = Hex(t.Color) });
            }
        }
        y += tileH + 14;

        // The first AMS unit.
        if (m.S.Ams is { } ams && m.S.Spools.Count > 0)
        {
            var hu = Text(dc, ams.Title, St.AmsTitle, P, y, new() { W = W - 60 }).Used;
            if (ams.Humidity is { } humidity)
            {
                var w = Text(dc, humidity, St.AmsHum, P + W, y, new() { Align = TextAlignment.Right }).Width;
                Icon(dc, "drop", P + W - w - 13, y + (hu - 10) / 2, 10, SecondaryIcon, SecondaryAlpha);
            }
            y += hu + 9;
            var spools = m.S.Spools.Take(4).ToList();
            var colW = W / Math.Max(1, spools.Count);
            for (var i = 0; i < spools.Count; i++)
            {
                var sp = spools[i];
                var x = P + i * colW;
                Spool(dc, x + (colW - 36) / 2, y, 36, sp, active: m.Active && sp.Active);
                var my = y + 36 + 3;
                var mu = Text(dc, sp.Name, St.SpoolMat, x + colW / 2, my, new() { Align = TextAlignment.Center, W = colW - 4 }).Used;
                Text(dc, sp.Amount, St.SpoolAmt, x + colW / 2, my + mu + 3, new() { Align = TextAlignment.Center, W = colW - 4 });
            }
        }

        // The most severe issue, else the last job.
        if (m.S.Issue is { } issue)
        {
            const double tw = W - 13 - 6;
            var used = Text(null, issue.Text, St.Foot, P + 19, 0, new() { W = tw, Lines = 2 }).Used;
            var fy = bottom - used;
            Text(dc, issue.Text, St.Foot, P + 19, fy, new() { W = tw, Lines = 2 });
            Icon(dc, "warn", P, fy + 1, 13, Hex(WidgetPalette.Paused));
        }
        else if (m.S.LastJob is { } job)
        {
            const double fy = bottom - 11.5 * 1.25;
            var rw = Text(dc, job.Ago, St.FootRight, P + W, fy, new() { Align = TextAlignment.Right }).Width + 8;
            Text(dc, job.Name, St.Foot, P + 19, fy, new() { W = W - 19 - rw });
            var ok = job.Outcome == "finished";
            Icon(dc, ok ? "okCircle" : "xCircle", P, fy + 0.5, 13, Hex(ok ? WidgetPalette.Finished : WidgetPalette.Failed));
        }
    }

    // MARK: Text (View:text)

    /// <summary>A text style of the skins (BB.styles): CSS px, weight, case, line height; numbers in Nunito.</summary>
    private sealed record TextStyle(bool Number, double Px, int Weight, bool Upper = false, bool Secondary = false, double Lh = 1.25, double? PercentPx = null);

    private static class St
    {
        public static readonly TextStyle Cap = new(false, 10.5, 700, Upper: true);
        public static readonly TextStyle CapTwo = new(false, 10.5, 700, Upper: true, Lh: 1.2);
        public static readonly TextStyle Big = new(true, 20, 600, Lh: 1.15);
        public static readonly TextStyle BigAgo = new(true, 18, 600, Lh: 1.15);
        public static readonly TextStyle Sub = new(false, 11, 400, Secondary: true);
        public static readonly TextStyle SubNum = new(true, 11, 600, Secondary: true);
        public static readonly TextStyle Reason = new(false, 13, 600, Lh: 1.2);
        public static readonly TextStyle NoLive = new(false, 12.5, 600, Lh: 1.2);
        public static readonly TextStyle TempRow = new(true, 14, 600);
        public static readonly TextStyle SpoolLine = new(false, 11, 500, Secondary: true);
        public static readonly TextStyle FootTitle = new(false, 13, 600);
        public static readonly TextStyle FootDetail = new(true, 11, 600, Secondary: true);
        public static readonly TextStyle FootLine = new(false, 11, 400, Secondary: true);
        public static readonly TextStyle DialPct = new(true, 18, 700, Lh: 1.1, PercentPx: 9.5);
        public static readonly TextStyle Name = new(false, 12, 600, Secondary: true);
        public static readonly TextStyle Status = new(false, 11.5, 600);
        public static readonly TextStyle Headline = new(false, 16, 600);
        public static readonly TextStyle Pct = new(true, 32, 600, Lh: 1.08, PercentPx: 16);
        public static readonly TextStyle Layers = new(true, 11.5, 600, Secondary: true);
        public static readonly TextStyle Sline = new(true, 11.5, 600);
        public static readonly TextStyle SlineRight = new(true, 11.5, 600, Secondary: true);
        public static readonly TextStyle Line2 = new(false, 12, 400, Secondary: true);
        public static readonly TextStyle TempInline = new(true, 11.5, 600);
        public static readonly TextStyle TileLabel = new(false, 11, 500, Secondary: true);
        public static readonly TextStyle TileValue = new(true, 19, 600, Lh: 1.15);
        public static readonly TextStyle TileTarget = new(true, 10.5, 600, Lh: 1.15);
        public static readonly TextStyle AmsTitle = new(false, 10.5, 700, Upper: true, Secondary: true);
        public static readonly TextStyle AmsHum = new(false, 11, 500, Secondary: true);
        public static readonly TextStyle SpoolMat = new(false, 11, 600);
        public static readonly TextStyle SpoolAmt = new(true, 10.5, 600, Secondary: true);
        public static readonly TextStyle Foot = new(false, 11.5, 500);
        public static readonly TextStyle FootRight = new(false, 11.5, 500, Secondary: true);
        public static readonly TextStyle Info = new(false, 12, 400, Secondary: true, Lh: 1.35);
    }

    private readonly record struct TextOpt(double? W = null, int Lines = 1, TextAlignment Align = TextAlignment.Left, Color? Color = null,
        double? Fit = null, bool FitWords = false)
    {
        public TextOpt() : this(null) { }
    }

    /// <summary>Places a text, or only measures it when <paramref name="dc"/> is null. With a width it trims with an
    /// ellipsis (or wraps, up to <c>Lines</c>); <c>Fit</c> shrinks it to fit, down to that scale. Right and Center
    /// alignment hang from <paramref name="x"/>. Returns the height it takes, its natural width and its lines.</summary>
    private (double Used, double Width, int Lines) Text(DrawingContext? dc, string text, TextStyle st, double x, double y, TextOpt o)
    {
        if (string.IsNullOrEmpty(text)) return (st.Px * st.Lh, 0, 1);
        var color = o.Color ?? (st.Secondary ? SecondaryText : PrimaryText);
        var scale = 1.0;
        var natural = Format(text, st, 1, color).WidthIncludingTrailingWhitespace;
        if (o.Fit is { } fit && o.W is { } fitWidth)
        {
            var need = o.FitWords
                ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => Format(word, st, 1, color).Width).DefaultIfEmpty(0).Max()
                : natural;
            if (need > fitWidth)
            {
                scale = Math.Max(fit, Math.Floor(fitWidth / need * 20) / 20);
                natural = Format(text, st, scale, color).WidthIncludingTrailingWhitespace;
            }
        }
        var ft = Format(text, st, scale, color);
        var lh = st.Px * scale * st.Lh;
        ft.LineHeight = lh;
        var lines = 1;
        if (o.W is { } w)
        {
            ft.MaxTextWidth = Math.Max(1, w);
            ft.MaxLineCount = Math.Max(1, o.Lines);
            ft.Trimming = TextTrimming.CharacterEllipsis;
            ft.TextAlignment = o.Align;
            lines = Math.Clamp((int)Math.Round(ft.Height / lh), 1, Math.Max(1, o.Lines));
        }
        if (dc is not null)
        {
            var box = o.W ?? natural;
            var left = o.Align switch
            {
                TextAlignment.Right => x - box,
                TextAlignment.Center => x - box / 2,
                _ => x,
            };
            dc.DrawText(ft, new Point(left, y));
        }
        return (lines * lh, natural, lines);
    }

    private double Measure(string text, TextStyle st) => Format(text, st, 1, PrimaryText).WidthIncludingTrailingWhitespace;

    private FormattedText Format(string text, TextStyle st, double scale, Color color)
    {
        var shown = st.Upper ? text.ToUpper(CultureInfo.CurrentCulture) : text;
        var face = new Typeface(st.Number ? Fonts.Number : Fonts.Text, FontStyles.Normal, FontWeight.FromOpenTypeWeight(st.Weight), FontStretches.Normal);
        var ft = new FormattedText(shown, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, st.Px * scale, Brush(color), _ppd);
        if (st.PercentPx is { } pct)
        {
            // "64%": the sign smaller and secondary, as the skins' InlineSetting does.
            for (var i = shown.IndexOf('%'); i >= 0; i = shown.IndexOf('%', i + 1))
            {
                ft.SetFontSize(pct * scale, i, 1);
                ft.SetForegroundBrush(Brush(SecondaryText), i, 1);
            }
        }
        return ft;
    }

    /// <summary>Segoe UI for words; Nunito (bundled with the skins, OFL) for numbers, else Segoe UI Variable Display.</summary>
    private static class Fonts
    {
        public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Number = LoadNumber();

        private static FontFamily LoadNumber()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Skins", "BambuBar", "@Resources", "Fonts");
            if (File.Exists(Path.Combine(folder, "Nunito-SemiBold.ttf")))
                return new FontFamily(new Uri(folder + Path.DirectorySeparatorChar), "./#Nunito, Segoe UI Variable Display, Segoe UI");
            return new FontFamily("Segoe UI Variable Display, Segoe UI");
        }
    }

    // MARK: Shapes

    private void Art(DrawingContext dc, double x, double y, double w, double h)
    {
        if (_scene is not { } scene || ArtImage.For(scene, w, h) is not { } image) return;
        // The art is rendered to fit the slot, keeping its aspect.
        var scale = Math.Min(w / image.Width, h / image.Height);
        var (iw, ih) = (image.Width * scale, image.Height * scale);
        dc.DrawImage(image, new Rect(x + (w - iw) / 2, y + (h - ih) / 2, iw, ih));
    }

    /// <summary>The progress ring (BB.ringOptions): track, two glow strokes and the stroke, its gradient running from
    /// the top to the end of the arc, 55 % → 100 %.</summary>
    private static void Ring(DrawingContext dc, double cx, double cy, double d, double lw, double f, Color tint)
    {
        var r = (d - lw) / 2;
        var c = new Point(cx, cy);
        dc.DrawEllipse(null, new Pen(Brush(Track), lw), c, r, r);
        if (f <= 0.001) return;
        var full = f >= 0.999;
        Geometry shape = full ? new EllipseGeometry(c, r, r) : Arc(cx, cy, r, f, 0);
        dc.DrawGeometry(null, RoundPen(WithAlpha(tint, 0.08), lw + 5, !full), shape);
        dc.DrawGeometry(null, RoundPen(WithAlpha(tint, 0.14), lw + 2.5, !full), shape);
        var e = Math.Clamp(f, 0.08, 0.5) * 2 * Math.PI;
        var gradient = new LinearGradientBrush(WithAlpha(tint, 0.55), tint, new Point(cx, cy - r), new Point(cx + r * Math.Sin(e), cy - r * Math.Cos(e)))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        dc.DrawGeometry(null, RoundPen(gradient, lw, !full), shape);
    }

    /// <summary>"Connecting": a 22 % arc around a faint track.</summary>
    private void Spinner(DrawingContext dc, double cx, double cy, double d, double lw, Color color)
    {
        var r = (d - lw) / 2;
        dc.DrawEllipse(null, new Pen(Brush(Color.FromArgb(0x14, 255, 255, 255)), lw), new Point(cx, cy), r, r);
        dc.DrawGeometry(null, RoundPen(color, lw, true), Arc(cx, cy, r, 0.22, SpinAngle));
    }

    /// <summary>The progress bar (BB.barOptions), drawn from (x, y) = 2 px up and left of the track.</summary>
    private static void Bar(DrawingContext dc, double x, double y, double w, double f, Color tint)
    {
        dc.DrawRoundedRectangle(Brush(Color.FromArgb(0x17, 255, 255, 255)), null, new Rect(x + 2, y + 2, w, 6), 3, 3);
        if (f <= 0.001) return;
        var fw = Math.Max(6, f * w);
        dc.DrawRoundedRectangle(Brush(WithAlpha(tint, 0.2)), null, new Rect(x, y, fw + 4, 10), 5, 5);
        dc.DrawRoundedRectangle(new LinearGradientBrush(WithAlpha(tint, 0.75), tint, 0), null, new Rect(x + 2, y + 2, fw, 6), 3, 3);
        dc.DrawRoundedRectangle(Brush(Color.FromArgb(0x47, 255, 255, 255)), null, new Rect(x + 4.4, y + 3, Math.Max(0, fw - 4.8), 2.1), 1, 1);
    }

    /// <summary>A spool swatch (BB.spoolOptions): track, amount-left arc (accent when active), the filament colour
    /// (a diagonal gradient for multicolour spools), gloss, rim and hub; a dashed ring for an empty slot.</summary>
    private static void Spool(DrawingContext dc, double x, double y, double d, WidgetState.SpoolView sp, bool active)
    {
        var rw = Math.Max(2, d * 0.075);
        var gap = Math.Max(1.5, d * 0.06);
        var c = new Point(x + d / 2, y + d / 2);
        var r = d / 2 - rw / 2;
        var ir = d / 2 - rw - gap;
        dc.DrawEllipse(null, new Pen(Brush(Track), rw), c, r, r);
        var loaded = !sp.Empty;
        var arc = active ? Hex(WidgetPalette.Accent) : Color.FromArgb(0x8C, 255, 255, 255);
        if (loaded && sp.Fraction is { } fraction)
        {
            if (fraction > 0.001)
            {
                var full = fraction >= 0.999;
                Geometry g = full ? new EllipseGeometry(c, r, r) : Arc(c.X, c.Y, r, Math.Min(1, fraction), 0);
                dc.DrawGeometry(null, RoundPen(arc, rw, !full), g);
            }
        }
        else if (active)
        {
            dc.DrawEllipse(null, new Pen(Brush(arc), rw), c, r, r);
        }
        if (!loaded)
        {
            var dashed = new Pen(Brush(Color.FromArgb(0x38, 255, 255, 255)), 1) { DashStyle = new DashStyle([2.5, 2.5], 0) };
            dc.DrawEllipse(null, dashed, c, ir - 0.5, ir - 0.5);
            return;
        }
        Brush fill;
        if (sp.Colors.Count > 1)
        {
            var stops = new GradientStopCollection(sp.Colors.Select((col, i) => new GradientStop(Hex(col), i / (double)(sp.Colors.Count - 1))));
            fill = new LinearGradientBrush(stops, 45);
        }
        else
        {
            fill = Brush(Hex(sp.Color));
        }
        dc.DrawEllipse(fill, null, c, ir, ir);
        var gloss = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x4D, 255, 255, 255), 0), new(Color.FromArgb(0, 255, 255, 255), 0.5), new(Color.FromArgb(0x38, 0, 0, 0), 1),
        }, 45);
        dc.DrawEllipse(gloss, null, c, ir, ir);
        var light = Luminance(sp.Color) > 0.85;
        dc.DrawEllipse(null, new Pen(Brush(Color.FromArgb((byte)(light ? 0x2E : 0x0F), 255, 255, 255)), 0.75), c, ir - 0.375, ir - 0.375);
        dc.DrawEllipse(Brush(Color.FromArgb(0x8C, 0, 0, 0)), new Pen(Brush(Color.FromArgb(0x2E, 255, 255, 255)), 0.5), c, d * 0.1, d * 0.1);
    }

    /// <summary>The status dot: 6 px with a 2 px halo at 18 %.</summary>
    private static void Dot(DrawingContext dc, double cx, double cy, Color color)
    {
        dc.DrawEllipse(Brush(WithAlpha(color, 0.18)), null, new Point(cx, cy), 5, 5);
        dc.DrawEllipse(Brush(color), null, new Point(cx, cy), 3, 3);
    }

    private static StreamGeometry Arc(double cx, double cy, double r, double f, double startDeg)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + f * 360) * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx + r * Math.Sin(a0), cy - r * Math.Cos(a0)), false, false);
            ctx.ArcTo(new Point(cx + r * Math.Sin(a1), cy - r * Math.Cos(a1)), new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static Pen RoundPen(Color color, double width, bool caps) => RoundPen(Brush(color), width, caps);

    private static Pen RoundPen(Brush brush, double width, bool caps)
    {
        var pen = new Pen(brush, width);
        if (caps) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        return pen;
    }

    // MARK: Icons (the skins' SVGs: Fluent UI System Icons, MIT, and three of Bambu Bar's own)

    private sealed record Glyph(Geometry Shape, double Box, double? Stroke = null);

    private static readonly Dictionary<string, Glyph> Glyphs = new()
    {
        ["nozzle"] = new(G("M4.11 6.19C4.11 6.19 3.93 6.44 3.87 6.55C3.73 6.77 3.56 7.08 3.40 7.47C3.09 8.25 2.85 9.36 3.12 10.61C3.39 11.80 3.90 12.90 4.78 13.71C5.67 14.53 6.90 15 8.50 15C10.12 15 11.39 14.46 12.25 13.53C13.10 12.60 13.50 11.35 13.50 10C13.50 9.14 13.22 8.31 12.83 7.54C12.47 6.80 12.00 6.07 11.55 5.39L11.48 5.29C11.01 4.56 10.58 3.89 10.29 3.26C10.01 2.62 9.90 2.08 9.99 1.60C10.02 1.45 9.98 1.30 9.89 1.18C9.79 1.07 9.65 1 9.50 1C9.08 1 8.38 1.15 7.71 1.49C7.04 1.84 6.33 2.43 6.03 3.34C5.64 4.51 6.09 5.83 6.47 6.66C6.64 7.04 6.49 7.45 6.21 7.59C5.94 7.72 5.62 7.61 5.48 7.35L4.95 6.28C4.87 6.12 4.72 6.02 4.55 6.00C4.38 5.98 4.22 6.06 4.11 6.19Z"), 16),
        ["bed"] = new(G("M4.6 2.2C3.5 3.5 5.7 4.7 4.6 6S5.7 8.5 4.6 9.8M8 2.2C6.9 3.5 9.1 4.7 8 6S9.1 8.5 8 9.8M11.4 2.2C10.3 3.5 12.5 4.7 11.4 6S12.5 8.5 11.4 9.8M2.4 13.2H13.6"), 16, 1.6),
        ["chamber"] = new(G("M4.5 3.5C4.5 1.84 5.84 0.5 7.5 0.5C9.16 0.5 10.5 1.84 10.5 3.5L10.5 8.85C11.12 9.56 11.5 10.49 11.5 11.5C11.5 13.71 9.71 15.5 7.5 15.5C5.29 15.5 3.5 13.71 3.5 11.5C3.5 10.49 3.88 9.56 4.5 8.85V3.5ZM7.5 2C6.67 2 6 2.67 6 3.5V9.47L5.77 9.70C5.29 10.15 5 10.79 5 11.5C5 12.88 6.12 14 7.5 14C8.88 14 10 12.88 10 11.5C10 10.79 9.71 10.15 9.23 9.70L9 9.47L9 3.5C9 2.67 8.33 2 7.5 2ZM8 6C8 5.72 7.78 5.5 7.5 5.5C7.22 5.5 7 5.72 7 6V10.09C6.42 10.29 6 10.85 6 11.5C6 12.33 6.67 13 7.5 13C8.33 13 9 12.33 9 11.5C9 10.85 8.58 10.29 8 10.09V6Z"), 16),
        ["fan"] = new(Frozen(Fan()), 16),
        ["drop"] = new(G("M7.65 1.15C7.84 0.95 8.16 0.95 8.35 1.15C8.62 1.41 9.76 2.60 10.85 4.15C11.92 5.68 13 7.67 13 9.5C13 11.28 12.42 12.66 11.48 13.61C10.55 14.54 9.29 15 8 15C6.71 15 5.45 14.54 4.52 13.61C3.58 12.66 3 11.28 3 9.5C3 7.67 4.08 5.68 5.15 4.15C6.24 2.60 7.38 1.41 7.65 1.15Z"), 16),
        ["check"] = new(G("M8.5 16.59L4.71 12.79C4.32 12.40 3.68 12.40 3.29 12.79C2.90 13.18 2.90 13.82 3.29 14.21L7.79 18.71C8.18 19.10 8.82 19.10 9.21 18.71L20.21 7.71C20.60 7.32 20.60 6.68 20.21 6.29C19.82 5.90 19.18 5.90 18.79 6.29L8.5 16.59Z"), 24),
        ["cross"] = new(G("M4.29 4.29C4.68 3.90 5.32 3.90 5.71 4.29L12 10.59L18.29 4.29C18.68 3.90 19.32 3.90 19.71 4.29C20.10 4.68 20.10 5.32 19.71 5.71L13.42 12L19.71 18.29C20.10 18.68 20.10 19.32 19.71 19.71C19.32 20.10 18.68 20.10 18.29 19.71L12 13.42L5.71 19.71C5.32 20.10 4.68 20.10 4.29 19.71C3.90 19.32 3.90 18.68 4.29 18.29L10.59 12L4.29 5.71C3.90 5.32 3.90 4.68 4.29 4.29Z"), 24),
        ["bang"] = new(G("M12 2C9.86 2 8.13 3.74 8.13 5.88C8.13 8.80 9.33 12.43 9.94 14.08C10.26 14.95 11.10 15.50 12 15.50C12.91 15.50 13.74 14.96 14.06 14.08C14.67 12.44 15.88 8.83 15.88 5.88C15.88 3.74 14.14 2 12 2ZM12 17C10.62 17 9.5 18.12 9.5 19.50C9.5 20.88 10.62 22 12 22C13.38 22 14.50 20.88 14.50 19.50C14.50 18.12 13.38 17 12 17Z"), 24),
        ["pause"] = new(G("M3.75 2C2.78 2 2 2.78 2 3.75V12.25C2 13.22 2.78 14 3.75 14H5.25C6.22 14 7 13.22 7 12.25V3.75C7 2.78 6.22 2 5.25 2H3.75ZM10.75 2C9.78 2 9 2.78 9 3.75V12.25C9 13.22 9.78 14 10.75 14H12.25C13.22 14 14 13.22 14 12.25V3.75C14 2.78 13.22 2 12.25 2H10.75Z"), 16),
        ["warn"] = new(G("M5.82 2.28C6.78 0.57 9.22 0.57 10.18 2.28L14.68 10.28C15.61 11.95 14.41 14 12.50 14H3.50C1.59 14 0.39 11.95 1.32 10.28L5.82 2.28ZM8 9.5C7.59 9.5 7.25 9.84 7.25 10.25C7.25 10.66 7.59 11 8 11C8.41 11 8.75 10.66 8.75 10.25C8.75 9.84 8.41 9.5 8 9.5ZM8 5C7.72 5 7.5 5.22 7.5 5.5V8C7.5 8.28 7.72 8.5 8 8.5C8.28 8.5 8.5 8.28 8.5 8V5.5C8.5 5.22 8.28 5 8 5Z"), 16),
        ["okCircle"] = new(G("M1 8C1 4.13 4.13 1 8 1C11.87 1 15 4.13 15 8C15 11.87 11.87 15 8 15C4.13 15 1 11.87 1 8ZM10.85 6.85C11.05 6.66 11.05 6.34 10.85 6.15C10.66 5.95 10.34 5.95 10.15 6.15L7.25 9.04L5.85 7.65C5.66 7.45 5.34 7.45 5.15 7.65C4.95 7.84 4.95 8.16 5.15 8.35L6.90 10.10C7.09 10.30 7.41 10.30 7.60 10.10L10.85 6.85Z"), 16),
        ["xCircle"] = new(G("M8 15C11.87 15 15 11.87 15 8C15 4.13 11.87 1 8 1C4.13 1 1 4.13 1 8C1 11.87 4.13 15 8 15ZM6.35 5.65L8 7.29L9.65 5.65C9.84 5.45 10.16 5.45 10.35 5.65C10.55 5.84 10.55 6.16 10.35 6.35L8.71 8L10.35 9.65C10.55 9.84 10.55 10.16 10.35 10.35C10.16 10.55 9.84 10.55 9.65 10.35L8 8.71L6.35 10.35C6.16 10.55 5.84 10.55 5.65 10.35C5.45 10.16 5.45 9.84 5.65 9.65L7.29 8L5.65 6.35C5.45 6.16 5.45 5.84 5.65 5.65C5.84 5.45 6.16 5.45 6.35 5.65Z"), 16),
        ["key"] = new(G("M7.5 11.5V12C7.5 12.28 7.28 12.5 7 12.5H6V13C6 13.55 5.55 14 5 14H3C2.45 14 2 13.55 2 13V11.71C2 11.44 2.11 11.19 2.29 11L6.16 7.13C6.06 6.77 6 6.39 6 6C6 3.79 7.79 2 10 2C12.21 2 14 3.79 14 6C14 8.21 12.21 10 10 10H9V11C9 11.28 8.78 11.5 8.5 11.5H7.5ZM11 6C11.55 6 12 5.55 12 5C12 4.45 11.55 4 11 4C10.45 4 10 4.45 10 5C10 5.55 10.45 6 11 6Z"), 16),
        ["wifiOff"] = new(G("M2.85 2.15C2.66 1.95 2.34 1.95 2.15 2.15C1.95 2.34 1.95 2.66 2.15 2.85L5.18 5.88C4.49 6.29 3.83 6.79 3.24 7.38C2.85 7.78 2.46 8.24 2.12 8.74C1.96 8.97 2.02 9.28 2.24 9.44C2.47 9.60 2.78 9.54 2.94 9.31C3.25 8.86 3.60 8.44 3.95 8.09C4.54 7.49 5.21 7.00 5.91 6.62L7.35 8.05C6.67 8.36 6.03 8.80 5.48 9.36C4.99 9.85 4.59 10.40 4.28 11.00C4.16 11.25 4.26 11.55 4.50 11.67C4.75 11.80 5.05 11.70 5.17 11.45C5.43 10.94 5.77 10.48 6.18 10.06C6.75 9.50 7.41 9.08 8.11 8.82L9.97 10.67C8.96 10.69 7.95 11.08 7.18 11.85C6.83 12.21 6.55 12.63 6.34 13.10C6.23 13.35 6.35 13.65 6.60 13.76C6.86 13.87 7.15 13.75 7.26 13.50C7.42 13.14 7.63 12.82 7.89 12.56C8.78 11.67 10.10 11.45 11.19 11.90L12.84 13.55C12.89 13.63 12.96 13.70 13.04 13.75L17.15 17.85C17.34 18.05 17.66 18.05 17.85 17.85C18.05 17.66 18.05 17.34 17.85 17.15L2.85 2.15ZM9.60 7.48L10.62 8.50C11.81 8.63 12.97 9.15 13.89 10.06C14.29 10.46 14.64 10.95 14.90 11.47C15.03 11.71 15.33 11.81 15.57 11.69C15.82 11.56 15.92 11.26 15.79 11.01C15.48 10.40 15.07 9.83 14.60 9.36C13.23 7.99 11.40 7.36 9.60 7.48ZM7.14 5.01L7.95 5.82C10.78 5.12 13.91 5.87 16.13 8.09C16.49 8.46 16.84 8.88 17.14 9.32C17.30 9.54 17.61 9.60 17.84 9.44C18.07 9.29 18.12 8.98 17.97 8.75C17.63 8.26 17.24 7.79 16.83 7.38C14.21 4.76 10.45 3.97 7.14 5.01ZM10.96 13.79C11.47 14.29 11.47 15.12 10.96 15.62C10.46 16.13 9.63 16.13 9.13 15.62C8.62 15.12 8.62 14.29 9.13 13.79C9.63 13.28 10.46 13.28 10.96 13.79Z"), 20),
        ["printer"] = new(Frozen(new GeometryGroup
        {
            Children = { new RectangleGeometry(new Rect(2.6, 1.8, 10.8, 12.4), 2, 2), new RectangleGeometry(new Rect(4.7, 5, 6.6, 6), 0.8, 0.8) },
        }), 16, 1.4),
    };

    private static Geometry G(string path) => Frozen(Geometry.Parse(path));

    private static Geometry Frozen(Geometry g)
    {
        g.Freeze();
        return g;
    }

    private static GeometryGroup Fan()
    {
        var group = new GeometryGroup { Children = { new EllipseGeometry(new Point(8, 8), 1.5, 1.5) } };
        for (var i = 0; i < 3; i++)
        {
            var blade = Geometry.Parse("M8.3 6.6C7.1 4.9 7.2 2.2 9.1 2C10.8 1.9 11.2 4.1 9.7 5.6C9.2 6.1 8.8 6.4 8.3 6.6Z").Clone();
            blade.Transform = new RotateTransform(i * 120, 8, 8);
            group.Children.Add(blade);
        }
        return group;
    }

    private static void Icon(DrawingContext dc, string kind, double x, double y, double size, Color color, double alpha = 1)
    {
        if (!Glyphs.TryGetValue(kind, out var glyph)) return;
        var brush = Brush(WithAlpha(color, alpha * color.A / 255.0));
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(size / glyph.Box, size / glyph.Box));
        if (glyph.Stroke is { } stroke)
        {
            var pen = new Pen(brush, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, glyph.Shape);
        }
        else
        {
            dc.DrawGeometry(brush, null, glyph.Shape);
        }
        dc.Pop();
        dc.Pop();
    }

    // MARK: Colours (BB.C)

    private static readonly Color PrimaryText = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5);
    private static readonly Color SecondaryText = Color.FromArgb(0x9E, 0xEB, 0xEB, 0xF5);
    private static readonly Color SecondaryIcon = Color.FromRgb(0xEB, 0xEB, 0xF5);
    private const double SecondaryAlpha = 0.62;
    private static readonly Color Tile = Color.FromArgb(0x12, 255, 255, 255);
    private static readonly Color Track = Color.FromArgb(0x1A, 255, 255, 255);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>"#RRGGBB" from the state file; anything else reads as the idle grey (as BB.rgba does).</summary>
    private static Color Hex(string? hex)
    {
        var clean = (hex ?? "").TrimStart('#');
        return clean.Length >= 6 && uint.TryParse(clean[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromRgb(0x9E, 0xA8, 0xB3);
    }

    private static double Luminance(string hex)
    {
        var c = Hex(hex);
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
    }

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class CardPeer(WidgetCard owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(WidgetCard);
    }
}
