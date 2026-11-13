#!/usr/bin/env python3
"""Host bridge for Bambu Bar popup placement and autostart. No printer data or arbitrary commands."""
import json
import math
import os
import subprocess
from pathlib import Path
import time
import re
import sys
import socket
from gi.repository import Gio, GLib

NAME = 'dev.rolottr.BambuBar.Hyprland'
XML = f'''<node><interface name="{NAME}"><method name="GetAnchor">
<arg type="s" name="geometry" direction="out"/></method>
<method name="PreparePopup"><arg type="s" name="token" direction="in"/>
<arg type="s" name="geometry" direction="out"/></method>
<method name="SetAutostart"><arg type="b" name="enabled" direction="in"/>
<arg type="b" name="accepted" direction="out"/></method>
<method name="ShowPopup"><arg type="s" name="token" direction="in"/>
<arg type="i" name="width" direction="in"/><arg type="i" name="height" direction="in"/>
<arg type="b" name="fromTray" direction="in"/><arg type="b" name="positioned" direction="out"/></method>
<method name="HidePopup"><arg type="s" name="token" direction="in"/></method>
<signal name="Dismissed"><arg type="s" name="token"/></signal></interface></node>'''


def hypr_env():
    # Resolve the live session on every request, including after a compositor restart.
    env = os.environ.copy()
    instances = json.loads(subprocess.check_output(['hyprctl', '-j', 'instances'], timeout=1))
    live = [i for i in instances if i.get('wl_socket') == env.get('WAYLAND_DISPLAY')]
    if not live and len(instances) == 1:
        live = instances
    if len(live) != 1:
        raise ValueError('No unambiguous Hyprland session')
    env['HYPRLAND_INSTANCE_SIGNATURE'] = live[0]['instance']
    return env


def query(command):
    return json.loads(subprocess.check_output(['hyprctl', '-j', command], env=hypr_env(), timeout=1))


def anchor():
    cursor = query('cursorpos')
    monitors = query('monitors')
    for monitor in monitors:
        scale = float(monitor['scale'])
        if not math.isfinite(scale) or scale <= 0:
            continue
        width, height = monitor['width'], monitor['height']
        if monitor.get('transform', 0) % 2:
            width, height = height, width
        width, height = round(width / scale), round(height / scale)
        x, y = monitor['x'], monitor['y']
        if x <= cursor['x'] < x + width and y <= cursor['y'] < y + height:
            return json.dumps(dict(connector=monitor['name'], x=int(cursor['x'] - x),
                                   y=int(cursor['y'] - y), width=width, height=height, originX=x, originY=y))
    raise ValueError('Cursor monitor unavailable')


AUTOSTART = """[Desktop Entry]
Type=Application
Name=Bambu Bar
Exec=flatpak run dev.rolottr.BambuBar --background
Icon=dev.rolottr.BambuBar
X-BambuBar-Hyprland=true
"""


def set_autostart(enabled):
    directory = Path(os.environ.get('XDG_CONFIG_HOME', Path.home() / '.config')) / 'autostart'
    path = directory / 'dev.rolottr.BambuBar.desktop'
    if path.exists() and path.read_text() != AUTOSTART:
        # Never overwrite an independently managed login entry.
        return False
    if enabled:
        directory.mkdir(parents=True, exist_ok=True)
        if not path.exists():
            with path.open('x') as output:
                output.write(AUTOSTART)
    elif path.exists():
        path.rename(path.with_suffix('.desktop.backup-' + str(time.time_ns())))
    return True


