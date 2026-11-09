import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('verify_acceptance_host', ROOT / 'scripts/verify-acceptance-host.py')
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class AcceptanceHostLockTests(unittest.TestCase):
    def fixture(self, root):
        host = root / 'host'
        (host / 'src/Workbench.Sdk').mkdir(parents=True)
        sdk_source = host / 'src/Workbench.Sdk/PluginContracts.cs'
        sdk_source.write_bytes(b'locked public sdk\n')
        (host / 'host.txt').write_text('host code\n')
        subprocess.run(['git', 'init', '-q', str(host)], check=True)
        subprocess.run(['git', '-C', str(host), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(host), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'fixture'], check=True)
        host_commit = subprocess.check_output(['git', '-C', str(host), 'rev-parse', 'HEAD'], text=True).strip()
        sdk = {'sourceCommit': 'a' * 40, 'sdkVersion': '0.4.0', 'sources': [{'path': 'src/Workbench.Sdk/PluginContracts.cs', 'sha256': hashlib.sha256(sdk_source.read_bytes()).hexdigest()}]}
        acceptance = {'schemaVersion': 1, 'repository': 'https://github.com/ThaoKeeton/project-workbench.git', 'sourceCommit': host_commit, 'sdkSourceCommit': sdk['sourceCommit'], 'sdkVersion': '0.4.0'}
        return host, sdk, acceptance

    def test_distinct_host_and_sdk_commits_with_identical_locked_sources_are_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            host, sdk, acceptance = self.fixture(Path(temporary))
            self.assertNotEqual(acceptance['sourceCommit'], sdk['sourceCommit'])
            check.verify_acceptance_host(host, sdk, acceptance)

    def test_wrong_host_commit_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            host, sdk, acceptance = self.fixture(Path(temporary)); acceptance['sourceCommit'] = 'b' * 40
            with self.assertRaisesRegex(ValueError, 'HostSourceCommitMismatch'):
                check.verify_acceptance_host(host, sdk, acceptance)

    def test_changed_sdk_source_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            host, sdk, acceptance = self.fixture(Path(temporary))
            (host / 'src/Workbench.Sdk/PluginContracts.cs').write_text('changed\n')
            with self.assertRaisesRegex(ValueError, 'HostSdkSourceHashMismatch'):
                check.verify_acceptance_host(host, sdk, acceptance)

    def test_dirty_non_sdk_host_source_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            host, sdk, acceptance = self.fixture(Path(temporary)); (host / 'host.txt').write_text('changed\n')
            with self.assertRaisesRegex(ValueError, 'HostAcceptanceSourceDirty'):
                check.verify_acceptance_host(host, sdk, acceptance)

    def test_acceptance_lock_cannot_change_sdk_identity(self):
        with tempfile.TemporaryDirectory() as temporary:
            host, sdk, acceptance = self.fixture(Path(temporary)); acceptance['sdkSourceCommit'] = 'c' * 40
            with self.assertRaisesRegex(ValueError, 'HostAcceptanceLockInvalid'):
                check.verify_acceptance_host(host, sdk, acceptance)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell is required for host profile checks')
    def test_profiles_keep_sdk_and_acceptance_host_commits_separate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            host, sdk, acceptance = self.fixture(root)
            (root / 'eng').mkdir(); (root / 'scripts').mkdir()
            (root / 'eng/host-sdk.lock.json').write_text(json.dumps(sdk))
            (root / 'eng/host-acceptance.lock.json').write_text(json.dumps(acceptance))
            for name in ['TestingPluginDelivery.psm1', 'TestingManagedPayloadAudit.cs']:
                shutil.copyfile(ROOT / 'scripts' / name, root / 'scripts' / name)
            module = str(root / 'scripts/TestingPluginDelivery.psm1').replace("'", "''")
            command = ("$ErrorActionPreference='Stop'; Import-Module '" + module + "' -Force; "
                       "& (Get-Module TestingPluginDelivery) { (Get-TestingPinnedSdkCommit) + ':' + (Get-TestingPinnedHostCommit) }")
            for profile, expected in [('production', sdk['sourceCommit']), ('source-acceptance', acceptance['sourceCommit'])]:
                result = subprocess.run(['pwsh', '-NoProfile', '-Command', command], text=True, capture_output=True,
                                        env={**os.environ, 'WORKBENCH_HOST_PROFILE': profile})
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(sdk['sourceCommit'] + ':' + expected, result.stdout.strip())
            result = subprocess.run(['pwsh', '-NoProfile', '-Command', command], text=True, capture_output=True,
                                    env={**os.environ, 'WORKBENCH_HOST_PROFILE': 'arbitrary'})
            self.assertNotEqual(0, result.returncode)
            self.assertIn('TestingHostProfileInvalid', result.stderr)


if __name__ == '__main__':
    unittest.main()
