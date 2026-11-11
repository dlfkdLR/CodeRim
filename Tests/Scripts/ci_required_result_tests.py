#!/usr/bin/env python3
"""Pin the required CI result for skipped, failed and cancelled platform jobs."""
from itertools import product
import os
from pathlib import Path
import re
import subprocess
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'Scripts/ci_required_result.sh'
ROOT = SCRIPT.parents[1]


class RequiredResultTests(unittest.TestCase):
    def test_only_valid_success_or_unneeded_apple_tests_pass(self):
        allowed = {('success', 'true', 'success'), ('success', 'false', 'success'),
                   ('success', 'false', 'skipped')}
        states = ['success', 'failure', 'cancelled', 'skipped']
        for values in product(states, ['true', 'false', ''], states):
            with self.subTest(values=values):
                result = subprocess.run(['bash', str(SCRIPT), *values], capture_output=True, timeout=5)
                self.assertEqual(result.returncode == 0, values in allowed)

    def test_missing_or_extra_results_cannot_pass(self):
        for values in [[], ['success', 'true'], ['success', 'true', 'success', 'extra']]:
            result = subprocess.run(['bash', str(SCRIPT), *values], capture_output=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)

    def test_workflow_gates_reject_each_failed_or_missing_required_job(self):
        gates = [
            ('ci.yml', 'build-and-test', 'checks',
             {'apple-tests': 'macos', 'ios-tests': 'ios', 'release-build': 'release'}),
            ('windows.yml', 'test-and-package', 'windows-changes',
             {'windows-build': 'windows', 'native-x64': 'windows'}),
            ('windows.yml', 'native-arm64', 'windows-changes',
             {'windows-arm64-core': 'windows', 'windows-arm64-tests': 'windows'}),
        ]
        for workflow, job, selector, required in gates:
            source = (ROOT / '.github/workflows' / workflow).read_text()
            block = re.search(rf'^  {job}:\n(.*?)(?=^  [\w-]+:|\Z)', source, re.M | re.S).group(1)
            needs = re.search(r'^    needs: \[(.+)\]$', block, re.M).group(1).split(', ')
            self.assertEqual(set(needs), {selector, *required}, job)
            self.assertIn('always()', block, job)
            bindings = re.findall(r'^          (\w+): \$\{\{ needs\.([\w-]+)\.(result|outputs\.\w+) \}\}$', block, re.M)
            command = re.search(r'^        run: \|\n((?:          .*\n?)+)', block, re.M).group(1)
            command = '\n'.join(line[10:] for line in command.splitlines())

            def run(results, selected):
                values = {f'{name}.result': value for name, value in results.items()}
                values.update({f'{selector}.outputs.{name}': value for name, value in selected.items()})
                env = {**os.environ, **{name: values.get(f'{owner}.{field}', '') for name, owner, field in bindings}}
                return subprocess.run(['bash', '-e', '-c', command], cwd=ROOT, env=env,
                                      capture_output=True, timeout=5).returncode

            results = {name: 'success' for name in [selector, *required]}
            selected = {name: 'true' for name in required.values()}
            self.assertEqual(run(results, selected), 0, job)
            for name in results:
                for state in ['failure', 'cancelled', 'skipped', '']:
                    with self.subTest(workflow=workflow, gate=job, dependency=name, state=state):
                        self.assertNotEqual(run({**results, name: state}, selected), 0)
            for flag in selected:
                self.assertNotEqual(run(results, {**selected, flag: ''}), 0, job)
            # Only explicitly unneeded work may be skipped; shared selectors cover every job they select.
            for flags in product(['true', 'false'], repeat=len(selected)):
                choices = dict(zip(selected, flags))
                outcomes = {selector: 'success', **{name: 'success' if choices[flag] == 'true' else 'skipped'
                                                   for name, flag in required.items()}}
                self.assertEqual(run(outcomes, choices), 0, (job, choices))


if __name__ == '__main__':
    unittest.main()
