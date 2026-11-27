#!/usr/bin/env python3
"""Event-driven transport. --desktop never opens the private main-window socket."""
import json
import os
import socket
import sys
from urllib.parse import urlparse, unquote
from pathlib import Path
from gi.repository import Gio, GLib

APP = 'dev.rolottr.BambuBar'
BUS = Gio.bus_get_sync(Gio.BusType.SESSION, None)
DESKTOP = '--desktop' in sys.argv
loop = GLib.MainLoop()
connection = None
pending = bytearray()
input_pending = bytearray()
desired_page = os.environ.get("BAMBUBAR_PAGE", "Printer")
export_target = None  # The file the window's save dialog chose; the app's export may only be written there.

def emit(value):
    try:
        print(json.dumps(value, separators=(',', ':')), flush=True)
    except (BrokenPipeError, OSError):
        loop.quit()

def desktop_call(method, parameters=None):
    def done(bus, result):
        try:
            reply = bus.call_finish(result)
            if method == 'GetState':
                emit({'type': 'state', 'desktop': json.loads(reply.unpack()[0])})
        except GLib.Error:
            pages = {'show':'Printer', 'settings':'Settings', 'filament':'Filament', 'fleet':'All Printers',
                     'widgets':'Widgets', 'license':'About'}
            page = pages.get(parameters.unpack()[0]) if method == 'Invoke' else None
            if page:
                BUS.call(APP + '.Native', '/dev/rolottr/BambuBar/Native', APP + '.Native', 'Open', GLib.Variant('(s)', (page,)), None, Gio.DBusCallFlags.NONE, 3000, None, None)
            if method == 'GetState':
                emit({'type': 'state', 'desktop': {}})
    BUS.call(APP + '.Desktop', '/dev/rolottr/BambuBar/Desktop', APP + '.Desktop', method,
             parameters, None, Gio.DBusCallFlags.NONE, 3000, None, done)

def file_path(url):
    parsed = urlparse(str(url))
    if parsed.scheme != 'file' or parsed.netloc not in ('', 'localhost'):
        return None
    return unquote(parsed.path)

def save_export(message):
    global export_target
    path, export_target = export_target, None
    if not path or file_path(message.get('path', '')) != path:
        return
    try:
        with open(path, 'w', encoding='utf-8') as stream:
            stream.write(str(message.get('data', '')))
        emit({'type': 'notice', 'message': str(message.get('done', ''))})
    except OSError as error:
        emit({'type': 'error', 'message': f'{error.strerror}: {path}'})

def send(message):
    global desired_page, export_target
    if str(message.get("action", "")).startswith("navigate:"):
        desired_page = message["action"][9:]
    if DESKTOP:
        action = message.get('action')
        if action == 'open':
            desktop_call('SetOpen', GLib.Variant('(b)', (message.get('value') is True,)))
        elif action in ('select', 'show', 'settings', 'filament', 'fleet', 'widgets', 'license', 'support-panel', 'support-menu', 'refresh', 'camera', 'light',
                        'pause', 'resume', 'stop', 'stop-confirmed', 'quit', 'dismiss-issue') or (isinstance(action, str) and action.startswith(('speed:', 'ignore-issue:'))):
            desktop_call('Invoke', GLib.Variant('(ss)', (action, str(message.get('printer', '')))))
    elif connection:
        if message.get('action') == 'export-activity':
            export_target = file_path(message.get('value', ''))
            if not export_target:
                return
        if message.get('action') == 'import-file':
            parsed = urlparse(str(message.get('value', '')))
            if parsed.scheme != 'file' or parsed.netloc not in ('', 'localhost'):
                return
            try:
                with open(unquote(parsed.path), 'r', encoding='utf-8') as stream:
                    contents = stream.read(1024 * 1024 + 1)
                if len(contents) > 1024 * 1024:
                    raise ValueError('Too large')
                message = {'action': 'import-text', 'value': contents}
            except (OSError, ValueError):
                emit({'type': 'error', 'message': 'Could not read this settings file (maximum 1 MiB).'})
                return
        try:
            connection.sendall((json.dumps(message, separators=(',', ':')) + '\n').encode())
        except OSError:
            disconnect()

def stdin_ready(fd, condition):
    if condition & (GLib.IO_HUP | GLib.IO_ERR):
        loop.quit()
        return False
    try:
        chunk = os.read(fd, 4096)
        if not chunk:
            loop.quit()
            return False
        input_pending.extend(chunk)
        if len(input_pending) > 65536:
            loop.quit()
            return False
        while b'\n' in input_pending:
            line, _, rest = input_pending.partition(b'\n')
            input_pending[:] = rest
            value = json.loads(line)
            if isinstance(value, dict):
                send(value)
    except (OSError, ValueError):
        loop.quit()
        return False
    return True

