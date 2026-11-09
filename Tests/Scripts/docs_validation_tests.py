#!/usr/bin/env python3
"""The documentation gate rejects missing canonical indexes and catalogue drift."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class DocumentationValidationTests(unittest.TestCase):
    def reject_overlay(self, path, text, error):
        with tempfile.TemporaryDirectory(prefix='coderim-docs-') as directory:
            overlay = Path(directory)
            target = overlay / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text)
            result = subprocess.run([sys.executable, str(ROOT / 'Scripts/validate_docs.py'),
                                     '--overlay', str(overlay)], capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn(error, json.loads(result.stdout)['errors'])

    def test_readmes_link_their_canonical_provider_index(self):
        for path, index in [('README.md', 'docs/providers.md'), ('README.ko.md', 'docs/ko/providers.md')]:
            with self.subTest(path=path):
                text = (ROOT / path).read_text()
                self.assertIn(f']({index})', text)
                self.reject_overlay(path, text.replace(f']({index})', '](docs/usage.md)'),
                                    f'{path}: link to the canonical provider index: {index}')

    def test_canonical_catalogue_cannot_drop_a_provider(self):
        path = 'docs/providers.md'
        text = (ROOT / path).read_text()
        row = next(line for line in text.splitlines() if '](' in line and '`codex`' in line)
        self.reject_overlay(path, text.replace(row + '\n', ''),
                            f'{path}: catalogue does not match shared source IDs')


if __name__ == '__main__':
    unittest.main()