# Host-only dismissal surfaces are necessary because Hyprland intentionally hides
# layer-shell from security-context/Flatpak clients. No Wayland socket is exported.
class Popup:
    def __init__(self):
        self.windows = []
        self.token = None
        self.owner = None
        self.generation = 0
        self.prepared = None
        self.events = None
        self.event_watch = None

    def clear(self):
        self.generation += 1
        if self.event_watch is not None:
            GLib.source_remove(self.event_watch)
            self.event_watch = None
        if self.events is not None:
            self.events.close()
            self.events = None
        for window in self.windows:
            window.destroy()
        self.windows.clear()
        self.token = self.owner = None

    def show(self, connection, sender, token, width, height, from_tray, invocation):
        if not re.fullmatch('[a-f0-9]{32}', token) or not 200 <= width <= 800 or not 1 <= height <= 4096:
            raise ValueError('Invalid popup dimensions or identity')
        import gi
        gi.require_version('Gtk', '3.0')
        gi.require_version('Gdk', '3.0')
        gi.require_version('GtkLayerShell', '0.1')
        from gi.repository import Gtk, Gdk, GtkLayerShell
        import cairo
        if not Gtk.init_check([])[0]:
            raise ValueError('Desktop unavailable')
        self.clear()
        self.token, self.owner = token, sender
        generation = self.generation
        def monitor_layout():
            return [(m['name'], m['x'], m['y'], m['width'], m['height'], m['scale'], m.get('transform', 0))
                    for m in query('monitors')]
        layout = monitor_layout()
        if self.prepared is None or self.prepared[:2] != (sender, token):
            raise ValueError('Popup was not prepared')
        geometry = json.loads(self.prepared[2])
        px = geometry['x'] if from_tray else geometry['width'] - 32
        py = geometry['y'] if from_tray else 13
        distances = [px, geometry['width'] - px, py, geometry['height'] - py]
        edge = distances.index(min(distances))
        width = min(width, geometry['width'] - 16)
        height = min(height, max(100, geometry['height'] - (distances[edge] + 32 if edge >= 2 else 16)))
        x = max(8, min(px - width // 2, geometry['width'] - width - 8))
        y = py + 24 if edge == 2 else py - height - 24 if edge == 3 else 8
        if edge < 2:
            x = px + 24 if edge == 0 else px - width - 24
        x = geometry['originX'] + max(0, x)
        y = geometry['originY'] + max(0, y)
        attempts = 0

        def dismiss(*_):
            self.clear()
            connection.emit_signal(sender, '/dev/rolottr/BambuBar/Hyprland', NAME, 'Dismissed', GLib.Variant('(s)', (token,)))
            return True

        def position():
            nonlocal attempts
            attempts += 1
            try:
                if generation != self.generation:
                    invocation.return_value(GLib.Variant('(b)', (False,)))
                    return False
                clients = [c for c in query('clients') if c.get('class') == 'dev.rolottr.BambuBar'
                           and c.get('title') == 'Bambu Bar — Quick view ' + token and c.get('mapped')]
                if not clients:
                    if attempts < 40:
                        return True
                    raise ValueError('Quick view did not map')
                address = clients[0]['address']
                if not re.fullmatch('0x[0-9a-fA-F]+', address):
                    raise ValueError('Invalid window address')
                # Only this freshly identified Bambu Bar popup is a dispatch target.
                # Numeric geometry is computed above; caller supplies no Lua/selector.
                lua = (f'hl.dispatch(hl.dsp.window.float({{window="address:{address}",action="on"}}));'
                       f'hl.dispatch(hl.dsp.window.resize({{window="address:{address}",x={width},y={height},relative=false}}));'
                       f'hl.dispatch(hl.dsp.window.move({{window="address:{address}",x={x},y={y},relative=false}}));'
                       f'hl.dispatch(hl.dsp.focus({{window="address:{address}"}}))')
                subprocess.run(['hyprctl', 'eval', lua], env=hypr_env(), check=True, stdout=subprocess.DEVNULL, timeout=2)
                display = Gdk.Display.get_default()
                css = Gtk.CssProvider()
                css.load_from_data(b'window { background-color: transparent; box-shadow: none; }')
                for i in range(display.get_n_monitors()):
                    monitor = display.get_monitor(i)
                    bounds = monitor.get_geometry()
                    window = Gtk.Window()
                    window.set_app_paintable(True)
                    visual = window.get_screen().get_rgba_visual()
                    if visual:
                        window.set_visual(visual)
                    window.get_style_context().add_provider(css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
                    GtkLayerShell.init_for_window(window)
                    GtkLayerShell.set_namespace(window, 'dev.rolottr.BambuBar.Dismiss')
                    GtkLayerShell.set_monitor(window, monitor)
                    GtkLayerShell.set_layer(window, GtkLayerShell.Layer.OVERLAY)
                    GtkLayerShell.set_keyboard_mode(window, GtkLayerShell.KeyboardMode.NONE)
                    GtkLayerShell.set_exclusive_zone(window, -1)
                    for side in (GtkLayerShell.Edge.TOP, GtkLayerShell.Edge.BOTTOM, GtkLayerShell.Edge.LEFT, GtkLayerShell.Edge.RIGHT):
                        GtkLayerShell.set_anchor(window, side, True)
                    window.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
                    window.connect('button-press-event', dismiss)
                    self.windows.append(window)
                    window.show_all()
                    # Leave the actual popup and its compositor border interactive.
                    region = cairo.Region(cairo.RectangleInt(0, 0, bounds.width, bounds.height))
                    region.subtract(cairo.Region(cairo.RectangleInt(x - bounds.x - 3, y - bounds.y - 3, width + 6, height + 6)))
                    window.get_window().input_shape_combine_region(region, 0, 0)
                # Compositor focus events also cover GTK's stale is-active state on
                # reused toplevels. Close on output/config changes: old input holes
                # must never outlive the geometry they were calculated for.
                environment = hypr_env()
                self.events = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.events.connect(str(Path(environment['XDG_RUNTIME_DIR']) / 'hypr' /
                                        environment['HYPRLAND_INSTANCE_SIGNATURE'] / '.socket2.sock'))
                self.events.setblocking(False)
                pending = b''
                def event_ready(_fd, condition):
                    nonlocal pending
                    if condition & (GLib.IO_HUP | GLib.IO_ERR):
                        self.event_watch = None
                        dismiss()
                        return False
                    try:
                        data = self.events.recv(65536)
                        if not data:
                            self.event_watch = None
                            dismiss()
                            return False
                        pending += data
                    except BlockingIOError:
                        return True
                    except OSError:
                        self.event_watch = None
                        dismiss()
                        return False
                    if len(pending) > 131072:
                        self.event_watch = None
                        dismiss()
                        return False
                    lines = pending.split(b'\n')
                    pending = lines.pop()
                    for line in lines:
                        name, _, value = line.partition(b'>>')
                        lost_focus = name == b'activewindowv2' and value.decode().removeprefix('0x') != address.removeprefix('0x')
                        geometry_changed = name in (b'monitorremoved', b'monitoradded')
                        if name == b'configreloaded':
                            try:
                                geometry_changed = monitor_layout() != layout
                            except (OSError, ValueError, KeyError, subprocess.SubprocessError):
                                geometry_changed = True
                        if lost_focus or geometry_changed:
                            self.event_watch = None
                            dismiss()
                            return False
                    return True
                self.event_watch = GLib.io_add_watch(self.events.fileno(), GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR, event_ready)
                invocation.return_value(GLib.Variant('(b)', (True,)))
            except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
                print("Quick view positioning failed:", type(error).__name__, str(error), file=sys.stderr)
                self.clear()
                invocation.return_value(GLib.Variant('(b)', (False,)))
            return False
        GLib.timeout_add(25, position)


popup = Popup()


def method(connection, sender, path, interface, name, parameters, invocation):
    try:
        if name == 'GetAnchor':
            invocation.return_value(GLib.Variant('(s)', (anchor(),)))
        elif name == 'PreparePopup':
            token = parameters.unpack()[0]
            if not re.fullmatch('[a-f0-9]{32}', token):
                raise ValueError('Invalid popup identity')
            geometry = anchor()
            popup.prepared = (sender, token, geometry)
            invocation.return_value(GLib.Variant('(s)', (geometry,)))
        elif name == 'SetAutostart':
            invocation.return_value(GLib.Variant('(b)', (set_autostart(parameters.unpack()[0]),)))
        elif name == 'ShowPopup':
            popup.show(connection, sender, *parameters.unpack(), invocation)
        elif name == 'HidePopup':
            if popup.owner == sender and popup.token == parameters.unpack()[0]:
                popup.clear()
            invocation.return_value(None)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        invocation.return_dbus_error(NAME + '.Unavailable', 'Desktop integration unavailable')


def acquired(connection, name):
    node = Gio.DBusNodeInfo.new_for_xml(XML)
    connection.register_object('/dev/rolottr/BambuBar/Hyprland', node.interfaces[0], method, None, None)
    def owner_changed(connection, sender, path, interface, signal, parameters):
        name, old, new = parameters.unpack()
        if name == popup.owner and not new:
            popup.clear()
    connection.signal_subscribe('org.freedesktop.DBus', 'org.freedesktop.DBus', 'NameOwnerChanged',
                                '/org/freedesktop/DBus', None, Gio.DBusSignalFlags.NONE, owner_changed)


if __name__ == '__main__':
    Gio.bus_own_name(Gio.BusType.SESSION, NAME, Gio.BusNameOwnerFlags.NONE, acquired, None, None)
    GLib.MainLoop().run()
