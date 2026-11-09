#!/usr/bin/env python3
"""Verify CI path selection with real commits in disposable Git repositories."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / 'Scripts/ci_apple_changes.sh'


class AppleChangesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='coderim-apple-changes-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_SYSTEM=os.devnull)
        self.git('init', '--quiet')
        self.git('config', 'user.name', 'CI fixture')
        self.git('config', 'user.email', 'ci@example.invalid')
        self.write('Sources/original.swift')
        self.commit()
        self.base = self.git('rev-parse', 'HEAD').strip()

    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.root, env=self.env, text=True)

    def write(self, name):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('fixture\n')

    def commit(self):
        self.git('add', '.')
        self.git('commit', '--quiet', '-m', 'fixture')

    def expect(self, value, base=None):
        result = subprocess.run(['bash', str(SCRIPT), *(base if base is not None else [self.base])],
                                cwd=self.root, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, f'apple={str(value).lower()}\n')

    def test_unchanged_checkout_skips_apple_jobs(self):
        self.expect(False)

    def test_documentation_and_nonapple_trees_skip_apple_jobs(self):
        for path in ['docs/guide.md', 'Documentation/reference.md', 'website/app.js',
                     'MobileRelay/src/http.mjs', 'README.md', 'Sources/README.md']:
            self.write(path)
        self.commit()
        self.expect(False)

    def test_windows_and_its_workflows_skip_apple_jobs(self):
        for path in ['Windows/src/App.cs', '.github/workflows/windows.yml',
                     '.github/workflows/windows-desktop-probe.yml']:
            self.write(path)
        self.commit()
        self.expect(False)

    def test_mixed_apple_and_docs_changes_run_apple_jobs(self):
        self.write('docs/guide.md')
        self.write('Sources/new.swift')
        self.commit()
        self.expect(True)

    def test_apple_file_moved_to_windows_still_runs_apple_jobs(self):
        (self.root / 'Windows').mkdir()
        self.git('mv', 'Sources/original.swift', 'Windows/original.swift')
        self.commit()
        self.assertTrue(self.git('diff', '--name-status', '--find-renames', self.base, 'HEAD').startswith('R100\t'))
        self.expect(True)

    def test_shared_and_unknown_paths_run_apple_jobs(self):
        for path in ['Tests/Fixtures/pairing.txt', '.gitattributes', 'Package.swift', 'iOS/App.swift',
                     'Sources/Shared.swift', 'Scripts/test.sh', '.github/workflows/ci.yml', 'unknown/source.txt']:
            with self.subTest(path=path):
                self.git('reset', '--hard', '--quiet', self.base)
                self.write(path)
                self.commit()
                self.expect(True)

    def test_missing_or_zero_base_runs_all_jobs(self):
        for base in [[], [''], ['0'], ['0' * 40]]:
            with self.subTest(base=base):
                self.expect(True, base)

    def test_invalid_or_unavailable_base_runs_all_jobs(self):
        for base in ['not-a-revision', 'f' * 40]:
            with self.subTest(base=base):
                self.expect(True, [base])


if __name__ == '__main__':
    unittest.main()
