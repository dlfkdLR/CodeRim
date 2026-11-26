#!/usr/bin/env python3
"""Compare complete catalog projections and isolated bad readings in the desktop companion publisher."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = '14c332b30fd35cd266b40a4f9384b4b065b63707'
PUBLISHER = 'Sources/CodeRim/Services/CompanionSnapshotPublisher.swift'


def build(work, arch, version):
    def source(path):
        if version == 'release':
            return subprocess.check_output(['git', 'show', f'{RELEASE}:{path}'], cwd=ROOT, text=True)
        return (ROOT / path).read_text()
    src = source(PUBLISHER)
    def method(name):
        start = src.index('    static func ' + name + '(')
        end = src.index('\n    }', start) + len('\n    }')
        return src[start:end]
    if version == 'release':
        # Preserve the tag's catalog loop, disabled branch and validation. Inject
        # deterministic readings where the real publisher consults its live stores.
        start = src.index('        let providers = CompanionProviderID.allCases.map')
        switch = src.index('            switch id {', start)
        end = src.index('        let snapshot = CompanionSnapshot(', switch)
        snapshot = src[end:src.index('\n', end)]
        assert 'providers: providers.map(Self.encodable)' in snapshot
        body = src[start:switch].replace('notch.selectedProviderIDs', 'selected')
        projection = ('    static func projectedProviders(selected: Set<String>, '
                      'reading: (CompanionProviderID) -> CompanionProvider) -> [CompanionProvider] {\n'
                      + body + '            return reading(id)\n        }\n        return providers.map(Self.encodable)\n    }')
    else:
        projection = method('projectedProviders')
    folder = work / version
    folder.mkdir()
    publisher = folder / 'Publisher.swift'
    publisher.write_text('import Foundation\nenum Publisher {\n' + projection + '\n' +
                         method('disabled') + '\n' + method('encodable') + '\n}\n')
    sources = [publisher, HERE / 'main.swift']
    for name in ('CompanionProviderID', 'CompanionSnapshot'):
        path = folder / (name + '.swift')
        path.write_text(source('Sources/CodeRimShared/' + path.name))
        sources.append(path)
    exe = folder / 'project'
    subprocess.run(['swiftc', '-target', arch+'-apple-macos14.0', '-swift-version', '6', '-O',
                    '-whole-module-optimization', '-warnings-as-errors', *sources, '-o', exe], check=True)
    return exe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--arch', choices=['x86_64','arm64'], default=platform.machine())
    parser.add_argument('--benchmark-output', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='coderim-companion-projection-') as temp:
        bins = {k: build(Path(temp), args.arch, k) for k in ('release', 'candidate')}
        outputs = [subprocess.check_output([exe]) for exe in bins.values()]
        if outputs[0] != outputs[1]:
            raise ValueError('Projected providers differ from v2.1.41')
        print(f'{args.arch}: {len(outputs[0].splitlines())} full catalog snapshots; '
              f'{hashlib.sha256(outputs[0]).hexdigest()}', flush=True)
        if args.benchmark_output:
            observations = []
            for count in (0, 1, 8, 35, 70):
                times = {k: [] for k in bins}
                reference = None
                for pair in range(7):
                    order = list(bins) if pair % 2 == 0 else list(reversed(bins))
                    for key in order:
                        total, ns = subprocess.check_output([bins[key], str(count), '80']).split()
                        if reference is not None and total != reference:
                            raise ValueError('Timed provider states changed')
                        reference = total
                        times[key].append(float(ns))
                observations.append({'selected': count, 'samplesNs': times,
                                     'medianNs': {k:statistics.median(v) for k,v in times.items()}})
            args.benchmark_output.write_text(json.dumps(observations, indent=2)+'\n')


if __name__ == '__main__':
    main()
