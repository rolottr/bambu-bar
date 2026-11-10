using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace BambuBar.Tray;

/// <summary>
/// The tray menu drawn like Windows 11's own context menus instead of WinForms' Windows 7 look: light or dark to match
/// the taskbar, a rounded highlight inset from the edges, hairline separators, roomy rows, Segoe UI Variable, and the
/// window's corners rounded by DWM (Windows 11; Windows 10 keeps square ones).
/// </summary>
internal sealed partial class MenuRenderer : Forms.ToolStripRenderer
{
    private readonly bool _dark;
    private Color Back => _dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9);
    private Color Text => _dark ? Color.White : Color.FromArgb(0x1A, 0x1A, 0x1A);
    private Color Dim => _dark ? Color.FromArgb(0x9A, 0x9A, 0x9A) : Color.FromArgb(0x8A, 0x8A, 0x8A);
    private Color Hover => _dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0E, 0, 0, 0);
    private Color Line => _dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0);
    private Color Edge => _dark ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x20, 0, 0, 0);

    public MenuRenderer(bool dark) => _dark = dark;

    /// <summary>Dresses a menu (and its submenus as they're added): font, padding, rounded corners.</summary>
    public static void Apply(Forms.ToolStripDropDown menu, bool dark)
    {
        menu.Renderer = new MenuRenderer(dark);
        var font = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        menu.Font = font.Name == "Segoe UI Variable Text" ? font : new Font("Segoe UI", 9.5f);
        menu.Padding = new Forms.Padding(4, 6, 4, 6);
        menu.HandleCreated += (_, _) => Round(menu.Handle);
        if (menu.IsHandleCreated) Round(menu.Handle);
    }

    /// <summary>Gives every item its row height and each submenu the same dress.</summary>
    public static void Dress(Forms.ToolStripItemCollection items, bool dark)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            if (item is Forms.ToolStripSeparator) { item.Margin = new Forms.Padding(0, 4, 0, 4); continue; }
            item.Padding = new Forms.Padding(4, 6, 12, 6);
            if (item is Forms.ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                Apply(parent.DropDown, dark);
                Dress(parent.DropDownItems, dark);
            }
        }
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) => e.Graphics.Clear(Back);

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Edge);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = RoundedRect(r, 4);
        using var brush = new SolidBrush(Hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Dim;
        if (e.Item is Forms.ToolStripMenuItem && e.TextFormat.HasFlag(Forms.TextFormatFlags.Right)) e.TextColor = Dim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Line);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Centred on the row (the image rectangle sits a little low with the rows' padding).
        var r = e.ImageRectangle;
        var cx = r.X + r.Width / 2f;
        var cy = TextMiddle(e.Item);
        using var pen = new Pen(Text, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4)]);
    }

    /// <summary>A thin chevron, centred on the row.</summary>
    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var x = e.ArrowRectangle.X + e.ArrowRectangle.Width / 2f;
        var y = e.Item is { } item ? TextMiddle(item) : e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f;
        using var pen = new Pen(Dim, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, [new PointF(x - 1.5f, y - 3.5f), new PointF(x + 2, y), new PointF(x - 1.5f, y + 3.5f)]);
    }

    /// <summary>The middle of the item's letters, where marks beside them line up: the row's middle less the room the
    /// font keeps for descenders (about a sixth of its height).</summary>
    private static float TextMiddle(Forms.ToolStripItem item) => item.Height / 2f - item.Font.GetHeight() / 6f + 0.5f;

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL; a no-op before Windows 11.</summary>
    private static void Round(nint window)
    {
        var preference = 3;
        _ = DwmSetWindowAttribute(window, 33, ref preference, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
