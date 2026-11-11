#!/usr/bin/env python3
"""Verify native Windows selection using real disposable Git histories."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'Scripts/ci_windows_changes.sh'


class WindowsChangesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='coderim-windows-changes-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.env = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_SYSTEM=os.devnull)
        self.git('init', '--quiet')
        self.git('config', 'user.name', 'CI fixture')
        self.git('config', 'user.email', 'ci@example.invalid')
        self.write('Windows/original.cs')
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
        result = subprocess.run(['bash', str(SCRIPT), self.base if base is None else base],
                                cwd=self.root, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, f'windows={str(value).lower()}\n')

    def test_unchanged_tree_needs_no_windows_runner(self):
        self.expect(False)

    def test_docs_apple_and_relay_changes_need_no_windows_runner(self):
        for path in ['Documentation/plan.md', 'README.md', 'Sources/CodeRim/App.swift',
                     'MobileRelay/src/http.mjs', '.github/workflows/ci.yml']:
            self.write(path)
        self.commit()
        self.expect(False)

    def test_each_windows_or_shared_input_requires_windows(self):
        for path in ['Windows/new.cs', 'Sources/CodeRim/Resources/ProviderScripts/provider.mjs',
                     'Tests/Fixtures/contract.json', '.gitattributes', '.github/workflows/windows.yml',
                     'Scripts/ci_windows_changes.sh', 'Scripts/ci_required_result.sh']:
            with self.subTest(path=path):
                self.base = self.git('rev-parse', 'HEAD').strip()
                self.write(path)
                self.commit()
                self.expect(True)

    def test_windows_landing_readmes_need_no_windows_runner(self):
        for path in ['Windows/README.md', 'Windows/README.ko.md']:
            with self.subTest(path=path):
                self.write(path)
                self.commit()
                self.expect(False)

    def test_readme_mixed_with_source_still_requires_windows(self):
        self.write('Windows/README.md')
        self.write('Windows/README.ko.md')
        self.write('Windows/src/App.cs')
        self.commit()
        self.expect(True)

    def test_packaged_markdown_still_requires_windows(self):
        self.write('Windows/ThirdParty/NOTICE.md')
        self.commit()
        self.expect(True)

    def test_moving_windows_code_to_an_excluded_tree_still_requires_windows(self):
        (self.root / 'Documentation').mkdir()
        self.git('mv', 'Windows/original.cs', 'Documentation/original.md')
        self.commit()
        self.expect(True)

    def test_unknown_or_initial_base_conservatively_requires_windows(self):
        for base in ['', '0' * 40, 'invalid-base']:
            self.expect(True, base=base)


if __name__ == '__main__':
    unittest.main()
