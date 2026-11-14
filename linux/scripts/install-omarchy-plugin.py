#!/usr/bin/env python3
"""Install our plugin and add one bar entry, preserving the rest of shell.json."""
import datetime
import json
import os
import shutil
from pathlib import Path

root = Path(__file__).resolve().parents[1]
home = Path.home()
config = home / '.config/omarchy/shell.json'
if not Path('/usr/share/omarchy/shell/Ui/PopupCard.qml').is_file():
    raise SystemExit('This package requires the Omarchy Quickshell plugin API.')
state = json.loads(config.read_text())
if not isinstance(state, dict) or not isinstance(state.get('bar', {}), dict):
    raise SystemExit('The existing Omarchy bar configuration is not an object; no changes made.')
rule = home / '.config/hypr/bambu-bar-native.lua'
if rule.exists() and not rule.read_text().startswith('-- Bambu Bar native windows'):
    raise SystemExit('Refusing to overwrite an unmanaged bambu-bar-native.lua; no changes made.')
target = home / '.config/omarchy/plugins/dev.rolottr.BambuBar'
backup = home / '.local/state/bambu-bar/backups' / ('omarchy-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
backup.mkdir(parents=True, exist_ok=False)
shutil.copy2(config, backup / 'shell.json')
if target.exists():
    shutil.copytree(target, backup / 'plugin')
shutil.copytree(root / 'omarchy', target, dirs_exist_ok=True)
shutil.copytree(root / 'native/common', target / 'common', dirs_exist_ok=True)
shutil.copy2(root / 'native/omarchy/Kit.qml', target / 'Kit.qml')
shutil.copy2(root / 'native/bridge.py', target / 'bridge.py')
import subprocess
subprocess.run(['python3', str(root / 'scripts/generate-tray-icons.py'), str(target / 'tray-icons')], check=True)
layout = state.setdefault('bar', {}).setdefault('layout', {})
if not any((entry.get('id') if isinstance(entry, dict) else entry) == 'dev.rolottr.BambuBar' for entries in layout.values() if isinstance(entries, list) for entry in entries):
    layout.setdefault('right', []).insert(0, {'id': 'dev.rolottr.BambuBar'})
temporary = config.with_name('shell.json.bambu-bar.tmp')
temporary.write_text(json.dumps(state, indent=2) + '\n')
os.replace(temporary, config)
print('Omarchy plugin installed; original configuration backed up at', backup)

# A scoped compositor rule lets the native main/camera windows choose their own bounded sizes.
hypr = home / '.config/hypr'
main_lua = hypr / 'hyprland.lua'
if main_lua.is_file():
    legacy = hypr / 'bambu-bar.lua'
    if legacy.exists() and legacy.read_text().startswith('-- Bambu Bar compact main window'):
        shutil.copy2(legacy, backup / 'bambu-bar.lua')
        legacy.write_text('-- Bambu Bar compact main window; native rules are in bambu-bar-native.lua.\n')
    rule = hypr / 'bambu-bar-native.lua'
    if rule.exists():
        if not rule.read_text().startswith('-- Bambu Bar native windows'):
            raise SystemExit('Refusing to overwrite an unmanaged bambu-bar-native.lua')
        shutil.copy2(rule, backup / 'bambu-bar-native.lua')
    rule.write_text('\n'.join([
        '-- Bambu Bar native windows (managed by install-omarchy-plugin.py).',
        'hl.window_rule({',
        '  name = "bambu-bar-native-windows",',
        '  match = { class = "^dev\\\\.rolottr\\\\.BambuBar$", title = "^Bambu Bar.*|.* — Camera$" },',
        '  float = true, center = true,',
        '})', ''
    ]))
    include = 'dofile(os.getenv("HOME") .. "/.config/hypr/bambu-bar-native.lua")'
    original = main_lua.read_text()
    if include not in original:
        shutil.copy2(main_lua, backup / 'hyprland.lua')
        main_lua.write_text(original.rstrip() + '\n\n' + include + '\n')
