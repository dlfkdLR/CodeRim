#!/usr/bin/env python3
"""Exercise cached source timestamps using disposable Git worktrees only."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / 'Scripts/ci_source_mtimes.py'


class SourceMtimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='coderim-source-mtimes-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        subprocess.run(['git', 'init', '--quiet', str(self.root)], check=True)
        self.source = self.root / 'source with spaces.swift'
        self.source.write_bytes(b'let count = 1\n')
        os.utime(self.source, ns=(1_700_000_000_000_000_000,) * 2)
        subprocess.run(['git', '-C', str(self.root), 'add', '.'], check=True)

    def run_helper(self, command, *args, success=True):
        result = subprocess.run([sys.executable, str(SCRIPT), command, *args], cwd=self.root,
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
        return result

    def test_matching_bytes_restore_the_original_mtime_only(self):
        before = self.source.stat().st_mtime_ns
        self.run_helper('save')
        os.utime(self.source, ns=(before + 5_000_000_000,) * 2)
        self.run_helper('restore')
        self.assertEqual(self.source.stat().st_mtime_ns, before)
        self.assertEqual(self.source.read_bytes(), b'let count = 1\n')

    def test_changed_bytes_keep_the_checkout_mtime(self):
        self.run_helper('save', '--manifest', 'cache/state.json')
        self.source.write_bytes(b'let count = 2\n')
        os.utime(self.source, ns=(1_710_000_000_000_000_000,) * 2)
        before = self.source.stat().st_mtime_ns
        self.run_helper('restore', '--manifest', 'cache/state.json')
        self.assertEqual(self.source.stat().st_mtime_ns, before)
        self.assertEqual(self.source.read_bytes(), b'let count = 2\n')

    def test_deleted_files_and_links_are_not_restored(self):
        target = self.root / 'target.txt'
        target.write_text('untracked bytes')
        link = self.root / 'tracked-link'
        link.symlink_to(target)
        subprocess.run(['git', '-C', str(self.root), 'add', 'tracked-link'], check=True)
        self.run_helper('save')
        manifest = self.root / '.build/ci-source-mtimes.json'
        state = json.loads(manifest.read_text())
        self.assertNotIn('tracked-link', state)
        self.source.unlink()
        self.source.symlink_to(target)
        before = target.stat().st_mtime_ns
        self.run_helper('restore')
        self.assertEqual(target.stat().st_mtime_ns, before)
        self.assertTrue(self.source.is_symlink())
        self.source.unlink()
        self.run_helper('restore')
        self.assertFalse(self.source.exists())

    def test_cache_miss_is_a_read_only_noop(self):
        before = self.source.stat().st_mtime_ns
        self.run_helper('restore')
        self.assertEqual(self.source.stat().st_mtime_ns, before)
        self.assertFalse((self.root / '.build').exists())

    def test_manifest_cannot_overwrite_sources_or_escape_the_checkout(self):
        for path in ['source with spaces.swift', '../outside.json']:
            self.run_helper('save', '--manifest', path, success=False)
        (self.root / 'cache-link').symlink_to(self.root.parent, target_is_directory=True)
        self.run_helper('save', '--manifest', 'cache-link/state.json', success=False)
        self.assertEqual(self.source.read_bytes(), b'let count = 1\n')


if __name__ == '__main__':
    unittest.main()
