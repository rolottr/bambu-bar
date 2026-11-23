#!/usr/bin/env python3
"""Verify native model provenance, named parts and self-contained asset paths."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import tarfile
import unittest

ROOT = Path(__file__).resolve().parents[2]

class NativeAssets(unittest.TestCase):
    def test_models_match_shared_sources_and_preserve_animated_parts(self):
        folder = ROOT / 'shared/Printers'
        with tarfile.open(folder / 'qt-models.tar.gz') as archive:
            names = set(archive.getnames())
            manifest = json.load(archive.extractfile('sources.json'))
            self.assertEqual(set(manifest), {p.name for p in folder.glob('*.glb')})
            for source, digest in manifest.items():
                self.assertEqual(hashlib.sha256((folder / source).read_bytes()).hexdigest(), digest)
                stem = Path(source).stem
                qml = archive.extractfile(stem + '/Printer.qml').read().decode()
                self.assertIn('objectName: "Toolhead"', qml)
                self.assertIn('objectName: "Part_00"', qml)
                self.assertIn('position: Qt.vector3d(', qml)
                for mesh in re.findall(r'source: "([^"]+)"', qml):
                    self.assertIn(stem + '/' + mesh, names)
            for name in names:
                self.assertFalse(PurePosixPath(name).is_absolute())
                self.assertNotIn('..', PurePosixPath(name).parts)

if __name__ == '__main__':
    unittest.main()
