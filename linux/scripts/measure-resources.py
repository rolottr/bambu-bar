#!/usr/bin/env python3
"""Sample Bambu Bar and GNOME Shell without requiring an attached profiler.

Usage: python3 measure-resources.py SCENARIO SECONDS OUTPUT.json
CPU is percent of one logical CPU, measured from process CPU-time deltas.
RSS includes shared mappings; PSS divides shared pages between processes.
Run each scenario after startup settles and with builds/captures stopped.
"""
import json
import os
from pathlib import Path
import sys
import time


def sample(pid):
    process = Path('/proc') / pid
    fields = (process / 'stat').read_text().rsplit(')', 1)[1].split()
    status = dict(line.split(':', 1) for line in (process / 'status').read_text().splitlines() if ':' in line)
    pss = None
    try:
        for line in (process / 'smaps_rollup').read_text().splitlines():
            if line.startswith('Pss:'):
                pss = int(line.split()[1]) / 1024
                break
    except (OSError, ValueError):
        pass
    return {
        'time': time.monotonic(),
        'cpu': (int(fields[11]) + int(fields[12])) / os.sysconf('SC_CLK_TCK'),
        'rss': int(status.get('VmRSS', '0 kB').split()[0]) / 1024,
        'pss': pss,
        'threads': int(status['Threads']),
    }


def main():
    if len(sys.argv) != 4 or not sys.argv[2].isdigit() or int(sys.argv[2]) < 1:
        raise SystemExit(__doc__)
    label, duration, output = sys.argv[1], int(sys.argv[2]), Path(sys.argv[3])
    processes = {}
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            name = (process / 'comm').read_text().strip()
        except OSError:
            continue
        if name in ('bambu-bar', 'gnome-shell'):
            processes[process.name] = {'name': name, 'samples': []}
    for index in range(duration + 1):
        for pid, process in processes.items():
            try:
                process['samples'].append(sample(pid))
            except OSError:
                process['exited'] = True
        if index < duration:
            time.sleep(1)
    result = {'scenario': label, 'duration_seconds': duration,
              'cpu_units': 'percent of one logical CPU', 'processes': []}
    for pid, process in processes.items():
        samples = process.pop('samples')
        if len(samples) < 2:
            continue
        first, last = samples[0], samples[-1]
        peaks = [100 * (b['cpu'] - a['cpu']) / (b['time'] - a['time'])
                 for a, b in zip(samples, samples[1:])]
        result['processes'].append({
            'pid': int(pid), **process,
            'cpu_average': round(100 * (last['cpu'] - first['cpu']) / (last['time'] - first['time']), 3),
            'cpu_peak_1s': round(max(peaks), 2),
            'rss_start_mib': round(first['rss'], 2), 'rss_end_mib': round(last['rss'], 2),
            'rss_peak_mib': round(max(s['rss'] for s in samples), 2),
            'pss_end_mib': round(last['pss'], 2) if last['pss'] is not None else None,
            'threads': last['threads'],
        })
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
