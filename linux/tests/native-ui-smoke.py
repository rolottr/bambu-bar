#!/usr/bin/env python3
"""Exercise a running native backend without changing printer hardware.

Visits all pages, checks the private socket boundary, rejects malformed requests,
and round-trips one notification preference before restoring it. Run in the
logged-in desktop session. Optional argument: an isolated demo's socket path.
"""
import json
import os
from pathlib import Path
import socket
import stat
import sys

path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ['XDG_RUNTIME_DIR']) / 'app/dev.rolottr.BambuBar/native/ui.sock'
assert stat.S_IMODE(path.parent.stat().st_mode) == 0o700
assert stat.S_IMODE(path.stat().st_mode) == 0o600
assert path.stat().st_uid == os.getuid()

with socket.socket(socket.AF_UNIX) as client:
    client.settimeout(15)
    client.connect(str(path))
    reader = client.makefile('r')
    def receive(predicate):
        for _ in range(200):
            message = json.loads(reader.readline())
            if predicate(message):
                return message
        raise AssertionError('Expected response did not arrive')
    def request(action, value='', predicate=None):
        client.sendall((json.dumps({'action': action, 'value': value}) + '\n').encode())
        return receive(predicate or (lambda m: m.get('type') == 'state'))
    initial = receive(lambda m: m.get('type') == 'state')
    pages = [p['id'] for p in initial['pages']]
    assert len(pages) == len(set(pages)), 'Duplicate navigation page'
    for page in pages:
        state = request('navigate:' + page, predicate=lambda m: m.get('type') == 'state' and m.get('page') == page)
        assert isinstance(state['sections'], list)
        assert not state['error'], 'Page reported an error'
    # Activity: capabilities and events arrive only for its page; filters and the export round-trip.
    state = request('navigate:Activity', predicate=lambda m: m.get('page') == 'Activity' and m.get('activity'))
    activity = state['activity']
    assert isinstance(activity['capabilities'], list) and isinstance(activity['events'], list)
    assert [f['value'] for f in activity['filters']] == ['', 'connection', 'job', 'issue', 'camera', 'action']
    state = request('activity-filter:connection', predicate=lambda m: (m.get('activity') or {}).get('filter') == 'connection')
    assert all(e['category'] == 'connection' for e in state['activity']['events'])
    request('activity-filter:', predicate=lambda m: (m.get('activity') or {}).get('filter') == '')
    export = request('export-activity', 'file:///nonexistent/activity.json', lambda m: m.get('type') == 'activity-export')
    assert export['path'] == 'file:///nonexistent/activity.json'
    assert sorted(json.loads(export['data'])) == ['appVersion', 'capabilities', 'events', 'exportedAt']
    other = request('navigate:Settings', predicate=lambda m: m.get('page') == 'Settings')
    assert other.get('activity') is None
    state = request('navigate:Notifications', predicate=lambda m: m.get('page') == 'Notifications')
    def setting(message):
        return next(f['value'] for s in message['sections'] for f in s['fields'] if f['key'] == 'notifyFinished')
    original = setting(state)
    try:
        request('notifyFinished', not original, lambda m: m.get('page') == 'Notifications' and setting(m) != original)
    finally:
        request('notifyFinished', original, lambda m: m.get('page') == 'Notifications' and setting(m) == original)
    client.sendall(b'{}\n')
    receive(lambda m: m.get('type') == 'state' and bool(m.get('error')))
    request('navigate:Printer', predicate=lambda m: m.get('page') == 'Printer' and not m.get('error'))

# Invalid JSON closes this client only; reconnecting still receives live state.
with socket.socket(socket.AF_UNIX) as bad:
    bad.connect(str(path))
    bad.sendall(b'{invalid}\n')
with socket.socket(socket.AF_UNIX) as fresh:
    fresh.settimeout(15)
    fresh.connect(str(path))
    assert json.loads(fresh.makefile('r').readline())['type'] == 'state'
print(f'Native UI: {len(pages)} unique pages, preference restore, socket permissions and malformed-client recovery passed.')
