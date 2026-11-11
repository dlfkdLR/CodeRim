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
        values = value if isinstance(value, tuple) else (value, value, value)
        self.assertEqual(result.stdout, ''.join(f'{key}={str(selected).lower()}\n'
            for key, selected in zip(['macos', 'ios', 'release'], values)))

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
        for path in ['Tests/Fixtures/pairing.txt', '.gitattributes', 'Package.swift', 'Package.resolved',
                     'Sources/CodeRimShared/MobileModels.swift', 'Config/Info.plist',
                     'Sources/Shared.swift', 'Scripts/test.sh', '.github/workflows/ci.yml', 'unknown/source.txt']:
            with self.subTest(path=path):
                self.git('reset', '--hard', '--quiet', self.base)
                self.write(path)
                self.commit()
                self.expect(True)

    def test_ios_changes_select_only_ios(self):
        for path in ['iOS/CodeRimMobile/MobileAppModel.swift', 'iOS/CodeRimLiveActivity/CodeRimLiveViews.swift',
                     'iOS/CodeRimMobileTests/MobileContractTests.swift', 'iOS/generate_project.py',
                     'iOS/Config/CodeRimMobile-Info.plist', 'iOS/CodeRimMobile.xcodeproj/project.pbxproj']:
            with self.subTest(path=path):
                self.git('reset', '--hard', '--quiet', self.base)
                self.write(path)
                self.commit()
                self.expect((False, True, False))

    def test_mac_runtime_changes_select_tests_and_release(self):
        for path in ['Sources/CodeRim/Notch/NotchViewModel.swift', 'Sources/CodeRimUsageCore/TokenUsage.swift',
                     'Sources/CodeRimCLI/main.swift', 'Sources/CodeRimClaudeBridge/main.swift',
                     'Sources/ClaudeBridgeCore/ClaudeRateLimitCodec.swift', 'Sources/CSQLite/module.modulemap',
                     'Sources/CodeRimWidget/CodeRimWidget.swift', 'WidgetExtension/CodeRimWidget.xcodeproj/project.pbxproj',
                     'Assets/AppIcon.icns']:
            with self.subTest(path=path):
                self.git('reset', '--hard', '--quiet', self.base)
                self.write(path)
                self.commit()
                self.expect((True, False, True))

    def test_mac_test_changes_skip_ios_and_release(self):
        for path in ['Tests/CodeRimTests/Providers/ProviderMatrixTests.swift',
                     'Tests/CodeRimUsageCoreTests/UsageCoreTests.swift',
                     'Tests/CodeRimCompanionTests/CompanionTests.swift']:
            with self.subTest(path=path):
                self.git('reset', '--hard', '--quiet', self.base)
                self.write(path)
                self.commit()
                self.expect((True, False, False))

    def test_mixed_platform_changes_select_both_and_release(self):
        self.write('Sources/CodeRim/Notch/NotchViewModel.swift')
        self.write('iOS/CodeRimMobile/MobileAppModel.swift')
        self.commit()
        self.expect(True)

    def test_deleted_or_moved_files_keep_the_original_platform_checks(self):
        for path, expected in [('iOS/CodeRimMobile/App.swift', (False, True, False)),
                               ('Sources/CodeRim/App.swift', (True, False, True)),
                               ('Sources/CodeRimShared/MobileModels.swift', (True, True, True))]:
            for moved in [False, True]:
                with self.subTest(path=path, moved=moved):
                    self.git('reset', '--hard', '--quiet', self.base)
                    self.write(path)
                    self.commit()
                    original = self.git('rev-parse', 'HEAD').strip()
                    if moved:
                        self.write('Windows/placeholder.txt')
                        self.git('mv', path, 'Windows/moved.swift')
                    else:
                        self.git('rm', path)
                    self.commit()
                    self.expect(expected, [original])

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
