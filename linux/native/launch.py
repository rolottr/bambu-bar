#!/usr/bin/env python3
"""One native main window per backend instance, even after the activation service restarts."""
import fcntl
import os
from pathlib import Path
import signal
import subprocess
import sys

root = Path(__file__).resolve().parent
folder = Path(os.environ.get('XDG_RUNTIME_DIR', '/run/user/' + str(os.getuid()))) / 'bambu-bar-native'
folder.mkdir(mode=0o700, exist_ok=True)
suffix = os.environ.get('BAMBUBAR_ENDPOINT', '')
if suffix and not (suffix.startswith('-') and len(suffix) == 33 and all(c in '0123456789abcdef' for c in suffix[1:])):
    raise SystemExit(2)
with open(folder / ('window' + suffix + '.lock'), 'a') as lock:
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise SystemExit(0)
    command = ['qs', '-p', str(root / 'omarchy/shell.qml')] if Path('/usr/share/omarchy/shell/shell.qml').exists() else ['/usr/bin/python3', str(root / 'kde/main.py')]
    child = subprocess.Popen(command, env={**os.environ, 'BAMBUBAR_NATIVE_ROOT': str(root)})
    def stop(*_):
        child.terminate()
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    raise SystemExit(child.wait())
