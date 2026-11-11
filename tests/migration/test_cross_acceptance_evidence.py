import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('verify_cross_acceptance_evidence', ROOT / 'scripts/verify-cross-acceptance-evidence.py')
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)

PLUGIN = 'a' * 40
HOST = json.loads((ROOT / 'eng/host-acceptance.lock.json').read_text())['sourceCommit']
SDK = json.loads((ROOT / 'eng/host-sdk.lock.json').read_text())['sourceCommit']


def _sha(data):
    return hashlib.sha256(data).hexdigest()


class CrossAcceptanceEvidenceTests(unittest.TestCase):
    def test_module_contract_is_four_phases_and_thirty_six_checks(self):
        checks = check.required_checks((ROOT / 'scripts/SourceAcceptanceProbe.psm1').read_text(encoding='utf-8'))
        self.assertEqual(['seed', 'verify', 'stop-figma', 'stop-ai'], list(checks))
        self.assertEqual(36, sum(len(names) for names in checks.values()))
        self.assertEqual(checks['stop-figma'], checks['stop-ai'])
        self.assertEqual('source-installed-same-page', checks['seed'][0])

    def test_aligned_bundle_is_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary))
            result = check.verify_cross_acceptance_evidence(root, PLUGIN)
            self.assertEqual(PLUGIN, result['pluginCommit'])
            self.assertEqual(HOST, result['hostCommit'])
            self.assertEqual(SDK, result['sdkCommit'])
            self.assertEqual(36, result['checks'])

    def test_tampered_host_executable_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary))
            path = root / '.host/acceptance-host/artifacts/native/app/Workbench.App.exe'
            path.write_bytes(path.read_bytes() + b'x')
            with self.assertRaisesRegex(ValueError, 'CrossEvidenceHashMismatch'):
                check.verify_cross_acceptance_evidence(root, PLUGIN)

    def test_swapped_host_commit_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary), host_commit='b' * 40)
            with self.assertRaisesRegex(ValueError, 'CrossEvidenceCommitMismatch'):
                check.verify_cross_acceptance_evidence(root, PLUGIN)

    def test_plugin_commit_cannot_reuse_the_host_commit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary), plugin_commit=HOST)
            with self.assertRaisesRegex(ValueError, 'CrossEvidenceCommitNotIndependent'):
                check.verify_cross_acceptance_evidence(root, HOST)

    def test_missing_phase_report_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary))
            probe = next((root / 'artifacts/native/source-acceptance-probes').rglob('summary.json')).parent
            (probe / 'stop-ai.json').unlink()
            with self.assertRaisesRegex(ValueError, 'CrossEvidenceFileBound'):
                check.verify_cross_acceptance_evidence(root, PLUGIN)

    def test_two_summaries_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary))
            extra = root / 'artifacts/native/source-acceptance-probes/other'
            extra.mkdir()
            (extra / 'summary.json').write_text('{}', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'CrossEvidenceSummaryAmbiguous'):
                check.verify_cross_acceptance_evidence(root, PLUGIN)

    def test_renamed_check_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.bundle(Path(temporary), rename_check=True)
            with self.assertRaisesRegex(ValueError, 'CrossEvidenceCheckMismatch'):
                check.verify_cross_acceptance_evidence(root, PLUGIN)

    def bundle(self, temporary, host_commit=HOST, plugin_commit=PLUGIN, rename_check=False):
        root = temporary / 'evidence'
        files = {
            '.host/acceptance-host/artifacts/native/app/Workbench.App.exe': b'exe',
            '.host/acceptance-host/artifacts/native/app/Workbench.App.dll': b'dll',
            '.host/acceptance-host/artifacts/native/app/Workbench.Sdk.dll': b'core',
            '.host/acceptance-host/artifacts/native/app/Workbench.WinUI.Sdk.dll': b'winui',
            'artifacts/host-sdk/packages/Workbench.Sdk.0.4.0.nupkg': b'sdk-core',
            'artifacts/host-sdk/packages/Workbench.WinUI.Sdk.0.4.0.nupkg': b'sdk-winui',
            'artifacts/native/source-acceptance-fixture/workbench-testing-source-acceptance-0.2.0.zip': b'fixture-zip',
            'artifacts/native/source-paired-production/workbench-testing-plugin-0.2.0.zip': b'production-zip',
        }
        hashes = {}
        sizes = {}
        for relative, data in files.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            hashes[relative] = _sha(data)
            sizes[relative] = len(data)
        lock = json.loads((ROOT / 'eng/host-sdk.lock.json').read_text())
        manifest = dict(lock)
        manifest['referenceOnly'] = False
        manifest['packages'] = [
            {'path': 'packages/Workbench.Sdk.0.4.0.nupkg', 'length': sizes['artifacts/host-sdk/packages/Workbench.Sdk.0.4.0.nupkg'], 'sha256': hashes['artifacts/host-sdk/packages/Workbench.Sdk.0.4.0.nupkg']},
            {'path': 'packages/Workbench.WinUI.Sdk.0.4.0.nupkg', 'length': sizes['artifacts/host-sdk/packages/Workbench.WinUI.Sdk.0.4.0.nupkg'], 'sha256': hashes['artifacts/host-sdk/packages/Workbench.WinUI.Sdk.0.4.0.nupkg']},
        ]
        (root / 'artifacts/host-sdk/manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        manifest_hash = _sha((root / 'artifacts/host-sdk/manifest.json').read_bytes())
        host_identity = {
            'sourceCommit': host_commit, 'productVersion': '0.4.0+' + host_commit, 'fileVersion': '0.4.0.0', 'assemblyVersion': '1.0.0.0',
            'executableSha256': hashes['.host/acceptance-host/artifacts/native/app/Workbench.App.exe'],
            'dllSha256': hashes['.host/acceptance-host/artifacts/native/app/Workbench.App.dll'],
        }
        cross = {
            'hostSourceCommit': host_commit, 'sdkSourceCommit': SDK,
            'pairedProductionSha256': hashes['artifacts/native/source-paired-production/workbench-testing-plugin-0.2.0.zip'],
            'sdkManifestSha256': manifest_hash,
            'sdkCorePackageSha256': hashes['artifacts/host-sdk/packages/Workbench.Sdk.0.4.0.nupkg'],
            'sdkWinUiPackageSha256': hashes['artifacts/host-sdk/packages/Workbench.WinUI.Sdk.0.4.0.nupkg'],
            'hostCoreSdkSha256': hashes['.host/acceptance-host/artifacts/native/app/Workbench.Sdk.dll'],
            'hostWinUiSdkSha256': hashes['.host/acceptance-host/artifacts/native/app/Workbench.WinUI.Sdk.dll'],
        }
        bindings = {'hostBindings': [
            {'name': 'System.Runtime', 'sha256': '1' * 64},
            {'name': 'Workbench.Sdk', 'sha256': cross['hostCoreSdkSha256']},
            {'name': 'Workbench.WinUI.Sdk', 'sha256': cross['hostWinUiSdkSha256']},
        ]}
        common = {
            'schemaVersion': 1, 'version': '0.2.0', 'minimumHostVersion': '0.4.0', 'sourceRepository': 'https://github.com/ThaoKeeton/workbench-testing',
            'hostRepository': 'https://github.com/ThaoKeeton/project-workbench', 'sourceCommit': plugin_commit, 'sourceTreeDirty': False,
            'referenceOnly': False, 'host': host_identity, 'sdk': manifest, 'sdkManifestSha256': manifest_hash,
        }
        fixture = dict(common)
        fixture.update({
            'profile': 'SourceAcceptance', 'pluginId': 'com.projectworkbench.testing.source-acceptance', 'apiVersion': 1,
            'hostSourceCommit': host_commit, 'sdkSourceCommit': SDK, 'pairedProductionSha256': cross['pairedProductionSha256'], 'managed': bindings,
            'zip': {'path': 'workbench-testing-source-acceptance-0.2.0.zip', 'length': sizes['artifacts/native/source-acceptance-fixture/workbench-testing-source-acceptance-0.2.0.zip'], 'sha256': hashes['artifacts/native/source-acceptance-fixture/workbench-testing-source-acceptance-0.2.0.zip']},
        })
        production = dict(common)
        production.update({
            'pluginId': 'com.projectworkbench.testing',
            'zip': {'path': 'workbench-testing-plugin-0.2.0.zip', 'length': sizes['artifacts/native/source-paired-production/workbench-testing-plugin-0.2.0.zip'], 'sha256': hashes['artifacts/native/source-paired-production/workbench-testing-plugin-0.2.0.zip']},
        })
        (root / 'artifacts/native/source-acceptance-fixture/provenance.json').write_text(json.dumps(fixture), encoding='utf-8')
        (root / 'artifacts/native/source-paired-production/provenance.json').write_text(json.dumps(production), encoding='utf-8')
        checks = check.required_checks((ROOT / 'scripts/SourceAcceptanceProbe.psm1').read_text(encoding='utf-8'))
        probe = root / 'artifacts/native/source-acceptance-probes/synthetic'
        probe.mkdir(parents=True)
        roots = {'seed': '/synthetic/seed', 'verify': '/synthetic/seed', 'stop-figma': '/synthetic/figma', 'stop-ai': '/synthetic/ai'}
        phases = []
        for index, phase in enumerate(checks):
            names = list(checks[phase])
            if rename_check and phase == 'seed':
                names[0] = 'source-renamed-check'
            native = {
                'schemaVersion': 2, 'kind': 'SourceAcceptanceNative', 'succeeded': True, 'phase': phase, 'userRoot': roots[phase],
                'sourceCommit': plugin_commit, 'packageSha256': hashes['artifacts/native/source-acceptance-fixture/workbench-testing-source-acceptance-0.2.0.zip'],
                'hostExeSha256': host_identity['executableSha256'], 'hostDllSha256': host_identity['dllSha256'],
                'processId': 500 + index, 'processCreationFileTimeUtc': 1000 + index, 'actualWindowClosed': True, 'rootsRetired': True,
                'processArchitecture': 'X64', 'checks': names,
            }
            native.update(cross)
            path = probe / (phase + '.json')
            path.write_text(json.dumps(native), encoding='utf-8')
            phases.append({
                'schemaVersion': 1, 'kind': 'SourceAcceptanceRunner', 'succeeded': True, 'phase': phase, 'processId': 500 + index,
                'processCreationFileTimeUtc': 1000 + index, 'hasExited': True, 'exitCode': 0, 'terminated': False, 'observationFailed': False,
                'runnerError': None, 'nativeReport': 'available', 'nativeReportSha256': _sha(path.read_bytes()), 'sourceCommit': plugin_commit,
                'packageSha256': native['packageSha256'], 'hostExeSha256': native['hostExeSha256'], 'hostDllSha256': native['hostDllSha256'],
                'crossIdentity': cross,
            })
        summary = {
            'schemaVersion': 2, 'kind': 'SourceAcceptanceSummary', 'succeeded': True, 'sourceCommit': plugin_commit,
            'packageSha256': hashes['artifacts/native/source-acceptance-fixture/workbench-testing-source-acceptance-0.2.0.zip'],
            'hostExeSha256': host_identity['executableSha256'], 'hostDllSha256': host_identity['dllSha256'], 'crossIdentity': cross,
            'seedRoot': roots['seed'], 'stopFigmaRoot': roots['stop-figma'], 'stopAiRoot': roots['stop-ai'], 'phases': phases,
            'phaseQualifiedChecks': 36, 'limits': check.LIMITS,
        }
        (probe / 'summary.json').write_text(json.dumps(summary), encoding='utf-8')
        return root


if __name__ == '__main__':
    unittest.main()
