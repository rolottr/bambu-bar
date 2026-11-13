#!/usr/bin/env python3
"""Read-only geometry bridge. No printer data, arbitrary commands or socket forwarding."""
import json
import math
import os
import subprocess
from gi.repository import Gio, GLib

NAME = 'dev.rolottr.BambuBar.Hyprland'
XML = f'''<node><interface name="{NAME}"><method name="GetAnchor">
<arg type="s" name="geometry" direction="out"/></method></interface></node>'''


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


def method(connection, sender, path, interface, name, parameters, invocation):
    try:
        invocation.return_value(GLib.Variant('(s)', (anchor(),)))
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        invocation.return_dbus_error(NAME + '.Unavailable', 'Desktop geometry unavailable')


def acquired(connection, name):
    node = Gio.DBusNodeInfo.new_for_xml(XML)
    connection.register_object('/dev/rolottr/BambuBar/Hyprland', node.interfaces[0], method, None, None)


if __name__ == '__main__':
    Gio.bus_own_name(Gio.BusType.SESSION, NAME, Gio.BusNameOwnerFlags.NONE, acquired, None, None)
    GLib.MainLoop().run()
