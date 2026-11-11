import importlib.util
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('prepare_host_sdk', ROOT / 'scripts/prepare-host-sdk.py')
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class RepositoryContractTests(unittest.TestCase):
    def test_host_implementation_and_contract_tests_are_not_vendored(self):
        self.assertFalse((ROOT / 'src').exists())
        for name in ['PluginTestingContextTests.cs', 'ProjectTestStoreTests.cs', 'ProjectTestRunStoreTests.cs', 'HostRuntimeTests.cs', 'TestingNativeProbeDiagnosticTests.cs']:
            self.assertFalse((ROOT / 'tests/Workbench.Testing.Tests' / name).exists())

    def test_sdk_dependencies_are_exact_packages(self):
        for project, package in [('Workbench.Testing.Domain', 'Workbench.Sdk'), ('Workbench.Testing', 'Workbench.WinUI.Sdk'), ('Workbench.TestCases.Sample', 'Workbench.WinUI.Sdk')]:
            text = (ROOT / 'plugins' / project / (project + '.csproj')).read_text()
            self.assertIn('PackageReference Include="' + package + '" Version="[0.4.0]"', text)
            self.assertNotIn('../../src/', text)

    def test_host_commit_mismatch_refuses_before_sdk_reads(self):
        with patch.object(prepare.subprocess, 'check_output', return_value='f' * 40 + '\n'):
            with self.assertRaisesRegex(ValueError, 'HostSourceCommitMismatch'):
                prepare.verify_host(ROOT, {'sourceCommit': '0' * 40, 'sources': []})

    def test_sdk_hash_mismatch_is_refused(self):
        with patch.object(prepare.subprocess, 'check_output', return_value='0' * 40 + '\n'):
            with self.assertRaisesRegex(ValueError, 'HostSdkSourceHashMismatch'):
                prepare.verify_host(ROOT, {'sourceCommit': '0' * 40, 'sources': [{'path': 'global.json', 'sha256': '0' * 64}]})

    def test_crlf_host_checkout_prepares_locked_lf_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'testing'
            host = Path(temporary) / 'host'
            (root / 'eng').mkdir(parents=True)
            host.mkdir()
            canonical = b'<Project>\n  <Version>0.4.0</Version>\n</Project>\n'
            (host / 'Directory.Build.props').write_bytes(canonical.replace(b'\n', b'\r\n'))
            lock = {'sourceCommit': '0' * 40, 'sources': [{'path': 'Directory.Build.props', 'sha256': hashlib.sha256(canonical).hexdigest()}]}
            lock_path = root / 'eng/host-sdk.lock.json'
            lock_path.write_text(json.dumps(lock))

            def fake_pack(command, cwd, env, check):
                output = Path(command[command.index('-o') + 1])
                (output / (Path(command[2]).stem + '.nupkg')).write_bytes(b'fixture package')

            with patch.object(prepare, 'ROOT', root), patch.object(prepare, 'LOCK', lock_path), \
                 patch.object(prepare.subprocess, 'check_output', return_value='0' * 40 + '\n'), \
                 patch.object(prepare.subprocess, 'run', side_effect=fake_pack), \
                 patch('sys.argv', ['prepare-host-sdk', '--host-root', str(host), '--reference-only']):
                prepare.main()
            self.assertEqual(canonical, (root / 'artifacts/host-sdk/source/Directory.Build.props').read_bytes())

    def test_crlf_host_checkout_with_changed_content_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            host = Path(temporary)
            (host / 'global.json').write_bytes(b'first\r\nchanged\r\n')
            lock = {'sourceCommit': '0' * 40, 'sources': [{'path': 'global.json', 'sha256': hashlib.sha256(b'first\nsecond\n').hexdigest()}]}
            with patch.object(prepare.subprocess, 'check_output', return_value='0' * 40 + '\n'):
                with self.assertRaisesRegex(ValueError, 'HostSdkSourceHashMismatch'):
                    prepare.verify_host(host, lock)

    def test_plugin_and_host_provenance_use_separate_commits(self):
        text = (ROOT / 'scripts/TestingPluginDelivery.psm1').read_text()
        self.assertIn('Get-TestingPinnedHostCommit', text)
        self.assertIn('-SourceCommit (Get-TestingPinnedHostCommit)', text)
        self.assertIn('$metadata.host.sourceCommit', text)
        self.assertNotIn('-AppDirectory $AppDirectory -SourceCommit $metadata.sourceCommit', text)

    def test_ci_is_public_safe_and_never_uses_real_services(self):
        text = (ROOT / '.github/workflows/build.yml').read_text()
        self.assertIn('pull_request:', text)
        self.assertIn('push:', text)
        self.assertIn('WORKBENCH_DISABLE_REAL_SERVICES:', text)
        self.assertIn('scripts/verify-cross-acceptance-evidence.py', text)
        self.assertNotIn('secrets.', text)
        self.assertNotIn('secrets:', text)
        self.assertIn('permissions:\n  contents: read', text)

    def test_migration_records_preserve_baseline(self):
        source = json.loads((ROOT / 'eng/migration-source.json').read_text())
        lock = json.loads((ROOT / 'eng/host-sdk.lock.json').read_text())
        self.assertEqual(source['sourceCommit'], lock['sourceCommit'])
        self.assertEqual(163, len(source['files']))
        for record in source['files']:
            self.assertTrue((ROOT / record['path']).is_file(), record['path'])

    def test_source_acceptance_supplement_matches_recorded_host_bytes(self):
        supplement = json.loads((ROOT / 'eng/source-acceptance-supplement.json').read_text())
        self.assertEqual('b1a3e9c9963c2d75798a6068f99a6270d81bd074', supplement['sourceCommit'])
        self.assertEqual(6, len(supplement['files']))
        for record in supplement['files']:
            content = (ROOT / record['path']).read_bytes()
            self.assertEqual(record['length'], len(content), record['path'])
            self.assertEqual(record['sha256'], hashlib.sha256(content).hexdigest(), record['path'])

    def test_source_report_supplement_matches_recorded_host_bytes(self):
        supplement = json.loads((ROOT / 'eng/source-report-supplement.json').read_text())
        self.assertEqual('b1a3e9c9963c2d75798a6068f99a6270d81bd074', supplement['sourceCommit'])
        self.assertEqual(2, len(supplement['files']))
        for record in supplement['files']:
            self.assertTrue((ROOT / record['path']).is_file(), record['path'])
            content = (ROOT / record['baselinePath']).read_bytes()
            self.assertEqual(record['length'], len(content), record['path'])
            self.assertEqual(record['sha256'], hashlib.sha256(content).hexdigest(), record['path'])


if __name__ == '__main__':
    unittest.main()
