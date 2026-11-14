#!/usr/bin/env python3
"""D-Bus-activated launcher for native UI. No printer data or credentials pass here."""
import os
import subprocess
import time
from pathlib import Path
from gi.repository import Gio, GLib

APP = 'dev.rolottr.BambuBar'
ROOT = Path(__file__).resolve().parent
XML = '''<node><interface name="dev.rolottr.BambuBar.Native">
<method name="Open"><arg name="page" type="s" direction="in"/></method>
<method name="Panel"/>
<method name="SetAutostart"><arg name="enabled" type="b" direction="in"/><arg name="accepted" type="b" direction="out"/></method>
<method name="OpenDemo"><arg name="instance" type="s" direction="in"/></method>
<method name="SetPanelPresent"><arg name="present" type="b" direction="in"/></method>
<signal name="Show"><arg name="page" type="s"/></signal>
<signal name="TogglePanel"/>
</interface></node>'''
process = None
shell_owner = 0
panel_senders = set()
demos = {}
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)

def call(connection, sender, path, interface, method, parameters, invocation):
    global process, shell_owner
    omarchy = Path('/usr/share/omarchy/shell/shell.qml').exists()
    if method == 'Open':
        owned = bus.call_sync('org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', 'NameHasOwner', GLib.Variant('(s)', (APP + '.Desktop',)), None, Gio.DBusCallFlags.NONE, 1000, None).unpack()[0]
        if not owned:
            subprocess.Popen(['flatpak', 'run', APP, '--background'], stdin=subprocess.DEVNULL)
        page = parameters.unpack()[0]
        if page not in ('Printer', 'Filament', 'All Printers', 'Menu Bar', 'Notifications', 'Printers', 'Widgets', 'Settings', 'About'):
            invocation.return_dbus_error(APP + '.InvalidPage', 'Unknown page')
            return
        if process is None or process.poll() is not None:
            env = os.environ.copy()
            env['BAMBUBAR_PAGE'] = page
            env['QS_APP_ID'] = APP
            command = ['/usr/bin/python3', str(ROOT / 'launch.py')]
            process = subprocess.Popen(command, env=env, stdin=subprocess.DEVNULL)
        bus.emit_signal(None, '/dev/rolottr/BambuBar/Native', APP + '.Native', 'Show', GLib.Variant('(s)', (page,)))
    elif method == 'OpenDemo':
        instance = parameters.unpack()[0]
        if len(instance) != 32 or any(c not in '0123456789abcdef' for c in instance):
            invocation.return_dbus_error(APP + '.InvalidInstance', 'Invalid demo instance')
            return
        if instance not in demos or demos[instance].poll() is not None:
            env = os.environ.copy()
            env['BAMBUBAR_ENDPOINT'] = '-' + instance
            env['QS_APP_ID'] = APP
            env['BAMBUBAR_DEMO'] = '1'
            command = ['/usr/bin/python3', str(ROOT / 'launch.py')]
            demos[instance] = subprocess.Popen(command, env=env, stdin=subprocess.DEVNULL)
    elif method == 'SetAutostart':
        path = Path(os.environ.get('XDG_CONFIG_HOME', Path.home() / '.config')) / 'autostart' / (APP + '.desktop')
        # Recognize the previous managed entry, but leave unrelated user entries alone.
        entry = '[Desktop Entry]\nType=Application\nName=Bambu Bar\nExec=flatpak run dev.rolottr.BambuBar --background\nIcon=dev.rolottr.BambuBar\nX-BambuBar-Hyprland=true\n'
        try:
            if path.exists() and path.read_text() != entry:
                invocation.return_value(GLib.Variant('(b)', (False,)))
                return
            if parameters.unpack()[0]:
                path.parent.mkdir(parents=True, exist_ok=True)
                if not path.exists():
                    with path.open('x') as stream:
                        stream.write(entry)
            elif path.exists():
                path.rename(path.with_suffix('.desktop.backup-' + str(time.time_ns())))
            invocation.return_value(GLib.Variant('(b)', (True,)))
        except OSError:
            invocation.return_value(GLib.Variant('(b)', (False,)))
        return
    elif method == 'SetPanelPresent':
        present = parameters.unpack()[0]
        if present:
            panel_senders.add(sender)
        else:
            panel_senders.discard(sender)
        if present and not shell_owner:
            shell_owner = Gio.bus_own_name_on_connection(bus, APP + '.Shell', Gio.BusNameOwnerFlags.NONE, None, None)
        elif not panel_senders and shell_owner:
            Gio.bus_unown_name(shell_owner)
            shell_owner = 0
    elif method == 'Panel':
        if omarchy:
            subprocess.Popen(['omarchy-shell', 'shell', 'toggle', 'dev.rolottr.BambuBar'], stdin=subprocess.DEVNULL)
        bus.emit_signal(None, '/dev/rolottr/BambuBar/Native', APP + '.Native', 'TogglePanel', None)
    invocation.return_value(None)

def owner_changed(connection, sender, path, interface, signal, parameters):
    global shell_owner
    name, old_owner, new_owner = parameters.unpack()
    if not new_owner and name in panel_senders:
        panel_senders.discard(name)
        if not panel_senders and shell_owner:
            Gio.bus_unown_name(shell_owner)
            shell_owner = 0

bus.signal_subscribe('org.freedesktop.DBus', 'org.freedesktop.DBus', 'NameOwnerChanged',
    '/org/freedesktop/DBus', None, Gio.DBusSignalFlags.NONE, owner_changed)
bus.register_object('/dev/rolottr/BambuBar/Native', Gio.DBusNodeInfo.new_for_xml(XML).interfaces[0], call, None, None)
Gio.bus_own_name_on_connection(bus, APP + '.Native', Gio.BusNameOwnerFlags.NONE, None, None)
GLib.MainLoop().run()
