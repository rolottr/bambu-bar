using BambuBar.Core;

namespace BambuBar.Linux;

internal sealed partial class Shell
{
    private string[]? _pendingCommand;
    private Action? _pendingCompletion;
    public void HandleCommand(string[] args, Action? complete = null)
    {
        Platform.Trace($"command ready={_ready}");
        if (!_ready) { _pendingCommand = args; _pendingCompletion = complete; return; }
        var page = Array.IndexOf(args, "--page");
        if (page >= 0 && page + 1 < args.Length && _pages.Contains(args[page + 1]))
        {
            _nav.SelectRow(_nav.GetRowAtIndex(Array.IndexOf(_pages, args[page + 1])));
            Navigate(args[page + 1]);
        }
        if (args.Contains("--hide")) { HideMain(); _panel?.SetVisible(false); complete?.Invoke(); return; }
        if (args.Contains("--panel")) ShowPanel();
        var capture = Array.IndexOf(args, "--screenshot");
        Platform.Trace($"capture index={capture}");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            GLib.Functions.TimeoutAdd(200, 400, () => { Capture(args[capture + 1], args.Contains("--panel") ? _panel! : _window); complete?.Invoke(); return false; });
            return;
        }
        var snapshot = Array.IndexOf(args, "--snapshot");
        if (snapshot >= 0 && snapshot + 1 < args.Length)
        {
            var scenes = _pages.Where(p => p != "All Printers" || _store.HasMultiplePrinters).ToArray(); var index = 0; Navigate(scenes[0]); Present();
            GLib.Functions.TimeoutAdd(200, 1200, () =>
            {
                Capture(Path.Combine(args[snapshot + 1], "window-" + scenes[index].ToLowerInvariant().Replace(' ', '-') + ".png"), _window);
                index++;
                if (index < scenes.Length) { Navigate(scenes[index]); return true; }
                complete?.Invoke(); if (_ephemeral) _app.Quit(); return false;
            });
            return;
        }
        complete?.Invoke();
    }
    private static void Capture(string path, Adw.ApplicationWindow window)
    {
        try
        {
            Platform.Trace($"capture callback: {window.GetWidth()}x{window.GetHeight()} mapped={window.GetMapped()}");
            using var paintable = Gtk.WidgetPaintable.New(window);
            using var snapshot = Gtk.Snapshot.New();
            paintable.Snapshot(snapshot, window.GetWidth(), window.GetHeight());
            var node = snapshot.ToNode();
            if (node is null) { Platform.Trace("capture: no render node"); return; }
            using var bounds = new Graphene.Rect(); bounds.Init(0, 0, window.GetWidth(), window.GetHeight());
            using var texture = window.GetRenderer()!.RenderTexture(node, bounds);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            texture.SaveToPng(path);
        }
        catch (Exception) { Console.Error.WriteLine("Could not capture the window."); }
    }
}