def disconnect():
    global connection
    if connection:
        connection.close()
        connection = None
    pending.clear()
    emit({'type': 'disconnected'})

def socket_ready(fd, condition):
    if condition & (GLib.IO_HUP | GLib.IO_ERR):
        disconnect()
        return False
    try:
        chunk = connection.recv(65536)
        if not chunk:
            disconnect()
            return False
        pending.extend(chunk)
        if len(pending) > 32 * 1024 * 1024:
            disconnect()
            return False
        while b'\n' in pending:
            line, _, rest = pending.partition(b'\n')
            pending[:] = rest
            message = json.loads(line)
            if message.get('type') == 'activity-export':
                save_export(message)
                continue
            if message.get('type') == 'capture':
                folder = Path.home() / '.var/app' / APP / 'data'
                if os.environ.get('BAMBUBAR_SYSTEM_PACKAGE') == '1':
                    folder = Path(os.environ.get('XDG_DATA_HOME', Path.home() / '.local/share')) / 'bambu-bar'
                path = str(message.get('path', ''))
                if path.startswith('/var/data/'):
                    path = str(folder / path[len('/var/data/'):])
                target = Path(path).resolve()
                if not target.is_relative_to(folder.resolve()) or target.suffix != '.png':
                    send({'action':'capture-result','value':False})
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                message['path'] = str(target)
            emit(message)
    except (OSError, ValueError):
        disconnect()
        return False
    return True

def connect():
    global connection
    if connection:
        return True
    directory = Path(os.environ.get('XDG_RUNTIME_DIR', '/run/user/' + str(os.getuid()))) / 'app' / APP / 'native'
    suffix = os.environ.get('BAMBUBAR_ENDPOINT', '')
    if suffix and not (suffix.startswith('-') and len(suffix) == 33 and all(c in '0123456789abcdef' for c in suffix[1:])):
        return False
    path = directory / ('ui' + suffix + '.sock')
    try:
        if directory.stat().st_uid != os.getuid() or directory.stat().st_mode & 0o077:
            return True
        client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        client.connect(str(path))
        connection = client
        GLib.io_add_watch(client.fileno(), GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR, socket_ready)
        send({'action': 'navigate:' + desired_page})
    except OSError:
        client.close() if 'client' in locals() else None
    return True

GLib.io_add_watch(sys.stdin.fileno(), GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR, stdin_ready)
if DESKTOP:
    BUS.signal_subscribe(APP + '.Desktop', APP + '.Desktop', 'StateChanged', '/dev/rolottr/BambuBar/Desktop', None,
                         Gio.DBusSignalFlags.NONE, lambda *args: emit({'type': 'state', 'desktop': json.loads(args[5].unpack()[0])}))
    Gio.bus_watch_name_on_connection(BUS, APP + '.Desktop', Gio.BusNameWatcherFlags.NONE,
        lambda *_: desktop_call('GetState'), lambda *_: emit({'type': 'state', 'desktop': {}}))
    # The shell owns the printer icon while this actual companion is alive.
    Gio.bus_own_name_on_connection(BUS, APP + '.Shell', Gio.BusNameOwnerFlags.NONE, None, None)
else:
    if not os.environ.get('BAMBUBAR_ENDPOINT'):
        BUS.signal_subscribe(APP + '.Native', APP + '.Native', 'Show', '/dev/rolottr/BambuBar/Native', None,
                         Gio.DBusSignalFlags.NONE, lambda *args: emit({'type': 'show', 'page': args[5].unpack()[0]}))
    theme_root = Path.home() / '.local/state/omarchy/current'
    theme_monitors = []
    def theme_changed(*_):
        try:
            theme = theme_root / 'theme'
            emit({'type': 'theme', 'colors': (theme / 'colors.toml').read_text(), 'shell': (theme / 'shell.toml').read_text()})
        except OSError:
            pass
    if theme_root.is_dir():
        monitor = Gio.File.new_for_path(str(theme_root)).monitor_directory(Gio.FileMonitorFlags.WATCH_MOVES, None)
        monitor.connect('changed', theme_changed)
        theme_monitors.append(monitor)
        theme_changed()
    connect()
    GLib.timeout_add(500, connect)  # Reconnect only; live updates are socket events, never polled.
loop.run()
if connection:
    connection.close()
