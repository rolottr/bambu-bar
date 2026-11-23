#!/usr/bin/env python3
"""Regenerate the checked-in Qt meshes from shared GLBs using Qt's balsam tool.

Qt 6.10's RuntimeLoader discards node names, which are needed for per-spool
colors and print progress. Preconverted QML preserves those names on both
tested Qt versions. The archive has fixed metadata and source hashes.
Usage: generate-qt-models.py /usr/lib/qt6/bin/balsam
"""
import gzip
import hashlib
import itertools
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tarfile
import tempfile

root = Path(__file__).resolve().parents[2] / 'shared/Printers'
identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]

def multiply(a, b):
    return [sum(a[k * 4 + row] * b[col * 4 + k] for k in range(4)) for col in range(4) for row in range(4)]

def bounds(data):
    points = []
    def visit(index, parent):
        node = data['nodes'][index]
        x, y, z, w = node.get('rotation', [0, 0, 0, 1])
        matrix = node.get('matrix', [1-2*y*y-2*z*z, 2*x*y+2*z*w, 2*x*z-2*y*w, 0,
            2*x*y-2*z*w, 1-2*x*x-2*z*z, 2*y*z+2*x*w, 0,
            2*x*z+2*y*w, 2*y*z-2*x*w, 1-2*x*x-2*y*y, 0, *node.get('translation', [0, 0, 0]), 1])
        if 'matrix' not in node:
            for col, scale in enumerate(node.get('scale', [1, 1, 1])):
                for row in range(4): matrix[col * 4 + row] *= scale
        world = multiply(parent, matrix)
        if 'mesh' in node:
            for primitive in data['meshes'][node['mesh']]['primitives']:
                accessor = data['accessors'][primitive['attributes']['POSITION']]
                for point in itertools.product(*zip(accessor['min'], accessor['max'])):
                    points.append([sum(world[k * 4 + row] * point[k] for k in range(3)) + world[12 + row] for row in range(3)])
        for child in node.get('children', []): visit(child, world)
    for node in data['scenes'][data.get('scene', 0)]['nodes']: visit(node, identity)
    low = [min(p[i] for p in points) for i in range(3)]
    high = [max(p[i] for p in points) for i in range(3)]
    extent = max(b-a for a, b in zip(low, high))
    return 280 / extent, [-(a+b) * 140 / extent for a, b in zip(low, high)]

with tempfile.TemporaryDirectory() as temporary:
    output = Path(temporary)
    manifest = {}
    for source in sorted(root.glob('*.glb')):
        folder = output / source.stem
        folder.mkdir()
        subprocess.run([sys.argv[1], str(source), '-o', str(folder)], check=True,
            env={**os.environ, 'QT_QPA_PLATFORM':'offscreen'}, stdout=subprocess.DEVNULL)
        raw = source.read_bytes()
        length = struct.unpack_from('<I', raw, 12)[0]
        scale, position = bounds(json.loads(raw[20:20+length]))
        generated = next(folder.glob('*.qml'))
        text = generated.read_text().replace('    id: node', '    id: node\n    scale: Qt.vector3d(%s,%s,%s)\n    position: Qt.vector3d(%s,%s,%s)' % (scale, scale, scale, *position), 1)
        generated.write_text(text)
        generated.rename(folder / 'Printer.qml')
        manifest[source.name] = hashlib.sha256(raw).hexdigest()
    (output / 'sources.json').write_text(json.dumps(manifest, indent=2) + '\n')
    with (root / 'qt-models.tar.gz').open('wb') as destination:
        with gzip.GzipFile(fileobj=destination, mode='wb', filename='', mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode='w') as archive:
                for file in sorted(output.rglob('*')):
                    if not file.is_file(): continue
                    info = archive.gettarinfo(str(file), str(file.relative_to(output)))
                    info.uid = info.gid = info.mtime = 0
                    info.uname = info.gname = ''
                    info.mode = 0o644
                    with file.open('rb') as stream: archive.addfile(info, stream)
print(root / 'qt-models.tar.gz')
