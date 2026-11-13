#!/usr/bin/env python3
"""Desktop bridge: read-only geometry and a fixed app autostart entry. No printer data or arbitrary commands."""
import json
import math
import os
import subprocess
from pathlib import Path
import time
from gi.repository import Gio, GLib

NAME = 'dev.rolottr.BambuBar.Hyprland'
XML = f'''<node><interface name="{NAME}"><method name="GetAnchor">
<arg type="s" name="geometry" direction="out"/></method>
<method name="SetAutostart"><arg type="b" name="enabled" direction="in"/>
<arg type="b" name="accepted" direction="out"/></method></interface></node>'''


def query(command):
    # Resolve the live session on every request, including after a compositor restart.
    env = os.environ.copy()
    instances = json.loads(subprocess.check_output(['hyprctl', '-j', 'instances'], timeout=1))
    live = [i for i in instances if i.get('wl_socket') == env.get('WAYLAND_DISPLAY')]
    if not live and len(instances) == 1:
        live = instances
    if len(live) != 1:
        raise ValueError('No unambiguous Hyprland session')
    env['HYPRLAND_INSTANCE_SIGNATURE'] = live[0]['instance']
    return json.loads(subprocess.check_output(['hyprctl', '-j', command], env=env, timeout=1))


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
                                   y=int(cursor['y'] - y), width=width, height=height))
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


def method(connection, sender, path, interface, name, parameters, invocation):
    try:
        if name == 'GetAnchor':
            invocation.return_value(GLib.Variant('(s)', (anchor(),)))
        elif name == 'SetAutostart':
            invocation.return_value(GLib.Variant('(b)', (set_autostart(parameters.unpack()[0]),)))
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        invocation.return_dbus_error(NAME + '.Unavailable', 'Desktop geometry unavailable')


def acquired(connection, name):
    node = Gio.DBusNodeInfo.new_for_xml(XML)
    connection.register_object('/dev/rolottr/BambuBar/Hyprland', node.interfaces[0], method, None, None)


if __name__ == '__main__':
    Gio.bus_own_name(Gio.BusType.SESSION, NAME, Gio.BusNameOwnerFlags.NONE, acquired, None, None)
    GLib.MainLoop().run()
