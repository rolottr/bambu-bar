namespace BambuBar.Linux;

internal sealed class OmarchyTheme : IDisposable
{
    // Omarchy 4.5 stages files by renaming the theme directory under current.
    // The older config path is optional. Flatpak has read-only access to these parents,
    // not the entire home, all user themes, Hyprland configuration or compositor socket.
    internal static string[] Directories => [Path.Combine(Platform.Home, ".local/state/omarchy/current"), Path.Combine(Platform.Home, ".config/omarchy/current")];
    private readonly Gtk.CssProvider _provider = Gtk.CssProvider.New();
    private readonly Action _changed;
    private readonly uint _timer;
    private string _source = "";
    private int _appearance;
    private bool _enabled;
    public bool? Dark { get; private set; }
    public OmarchyPalette? Palette { get; private set; }
    public OmarchyTheme(Action changed)
    {
        _changed = changed;
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _provider, 602);
        // A bounded two-second read also survives atomic theme swaps and Flatpak bind mounts.
        // No GTK updates or allocations of CSS providers when the contents are unchanged.
        _timer = GLib.Functions.TimeoutAdd(200, 2000, () => { if (_enabled && ReadSource() != _source) _changed(); return true; });
    }
    private static string ReadFile(string path)
    {
        try { return new FileInfo(path).Length <= 65536 ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
    private static string ReadSource()
    {
        foreach (var directory in Directories)
        {
            var colors = ReadFile(Path.Combine(directory, "theme/colors.toml"));
            if (colors.Length > 0) return colors + "\0" + ReadFile(Path.Combine(directory, "theme/shell.toml"));
        }
        return "";
    }
    public void Apply(bool enabled, int appearance)
    {
        var source = enabled ? ReadSource() : "";
        if (_enabled == enabled && _appearance == appearance && _source == source) return;
        _enabled = enabled; _appearance = appearance; _source = source;
        var parts = source.Split('\0');
        var palette = enabled && parts.Length == 2 ? OmarchyPalette.Parse(parts[0], parts[1], appearance) : null;
        Palette = palette;
        Dark = palette?.Dark;
        _provider.LoadFromString(palette?.Css ?? "");
    }
    public void Dispose()
    {
        GLib.Functions.SourceRemove(_timer);
        Gtk.StyleContext.RemoveProviderForDisplay(Gdk.Display.GetDefault()!, _provider);
        _provider.Dispose();
    }
}
