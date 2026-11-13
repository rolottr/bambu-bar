using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private static Gtk.Image DetailIcon(string icon, int size = 13, string color = "#adadad")
    {
        var path = icon switch
        {
            "temperature-symbolic" => "M10 3a2 2 0 0 1 4 0v10a4 4 0 1 1-4 0V3m2 4v10m0 0h.01",
            "door-symbolic" => "M5 21V3h14v18H5m4 0V3m3 10h.01",
            "speedometer-symbolic" => "M4 18a9 9 0 1 1 16 0M12 13l4-6M6 18h12",
            "sensors-fan-symbolic" => "M12 12c-8-9 6-13 3-3M12 12c9-8 13 6 3 3M12 12c8 9-6 13-3 3M12 12c-9 8-13-6-3-3",
            "application-x-firmware-symbolic" => "M6 6h12v12H6V6m3 3h6v6H9V9M8 2v4m4-4v4m4-4v4M8 18v4m4-4v4m4-4v4M2 8h4m-4 4h4m-4 4h4m12-8h4m-4 4h4m-4 4h4",
            "weather-clear-symbolic" => "M12 2c2 6 6 6 6 12a6 6 0 0 1-12 0c0-3 2-5 3-7 0 4 2 4 3 5 2-3 0-6 0-10",
            "weather-fog-symbolic" => "M5 21V19m7 2v-2m7 2v-2M5 16c-5-5 5-5 0-10m7 10c-5-5 5-5 0-10m7 10c-5-5 5-5 0-10",
            _ => null,
        };
        if (path is null) { var native = Gtk.Image.NewFromIconName(icon); native.SetPixelSize(size); return native; }
        var image = Gtk.Image.New(); image.SetPixelSize(size);
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24'><path d='{path}' fill='none' stroke='{color}' stroke-width='1.7' stroke-linecap='round' stroke-linejoin='round'/></svg>";
        using var bytes = GLib.Bytes.New(System.Text.Encoding.UTF8.GetBytes(svg)); image.SetFromGicon(Gio.BytesIcon.New(bytes)); return image;
    }
    private static Gtk.Label Note(string text)
    { var label = Label(Loc.T(text), "muted", "note"); label.SetWrap(true); return label; }

    private static Gtk.Box SettingsSection(string title, string icon, Gtk.Widget content)
    {
        var group = Column(8); var heading = Row(8); var image = Gtk.Image.NewFromIconName(icon); image.SetPixelSize(14);
        heading.Append(image); heading.Append(Label(Loc.T(title), "heading")); group.Append(heading); group.Append(content); return group;
    }
    private static Gtk.Box SettingRow(string title, Gtk.Widget control)
    {
        var row = Row(12); var label = Label(Loc.T(title)); label.SetHexpand(true); row.Append(label);
        control.SetValign(Gtk.Align.Center); row.Append(control); return row;
    }
    private Gtk.Image PreviewArt(JobState state, double progress, int size = 120)
    {
        var image = Gtk.Image.New(); image.SetPixelSize(size); image.SetSizeRequest(size, size + 12);
        var model = _store.Current?.Config.Model;
        var scene = new PrinterArt.Scene { Shape = model?.Shape ?? PrinterShape.Enclosed, State = state, Progress = progress,
            Filament = "FF9959FF", Spools = ["FF9959FF", "F5E83DFF", "EF333FFF", "181818FF"], LightOn = true };
        var alive = true; _cleanup.Add(() => alive = false);
        _ = Render();
        async Task Render()
        {
            var pixels = await Task.Run(() => PrinterArt.Render(scene, size * 2, (size + 12) * 2));
            if (!alive || _disposed || pixels is null) return;
            using var bytes = GLib.Bytes.New(Png.Encode(pixels)); image.SetFromGicon(Gio.BytesIcon.New(bytes));
        }
        return image;
    }
    private static Gtk.Box SampleTemperatures(bool chamber, bool tiles = true)
    {
        var row = Row(8); row.SetHomogeneous(true);
        foreach (var (title, value, icon, color) in new[] { ("Nozzle", "220°", "weather-clear-symbolic", "nozzle"), ("Bed", "55°", "weather-fog-symbolic", "bed"), ("Chamber", "38°", "temperature-symbolic", "chamber") })
        {
            if (title == "Chamber" && !chamber) continue;
            var heading = Row(5); var glyph = DetailIcon(icon, 12, color == "nozzle" ? "#ff7a4d" : color == "bed" ? "#ffb84d" : "#b8adb8"); heading.Append(glyph);
            heading.Append(Label(tiles ? Loc.T(title) : value, tiles ? "caption" : "heading", "muted"));
            if (tiles) { var tile = Module(heading, Label(value, "metric")); tile.AddCssClass("temperature-card"); row.Append(tile); }
            else row.Append(heading);
        }
        return row;
    }
    private Gtk.Box SampleSpools()
    {
        var card = Module(); var header = Row(); var title = Label("AMS 2 PRO A", "section-title"); title.SetHexpand(true); header.Append(title);
        header.Append(Label("44% RH · 27°", "muted", "caption")); card.Append(header);
        var row = Row(12); row.SetHomogeneous(true);
        var colors = new[] { "FF9959FF", "F5E83DFF", "EF333FFF", "181818FF", "888888FF" };
        for (var i = 0; i < colors.Length; i++)
        {
            var spool = new Spool { Color = colors[i], Material = "PLA" }; var item = Column(3);
            var refresh = new List<Action>(); var ring = SpoolRing(() => spool, 32, refresh); foreach (var update in refresh) update(); ring.SetHalign(Gtk.Align.Center); item.Append(ring);
            var material = Label("PLA", "caption", "heading"); material.SetXalign(.5f); item.Append(material);
            var amount = Label(i == 0 ? "≈ 1 kg" : i == 3 ? "≈ 890 g" : "—", "caption", "muted"); amount.SetXalign(.5f); item.Append(amount); row.Append(item);
        }
        card.Append(row); return card;
    }
    private static Gtk.Widget PreviewControl(string icon, string text, string? css = null)
    {
        var item = Row(7); item.SetHalign(Gtk.Align.Center); var image = DetailIcon(icon, 12); item.Append(image);
        item.Append(Label(Loc.T(text), "caption", "heading")); var box = Module(item); box.AddCssClass("sample-control"); if (css is not null) box.AddCssClass(css); return box;
    }
}
