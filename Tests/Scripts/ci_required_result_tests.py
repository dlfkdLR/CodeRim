#!/usr/bin/env python3
"""Pin the required CI result for skipped, failed and cancelled platform jobs."""
from itertools import product
from pathlib import Path
import subprocess
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'Scripts/ci_required_result.sh'


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


if __name__ == '__main__':
    unittest.main()
