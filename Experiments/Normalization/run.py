#!/usr/bin/env python3
"""Build the isolated macOS trial, compare real language bindings, and record measurements."""
import hashlib
import json
import os
import platform
import re
import statistics
import subprocess
import sys
from pathlib import Path

TRIAL = Path(__file__).resolve().parent
ROOT = TRIAL.parents[1]
BUILD = ROOT / '.build' / 'normalization-trial'
FIXTURE = ROOT / 'Tests/Fixtures/usage-normalization-v1.json'


def run(command, environment=None):
    result = subprocess.run([str(item) for item in command], cwd=ROOT, text=True, capture_output=True,
                            env=None if environment is None else {**os.environ, **environment})
    if result.returncode:
        raise RuntimeError(f'{command}\n{result.stdout}\n{result.stderr}')
    return result


def measure(command, environment=None):
    result = run(['/usr/bin/time', '-l', *command], environment)
    report = json.loads(result.stdout)
    match = re.search(r'(\d+)\s+maximum resident set size', result.stderr)
    if not match:
        raise RuntimeError('macOS time did not report peak resident memory')
    report['peakResidentBytes'] = int(match.group(1))
    for key in ('nativeNanoseconds', 'rustNanoseconds', 'pipelineNativeNanoseconds', 'pipelineRustNanoseconds'):
        if report[key]:
            report[key.replace('Nanoseconds', 'MedianNanoseconds')] = statistics.median(report[key][1:])
    return report


def main():
    if sys.platform != 'darwin':
        raise SystemExit('This isolated trial currently measures macOS only.')
    BUILD.mkdir(parents=True, exist_ok=True)
    run(['cargo', 'test', '--manifest-path', TRIAL / 'Cargo.toml', '--target-dir', BUILD / 'rust'])
    run(['cargo', 'build', '--manifest-path', TRIAL / 'Cargo.toml', '--release', '--target-dir', BUILD / 'rust'])
    library = BUILD / 'rust/release/libcoderim_normalization_trial.dylib'
    sources = ROOT / 'Sources/CodeRimUsageCore'
    run(['swiftc', '-package-name', 'CodeRim', '-O', '-warnings-as-errors', '-I', TRIAL, '-L', library.parent,
         '-lcoderim_normalization_trial', '-Xlinker', '-rpath', '-Xlinker', library.parent,
         sources / 'TokenUsage.swift', sources / 'UsageSnapshot.swift', sources / 'CodexTokenObservation.swift',
         sources / 'UsageNormalizer.swift', sources / 'UsageEvent.swift',
         ROOT / 'Sources/CodeRimUsageCore/CodexJSONLParser.swift',
         TRIAL / 'Swift/main.swift', '-o', BUILD / 'swift-trial'])
    run(['dotnet', 'build', TRIAL / 'DotNet/Trial.csproj', '-c', 'Release', '-o', BUILD / 'dotnet'])
    reports = []
    for driver in ([BUILD / 'swift-trial', FIXTURE],
                   ['dotnet', BUILD / 'dotnet/Trial.dll', FIXTURE, library]):
        for mode in ('both', 'native', 'rust'):
            reports.append(measure([*driver, mode], {'DOTNET_TieredCompilation': '0'} if driver[0] == 'dotnet' else None))
    tracked = [FIXTURE, *TRIAL.rglob('*.rs'), *TRIAL.rglob('*.swift'), TRIAL / 'DotNet/Program.cs',
               TRIAL / 'run.py', TRIAL / 'Cargo.toml', TRIAL / 'normalization.h', TRIAL / 'module.modulemap',
               TRIAL / 'DotNet/Trial.csproj', sources / 'TokenUsage.swift', sources / 'UsageNormalizer.swift',
               sources / 'UsageSnapshot.swift', sources / 'CodexTokenObservation.swift',
               sources / 'UsageEvent.swift', ROOT / 'Sources/CodeRimUsageCore/CodexJSONLParser.swift',
               ROOT / 'Windows/src/CodeRim.UsageCore/Domain/UsageModels.cs',
               ROOT / 'Windows/src/CodeRim.UsageCore/Parsing/CodexJsonlParser.cs',
               ROOT / 'Windows/src/CodeRim.UsageCore/Domain/TokenUsage.cs',
               ROOT / 'Windows/src/CodeRim.UsageCore/Domain/UsageNormalizer.cs']
    report = {'platform': platform.platform(), 'machine': platform.machine(),
              'swift': run(['swiftc', '--version']).stdout.strip(),
              'rust': run(['rustc', '--version']).stdout.strip(),
              'dotnet': run(['dotnet', '--version']).stdout.strip(),
              'dotnetEnvironment': {'DOTNET_TieredCompilation': '0'},
              'inputHashes': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in tracked},
              'measurements': reports,
              'scope': 'Synthetic macOS normalization and JSON-parse-plus-normalization benchmark; '
                       'peak RSS includes runtime, fixtures and both binding checks. '
                       'First timing sample is warm-up. C# allocated bytes cover the benchmark thread. '
                       'This is not end-to-end app or native Windows performance.'}
    destination = BUILD / 'results.json'
    destination.write_text(json.dumps(report, indent=2) + '\n')
    print(destination)
    for row in reports:
        print(row['language'], row['mode'], 'native ns:', row.get('nativeMedianNanoseconds'),
              'Rust ns:', row.get('rustMedianNanoseconds'), 'peak bytes:', row['peakResidentBytes'])


if __name__ == '__main__':
    main()
