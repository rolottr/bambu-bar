"""Run on the Linux host with PyGObject; uses no compositor or printer connection."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('anchor', Path(__file__).parents[1] / 'hyprland/anchor.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class IntegrationTests(unittest.TestCase):
    def test_mixed_scale_offset(self):
        def query(command):
            return {'x': 2250, 'y': 12} if command == 'cursorpos' else [
                dict(name='left', x=0, y=0, width=1920, height=1080, scale=1),
                dict(name='right', x=1920, y=0, width=2560, height=1440, scale=2)]
        with patch.object(bridge, 'query', query):
            self.assertEqual(json.loads(bridge.anchor()), dict(connector='right', x=330, y=12, width=1280, height=720))

    def test_rotated_negative_origin(self):
        def query(command):
            return {'x': -100, 'y': 20} if command == 'cursorpos' else [
                dict(name='portrait', x=-720, y=0, width=2560, height=1440, scale=2, transform=1)]
        with patch.object(bridge, 'query', query):
            self.assertEqual(json.loads(bridge.anchor())['x'], 620)
            self.assertEqual(json.loads(bridge.anchor())['height'], 1280)

    def test_autostart_and_preserve_unmanaged_file(self):
        with tempfile.TemporaryDirectory() as folder, patch.dict(os.environ, XDG_CONFIG_HOME=folder):
            file = Path(folder) / 'autostart/dev.rolottr.BambuBar.desktop'
            self.assertTrue(bridge.set_autostart(True))
            self.assertEqual(file.read_text(), bridge.AUTOSTART)
            self.assertTrue(bridge.set_autostart(False))
            self.assertFalse(file.exists())
            self.assertEqual(len(list(file.parent.glob('*.backup-*'))), 1)
            file.write_text('user managed')
            self.assertFalse(bridge.set_autostart(True))
            self.assertFalse(bridge.set_autostart(False))
            self.assertEqual(file.read_text(), 'user managed')


unittest.main()
