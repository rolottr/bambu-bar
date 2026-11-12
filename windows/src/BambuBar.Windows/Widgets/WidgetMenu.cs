using System.Runtime.InteropServices;
using System.Windows.Interop;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Theme;

namespace BambuBar.Widgets;

/// <summary>
/// A widget's right-click menu (P2.5). The skins run <c>BambuBar.exe --widget-menu "BambuBar\W3"</c> on right-click
/// (which also keeps Rainmeter's own menu away); the running app shows this at the cursor: Printer ▸ (the tray's, or
/// one of them), Size ▸, Keep on Top, Remove. A real Win32 popup menu, so it looks and behaves like every other context
/// menu on the desktop (rounded and shadowed on Windows 11, keyboard, light-dismiss), dark when Windows' apps are.
/// </summary>
public static partial class WidgetMenu
{
    private const int CommandTray = 100, CommandPrinter = 101, CommandSize = 200, CommandOnTop = 300, CommandRemove = 400;

    /// <summary>The config a <c>--widget-menu</c> launch names, if any.</summary>
    public static string? Requested(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--widget-menu");
        return i >= 0 && i + 1 < args.Count && args[i + 1].Length > 0 ? args[i + 1] : null;
    }

    public static void Show(WidgetHost host, PrinterStore store, string config)
    {
        if (host.Find(config) is not { } widget) return;
        var printers = store.Sessions.Select(s => (s.Id, s.Config.Name)).ToList();
        var sizes = Enum.GetValues<WidgetHost.Size>();
        var onTop = host.IsOnTop(widget);

        var menu = CreatePopupMenu();
        try
        {
            var printerMenu = CreatePopupMenu();
            Append(printerMenu, CommandTray, Loc.T("Printer in the tray"), widget.Serial is null);
            if (printers.Count > 0) AppendMenu(printerMenu, MfSeparator, 0, null);
            for (var i = 0; i < printers.Count; i++)
                Append(printerMenu, CommandPrinter + i, Escape(printers[i].Name), string.Equals(widget.Serial, printers[i].Id, StringComparison.OrdinalIgnoreCase));
            AppendMenu(menu, MfPopup, printerMenu, Loc.T("Printer"));

            var sizeMenu = CreatePopupMenu();
            for (var i = 0; i < sizes.Length; i++) Append(sizeMenu, CommandSize + i, Title(sizes[i]), widget.Size == sizes[i]);
            AppendMenu(menu, MfPopup, sizeMenu, Loc.T("Size"));

            Append(menu, CommandOnTop, Loc.T("Keep on Top"), onTop);
            AppendMenu(menu, MfSeparator, 0, null);
            Append(menu, CommandRemove, Loc.T("Remove"), false);

            var command = Track(menu);
            switch (command)
            {
                case CommandTray: host.Change(widget, widget.Size, null); break;
                case >= CommandPrinter and < CommandSize when command - CommandPrinter < printers.Count:
                    host.Change(widget, widget.Size, printers[command - CommandPrinter].Id);
                    break;
                case >= CommandSize and < CommandOnTop when command - CommandSize < sizes.Length:
                    host.Change(widget, sizes[command - CommandSize], widget.Serial);
                    break;
                case CommandOnTop: host.KeepOnTop(widget, !onTop); break;
                case CommandRemove: host.Remove(widget); break;
            }
        }
        finally
        {
            // Destroys the submenus with it.
            DestroyMenu(menu);
        }
    }

    public static string Title(WidgetHost.Size size) => size switch
    {
        WidgetHost.Size.Small => Loc.T("Small", "Widget size"),
        WidgetHost.Size.Medium => Loc.T("Medium", "Widget size"),
        _ => Loc.T("Large", "Widget size"),
    };

    /// <summary>A printer named "R&amp;D" would underline the D: menus read &amp; as the access key.</summary>
    private static string Escape(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private static void Append(nint menu, int id, string text, bool check) =>
        AppendMenu(menu, MfString | (check ? MfChecked : 0), id, text);

    /// <summary>Shows the menu at the cursor from a hidden owner window that takes the foreground for it (else the menu
    /// wouldn't close when the person clicks elsewhere), and returns the command picked, or 0.</summary>
    private static int Track(nint menu)
    {
        GetCursorPos(out var cursor);
        var parameters = new HwndSourceParameters("Bambu Bar widget menu")
        {
            WindowStyle = unchecked((int)WsPopup), ExtendedWindowStyle = WsExToolWindow, Width = 0, Height = 0,
            PositionX = cursor.X, PositionY = cursor.Y,
        };
        using var owner = new HwndSource(parameters);
        UseMenuTheme(AppTheme.Current == ThemeVariant.Dark);
        SetForegroundWindow(owner.Handle);
        var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify, cursor.X, cursor.Y, owner.Handle, 0);
        // The documented follow-up, so the next click after the menu isn't swallowed.
        PostMessage(owner.Handle, 0, 0, 0);
        return command;
    }

    /// <summary>Dark or light menus for this process (uxtheme's SetPreferredAppMode and FlushMenuThemes, by ordinal:
    /// what Windows' own apps use, undocumented, present since 1903). Skipped where they aren't.</summary>
    private static unsafe void UseMenuTheme(bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
        try
        {
            if (!NativeLibrary.TryLoad("uxtheme.dll", out var uxtheme)) return;
            var setMode = GetProcAddress(uxtheme, 135);
            var flush = GetProcAddress(uxtheme, 136);
            if (setMode == 0 || flush == 0) return;
            // 2 = ForceDark, 3 = ForceLight.
            ((delegate* unmanaged[Stdcall]<int, int>)setMode)(dark ? 2 : 3);
            ((delegate* unmanaged[Stdcall]<void>)flush)();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Light menus.
        }
    }

    private const uint MfString = 0x0, MfChecked = 0x8, MfPopup = 0x10, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmNoNotify = 0x80, TpmReturnCmd = 0x100;
    private const uint WsPopup = 0x80000000;
    private const int WsExToolWindow = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcAddress(nint module, nint ordinal);
}
