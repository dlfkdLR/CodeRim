#!/usr/bin/env python3
"""Synthetic temporary-file tests only; no signing, certificate, install or release operations."""
from contextlib import contextmanager
from types import SimpleNamespace
from unittest import mock
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('payload', Path(__file__).with_name('update_payload_manifest.py'))
payload = importlib.util.module_from_spec(spec)
spec.loader.exec_module(payload)


class PayloadTests(unittest.TestCase):
    def setUp(self):
        base = '/private/tmp' if Path('/private/tmp').is_dir() else None
        self.temporary = tempfile.TemporaryDirectory(prefix='coderim-manifest-test-', dir=base)
        self.root = Path(self.temporary.name) / 'payload'
        self.root.mkdir()
        (self.root / 'CodeRim.exe').write_bytes(b'synthetic app, not an executable')
        (self.root / 'CodeRimCLI.exe').write_bytes(b'synthetic CLI, not an executable')

    def tearDown(self):
        self.temporary.cleanup()

    def test_hash_and_resource_layout(self):
        result = payload.inventory(self.root, '2.2.1', 'arm64')
        self.assertEqual(2, len(result['files']))
        for file in result['files']:
            self.assertEqual(hashlib.sha256((self.root / file['path']).read_bytes()).hexdigest(), file['sha256'])
        data = payload.encode(result)
        self.assertEqual(data, payload.encode(payload.inventory(self.root, '2.2.1', 'arm64')))
        resource = payload.resource(data)
        self.assertEqual((0, 32), struct.unpack_from('<II', resource))
        self.assertEqual((len(data), 32, 65535, 10, 65535, 21001), struct.unpack_from('<IIHHHH', resource, 32))
        self.assertEqual(0, struct.unpack_from('<H', resource, 32 + 22)[0])
        self.assertEqual(data, resource[64:64 + len(data)])
        self.assertEqual(0, len(resource) % 4)

    def test_windows_cached_directory_identity_is_not_used_for_open_file_identity(self):
        original_scandir = os.scandir

        class CachedWindowsEntry:
            def __init__(self, entry):
                self.name, self.path = entry.name, entry.path
                self.entry = entry

            def stat(self, *, follow_symlinks=True):
                info = self.entry.stat(follow_symlinks=follow_symlinks)
                values = {name: getattr(info, name) for name in dir(info) if name.startswith('st_')}
                values.update(st_dev=0, st_ino=0, st_nlink=0)
                return SimpleNamespace(**values)

        @contextmanager
        def cached_scandir(path):
            with original_scandir(path) as entries:
                yield [CachedWindowsEntry(entry) for entry in entries]

        with mock.patch.object(payload.os, 'scandir', side_effect=cached_scandir):
            result = payload.inventory(self.root, '2.2.1', 'x64')
        self.assertEqual(2, len(result['files']))
        for file in result['files']:
            self.assertEqual(hashlib.sha256((self.root / file['path']).read_bytes()).hexdigest(), file['sha256'])

    def test_same_size_replacement_between_stat_and_open_is_rejected(self):
        original_open = os.open
        replaced = False

        def replace_before_open(path, flags, *args, **kwargs):
            nonlocal replaced
            if not replaced and Path(path) == self.root / 'CodeRim.exe':
                replaced = True
                replacement = self.root.parent / 'replacement'
                replacement.write_bytes(b'x' * Path(path).stat().st_size)
                replacement.replace(path)
            return original_open(path, flags, *args, **kwargs)

        with mock.patch.object(payload.os, 'open', side_effect=replace_before_open), self.assertRaisesRegex(ValueError, 'identity changed'):
            payload.inventory(self.root, '2.2.1', 'x64')
        self.assertTrue(replaced)

    def test_path_rejections(self):
        for path in ('../a', '/a', 'a/b/../c', 'a\\b', 'a:b', 'a.', 'a ', 'CON', 'dir/LPT1.txt', 'a//b', 'x/.coderim-install.json', '.coderim-install.json', 'a' * 121, '/'.join(['a'] * 17)):
            with self.subTest(path=path), self.assertRaises(ValueError):
                payload.check_path(path)

    def test_versions_architecture(self):
        for version in ('02.2.1', '2.2', '2.2.1.0', '2.2.1-beta', '2147483648.0.0'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                payload.inventory(self.root, version, 'x64')
        with self.assertRaises(ValueError): payload.inventory(self.root, '2.2.1', 'x86')

    def test_worker_must_not_be_in_preworker_manifest(self):
        (self.root / payload.WORKER).write_bytes(b'fixture')
        with self.assertRaises(ValueError): payload.inventory(self.root, '2.2.1', 'x64')

    def test_both_products_required(self):
        (self.root / 'CodeRimCLI.exe').unlink()
        with self.assertRaises(ValueError): payload.inventory(self.root, '2.2.1', 'x64')

    def test_links_rejected_without_reading_target(self):
        try: (self.root / 'linked').symlink_to('CodeRim.exe')
        except OSError: self.skipTest('Host cannot create a synthetic symlink')
        with self.assertRaises(ValueError): payload.inventory(self.root, '2.2.1', 'x64')

    def test_size_limit_checked_before_file_read(self):
        path = self.root / 'too-large'
        with path.open('wb') as stream: stream.truncate(payload.MAX_FILE + 1)
        with self.assertRaises(ValueError): payload.inventory(self.root, '2.2.1', 'x64')

    def test_outputs_are_new_and_outside_payload(self):
        outside = self.root.parent / 'payload.json'
        payload.write_new(outside, b'{}', self.root)
        with self.assertRaises(ValueError): payload.write_new(outside, b'changed', self.root)
        self.assertEqual(b'{}', outside.read_bytes())
        with self.assertRaises(ValueError): payload.write_new(self.root / 'payload.json', b'{}', self.root)
        with self.assertRaises(ValueError): payload.write_new(self.root.parent / 'payload' / '..' / 'payload' / 'inside.json', b'{}', self.root)


if __name__ == '__main__':
    unittest.main()
