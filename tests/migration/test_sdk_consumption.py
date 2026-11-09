import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('verify_sdk_consumption', ROOT / 'scripts/verify-sdk-consumption.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class SdkConsumptionTests(unittest.TestCase):
    def fixture(self, root):
        (root / 'eng').mkdir()
        source = '1' * 40
        (root / 'eng/host-sdk.lock.json').write_text(json.dumps({'sourceCommit': source}))
        prepared = root / 'artifacts/host-sdk/packages'
        prepared.mkdir(parents=True)
        cache = root / 'artifacts/nuget-packages'
        records = []
        for name, dll in audit.PACKAGES.items():
            archive = prepared / (name + '.0.4.0.nupkg')
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr(dll, ('current-' + name).encode())
            data = archive.read_bytes()
            records.append({'path': 'packages/' + archive.name, 'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
            target = cache / name.lower() / '0.4.0'
            target.mkdir(parents=True)
            (target / archive.name.lower()).write_bytes(data)
            with zipfile.ZipFile(archive) as package:
                package.extractall(target)
        (prepared.parent / 'manifest.json').write_text(json.dumps({'sourceCommit': source, 'packages': records}))
        for project in audit.PROJECTS:
            target = root / 'plugins' / project / 'obj'
            target.mkdir(parents=True)
            (target / 'project.assets.json').write_text(json.dumps({'packageFolders': {str(cache): {}}, 'libraries': {name + '/0.4.0': {'type': 'package'} for name in audit.PACKAGES}}))
        return cache

    def test_stale_same_version_cache_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = self.fixture(root)
            (cache / 'workbench.sdk/0.4.0/workbench.sdk.0.4.0.nupkg').write_bytes(b'stale reference-only archive')
            with self.assertRaisesRegex(ValueError, 'SdkConsumedPackageHashMismatch'):
                audit.verify(root)

    def test_tampered_extracted_assembly_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = self.fixture(root)
            (cache / 'workbench.sdk/0.4.0/lib/net10.0/Workbench.Sdk.dll').write_bytes(b'wrong cached DLL')
            with self.assertRaisesRegex(ValueError, 'SdkConsumedAssemblyHashMismatch'):
                audit.verify(root)

    def test_matching_prepared_package_and_consumed_bytes_are_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fixture(root)
            audit.verify(root)

    def test_shared_global_package_folder_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fixture(root)
            assets = root / 'plugins/Workbench.Testing.Domain/obj/project.assets.json'
            data = json.loads(assets.read_text())
            data['packageFolders'] = {str(root / 'shared-global-cache'): {}}
            assets.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, 'SdkConsumedCacheMismatch'):
                audit.verify(root)

    def test_extra_fallback_folder_is_refused_even_with_expected_cache(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = self.fixture(root)
            assets = root / 'plugins/Workbench.Testing.Domain/obj/project.assets.json'
            data = json.loads(assets.read_text())
            data['packageFolders'] = {str(cache): {}, str(root / 'shared-fallback'): {}}
            assets.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, 'SdkConsumedCacheMismatch'):
                audit.verify(root)

    def test_cache_comparison_uses_platform_case_semantics(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = self.fixture(root)
            for project in audit.PROJECTS:
                assets = root / 'plugins' / project / 'obj/project.assets.json'
                data = json.loads(assets.read_text())
                data['packageFolders'] = {str(cache).swapcase(): {}}
                assets.write_text(json.dumps(data))
            with patch('os.path.normcase', side_effect=lambda path: str(path).casefold()):
                audit.verify(root)


if __name__ == '__main__':
    unittest.main()
