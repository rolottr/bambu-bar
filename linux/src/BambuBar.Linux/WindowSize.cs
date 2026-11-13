namespace BambuBar.Linux;

internal static class WindowSize
{
    // Wayland owns placement. Keep the requested size inside the monitor's logical work area,
    // including when a live scale change reduces that area without changing pixel resolution.
    public static void Track(Adw.ApplicationWindow window)
    {
        window.OnMap += (_, _) => Schedule();
        window.OnNotify += (_, e) => { if (e.Pspec.GetName() == "scale-factor") Schedule(); };
        void Schedule() => GLib.Functions.IdleAdd(200, () =>
        {
            if (!window.GetRealized() || window.GetSurface() is not { } surface) return false;
            var monitor = Gdk.Display.GetDefault()?.GetMonitorAtSurface(surface);
            if (monitor is null) return false;
            monitor.GetGeometry(out var area);
            using (area)
            {
                var maxWidth = Math.Max(320, area.Width - 48);
                var maxHeight = Math.Max(300, area.Height - 64);
                var width = window.GetWidth(); var height = window.GetHeight();
                if (width <= 0 || height <= 0) window.GetDefaultSize(out width, out height);
                if (width > maxWidth || height > maxHeight)
                    window.SetDefaultSize(Math.Min(width, maxWidth), Math.Min(height, maxHeight));
            }
            return false;
        });
    }
}
