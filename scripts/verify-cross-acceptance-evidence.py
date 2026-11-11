#!/usr/bin/env python3
"""Re-check retained cross-repository source-acceptance bytes against this checkout's locks.

The script hashes the uploaded bundle and compares identity fields. It does not execute the
host, read user directories, or promote a Windows Server run into Windows 11 human acceptance.
"""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PHASES = ('seed', 'verify', 'stop-figma', 'stop-ai')
CROSS_KEYS = (
    'hostSourceCommit', 'sdkSourceCommit', 'pairedProductionSha256', 'sdkManifestSha256',
    'sdkCorePackageSha256', 'sdkWinUiPackageSha256', 'hostCoreSdkSha256', 'hostWinUiSdkSha256')
LIMITS = (
    'Actual installed synthetic source controls and Windows CurrentUser DPAPI. '
    'Terminal in-memory HTTP only; generated cases manual, no AUT execution. '
    'Live services and Windows11 human acceptance remain separate.')
HEX40 = re.compile(r'^[0-9a-f]{40}$')
HEX64 = re.compile(r'^[0-9a-f]{64}$')
JSON_MAX = 2 * 1024 * 1024
BYTE_MAX = 64 * 1024 * 1024
REPORT_MAX = 262144


def _pairs(pairs):
    names = []
    for key, value in pairs:
        if any(key.lower() == seen.lower() for seen in names):
            raise ValueError('CrossEvidenceJsonDuplicate')
        names.append(key)
    return dict(pairs)


def _load(path, maximum):
    data = _bytes(path, maximum)
    if data.startswith(b'\xef\xbb\xbf'):
        raise ValueError('CrossEvidenceJsonInvalid')
    try:
        text = data.decode('utf-8')
        value = json.loads(text, object_pairs_hook=_pairs)
    except ValueError as error:
        if str(error) == 'CrossEvidenceJsonDuplicate':
            raise
        raise ValueError('CrossEvidenceJsonInvalid') from None
    if not isinstance(value, dict):
        raise ValueError('CrossEvidenceJsonInvalid')
    return value


def _bytes(path, maximum):
    current = path
    while True:
        if current.is_symlink():
            raise ValueError('CrossEvidenceLinkRefused')
        if current == current.parent:
            break
        current = current.parent
    if not path.is_file():
        raise ValueError('CrossEvidenceFileBound')
    size = path.stat().st_size
    if size < 1 or size > maximum:
        raise ValueError('CrossEvidenceFileBound')
    data = path.read_bytes()
    if len(data) != size:
        raise ValueError('CrossEvidenceFileBound')
    return data


def _under(root, relative):
    path = root / relative
    if path.is_symlink():
        raise ValueError('CrossEvidenceLinkRefused')
    resolved_root = root.resolve()
    resolved = path.resolve()
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise ValueError('CrossEvidencePathEscaped')
    return path


def _sha256(path, maximum):
    digest = hashlib.sha256()
    data = _bytes(path, maximum)
    digest.update(data)
    return digest.hexdigest(), len(data)


def _hex(value, length):
    pattern = HEX40 if length == 40 else HEX64
    return isinstance(value, str) and pattern.fullmatch(value) is not None


def required_checks(module_text):
    start = module_text.find('function Get-SourceAcceptanceRequiredChecks {')
    if start < 0:
        raise ValueError('CrossEvidenceCheckContractMissing')
    body = module_text[start:]
    end = body.find('\nfunction ', 1)
    body = body[:end] if end > 0 else body

    def block(label):
        match = re.search(label + r' \{ return @\((.*?)\) \}', body, re.S)
        if match is None:
            raise ValueError('CrossEvidenceCheckContractMissing')
        names = re.findall(r"'([a-z0-9-]+)'", match.group(1))
        if len(names) != len(set(names)) or not names:
            raise ValueError('CrossEvidenceCheckContractMissing')
        return names

    checks = {'seed': block('seed'), 'verify': block('verify'), 'stop-figma': block('default'), 'stop-ai': block('default')}
    if sum(len(names) for names in checks.values()) != 36:
        raise ValueError('CrossEvidenceCheckContractMissing')
    return checks


def _plugin_commit(explicit):
    if explicit is None:
        explicit = subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip()
    if not _hex(explicit, 40):
        raise ValueError('CrossEvidencePluginCommitInvalid')
    return explicit


def _bindings(metadata, core_hash, winui_hash):
    managed = metadata.get('managed')
    if not isinstance(managed, dict):
        raise ValueError('CrossEvidenceIdentityInvalid')
    rows = managed.get('hostBindings')
    if not isinstance(rows, list) or not rows:
        raise ValueError('CrossEvidenceIdentityInvalid')
    found = {}
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get('name'), str) or not _hex(row.get('sha256'), 64):
            raise ValueError('CrossEvidenceIdentityInvalid')
        if row['name'] in ('Workbench.Sdk', 'Workbench.WinUI.Sdk'):
            if row['name'] in found:
                raise ValueError('CrossEvidenceIdentityInvalid')
            found[row['name']] = row['sha256']
    if found.get('Workbench.Sdk') != core_hash or found.get('Workbench.WinUI.Sdk') != winui_hash:
        raise ValueError('CrossEvidenceHashMismatch')


def verify_cross_acceptance_evidence(root, plugin_commit=None):
    root = Path(root).resolve()
    if root.is_symlink():
        raise ValueError('CrossEvidenceLinkRefused')
    plugin = _plugin_commit(plugin_commit)
    host_lock = _load(ROOT / 'eng/host-acceptance.lock.json', JSON_MAX)
    sdk_lock = _load(ROOT / 'eng/host-sdk.lock.json', JSON_MAX)
    host = host_lock.get('sourceCommit')
    sdk = sdk_lock.get('sourceCommit')
    if (host_lock.get('schemaVersion') != 1 or host_lock.get('sdkVersion') != '0.4.0' or
            host_lock.get('sdkSourceCommit') != sdk or sdk_lock.get('sdkVersion') != '0.4.0' or
            not _hex(host, 40) or not _hex(sdk, 40) or plugin in (host, sdk) or host == sdk):
        raise ValueError('CrossEvidenceCommitNotIndependent')
    checks = required_checks((ROOT / 'scripts/SourceAcceptanceProbe.psm1').read_text(encoding='utf-8'))
    probes = _under(root, 'artifacts/native/source-acceptance-probes')
    summaries = [path for path in probes.rglob('summary.json') if path.is_file()]
    if not summaries:
        raise ValueError('CrossEvidenceSummaryMissing')
    if len(summaries) != 1:
        raise ValueError('CrossEvidenceSummaryAmbiguous')
    summary_path = summaries[0]
    if _under(root, summary_path.relative_to(root)) != summary_path:
        raise ValueError('CrossEvidencePathEscaped')
    summary = _load(summary_path, REPORT_MAX)
    cross = summary.get('crossIdentity')
    if (summary.get('schemaVersion') != 2 or summary.get('kind') != 'SourceAcceptanceSummary' or summary.get('succeeded') is not True or
            summary.get('sourceCommit') != plugin or summary.get('limits') != LIMITS or not isinstance(cross, dict) or
            tuple(sorted(cross)) != tuple(sorted(CROSS_KEYS))):
        raise ValueError('CrossEvidenceIdentityInvalid')
    if cross['hostSourceCommit'] != host or cross['sdkSourceCommit'] != sdk:
        raise ValueError('CrossEvidenceCommitMismatch')
    for key in CROSS_KEYS:
        length = 40 if key.endswith('Commit') else 64
        if not _hex(cross[key], length):
            raise ValueError('CrossEvidenceIdentityInvalid')
    files = {
        'packageSha256': 'artifacts/native/source-acceptance-fixture/workbench-testing-source-acceptance-0.2.0.zip',
        'pairedProductionSha256': 'artifacts/native/source-paired-production/workbench-testing-plugin-0.2.0.zip',
        'sdkManifestSha256': 'artifacts/host-sdk/manifest.json',
        'sdkCorePackageSha256': 'artifacts/host-sdk/packages/Workbench.Sdk.0.4.0.nupkg',
        'sdkWinUiPackageSha256': 'artifacts/host-sdk/packages/Workbench.WinUI.Sdk.0.4.0.nupkg',
        'hostExeSha256': '.host/acceptance-host/artifacts/native/app/Workbench.App.exe',
        'hostDllSha256': '.host/acceptance-host/artifacts/native/app/Workbench.App.dll',
        'hostCoreSdkSha256': '.host/acceptance-host/artifacts/native/app/Workbench.Sdk.dll',
        'hostWinUiSdkSha256': '.host/acceptance-host/artifacts/native/app/Workbench.WinUI.Sdk.dll',
    }
    observed = {}
    sizes = {}
    for key, relative in files.items():
        observed[key], sizes[key] = _sha256(_under(root, relative), BYTE_MAX if not relative.endswith('.json') else JSON_MAX)
    if summary.get('packageSha256') != observed['packageSha256'] or summary.get('hostExeSha256') != observed['hostExeSha256'] or summary.get('hostDllSha256') != observed['hostDllSha256']:
        raise ValueError('CrossEvidenceHashMismatch')
    for key in ('pairedProductionSha256', 'sdkManifestSha256', 'sdkCorePackageSha256', 'sdkWinUiPackageSha256', 'hostCoreSdkSha256', 'hostWinUiSdkSha256'):
        if cross[key] != observed[key]:
            raise ValueError('CrossEvidenceHashMismatch')
    manifest = _load(_under(root, files['sdkManifestSha256']), JSON_MAX)
    for key, value in sdk_lock.items():
        if manifest.get(key) != value:
            raise ValueError('CrossEvidenceCommitMismatch')
    if manifest.get('referenceOnly') is not False or not isinstance(manifest.get('packages'), list) or len(manifest['packages']) != 2:
        raise ValueError('CrossEvidenceIdentityInvalid')
    expected_packages = {
        'packages/Workbench.Sdk.0.4.0.nupkg': observed['sdkCorePackageSha256'],
        'packages/Workbench.WinUI.Sdk.0.4.0.nupkg': observed['sdkWinUiPackageSha256'],
    }
    seen_packages = set()
    for package in manifest['packages']:
        if not isinstance(package, dict) or package.get('path') not in expected_packages or not isinstance(package.get('length'), int):
            raise ValueError('CrossEvidenceIdentityInvalid')
        path = _under(root, 'artifacts/host-sdk') / package['path']
        digest, size = _sha256(path, BYTE_MAX)
        if digest != expected_packages[package['path']] or digest != package.get('sha256') or size != package['length'] or package['path'] in seen_packages:
            raise ValueError('CrossEvidenceHashMismatch')
        seen_packages.add(package['path'])
    fixture = _load(_under(root, 'artifacts/native/source-acceptance-fixture/provenance.json'), JSON_MAX)
    production = _load(_under(root, 'artifacts/native/source-paired-production/provenance.json'), JSON_MAX)
    _expect_provenance(fixture, production, manifest, plugin, host, sdk, observed, sizes, cross)
    phases = summary.get('phases')
    if not isinstance(phases, list) or len(phases) != 4 or [row.get('phase') for row in phases] != list(PHASES):
        raise ValueError('CrossEvidencePhaseMismatch')
    if summary.get('phaseQualifiedChecks') != 36:
        raise ValueError('CrossEvidenceCheckMismatch')
    roots = {}
    births = set()
    for row, phase in zip(phases, PHASES):
        native_path = summary_path.parent / (phase + '.json')
        native_hash, _ = _sha256(native_path, REPORT_MAX)
        native = _load(native_path, REPORT_MAX)
        if (row.get('schemaVersion') != 1 or row.get('kind') != 'SourceAcceptanceRunner' or row.get('succeeded') is not True or
                row.get('hasExited') is not True or row.get('exitCode') != 0 or row.get('terminated') is not False or
                row.get('observationFailed') is not False or row.get('runnerError') is not None or row.get('nativeReport') != 'available' or
                row.get('nativeReportSha256') != native_hash or row.get('sourceCommit') != plugin or row.get('crossIdentity') != cross or
                row.get('packageSha256') != observed['packageSha256'] or row.get('hostExeSha256') != observed['hostExeSha256'] or
                row.get('hostDllSha256') != observed['hostDllSha256']):
            raise ValueError('CrossEvidencePhaseMismatch')
        if native.get('checks') != checks[phase]:
            raise ValueError('CrossEvidenceCheckMismatch')
        identity_ok = (native.get('schemaVersion') == 2 and native.get('kind') == 'SourceAcceptanceNative' and native.get('succeeded') is True and
                       native.get('phase') == phase and native.get('processArchitecture') == 'X64' and native.get('actualWindowClosed') is True and
                       native.get('rootsRetired') is True and native.get('sourceCommit') == plugin and native.get('packageSha256') == observed['packageSha256'] and
                       native.get('hostExeSha256') == observed['hostExeSha256'] and native.get('hostDllSha256') == observed['hostDllSha256'] and
                       native.get('processId') == row.get('processId') and native.get('processCreationFileTimeUtc') == row.get('processCreationFileTimeUtc'))
        if not identity_ok or any(native.get(key) != cross[key] for key in CROSS_KEYS):
            raise ValueError('CrossEvidenceIdentityInvalid')
        user_root = native.get('userRoot')
        if not isinstance(user_root, str) or not user_root or user_root != summary.get({'seed': 'seedRoot', 'verify': 'seedRoot', 'stop-figma': 'stopFigmaRoot', 'stop-ai': 'stopAiRoot'}[phase]):
            raise ValueError('CrossEvidenceRootMismatch')
        roots[phase] = user_root
        birth = (row.get('processId'), row.get('processCreationFileTimeUtc'))
        if birth in births or not isinstance(birth[0], int) or not isinstance(birth[1], int):
            raise ValueError('CrossEvidenceProcessReused')
        births.add(birth)
    if roots['seed'] != roots['verify'] or len({roots['seed'], roots['stop-figma'], roots['stop-ai']}) != 3:
        raise ValueError('CrossEvidenceRootMismatch')
    return {'pluginCommit': plugin, 'hostCommit': host, 'sdkCommit': sdk, 'checks': 36}


def _zip_record(metadata, name, digest, size):
    record = metadata.get('zip')
    if not isinstance(record, dict) or record.get('path') != name or record.get('sha256') != digest or record.get('length') != size:
        raise ValueError('CrossEvidenceHashMismatch')


def _expect_provenance(fixture, production, manifest, plugin, host, sdk, observed, sizes, cross):
    if (fixture.get('schemaVersion') != 1 or production.get('schemaVersion') != 1 or fixture.get('profile') != 'SourceAcceptance' or
            fixture.get('pluginId') != 'com.projectworkbench.testing.source-acceptance' or production.get('pluginId') != 'com.projectworkbench.testing' or
            fixture.get('version') != '0.2.0' or production.get('version') != '0.2.0' or fixture.get('apiVersion') != 1 or
            fixture.get('minimumHostVersion') != '0.4.0' or production.get('minimumHostVersion') != '0.4.0' or
            fixture.get('sourceRepository') != 'https://github.com/ThaoKeeton/workbench-testing' or
            production.get('sourceRepository') != 'https://github.com/ThaoKeeton/workbench-testing' or
            fixture.get('hostRepository') != 'https://github.com/ThaoKeeton/project-workbench' or
            production.get('hostRepository') != 'https://github.com/ThaoKeeton/project-workbench' or
            fixture.get('sourceTreeDirty') is not False or production.get('sourceTreeDirty') is not False or
            fixture.get('referenceOnly') is not False or production.get('referenceOnly') is not False or
            fixture.get('sourceCommit') != plugin or production.get('sourceCommit') != plugin or
            fixture.get('hostSourceCommit') != host or fixture.get('sdkSourceCommit') != sdk or
            fixture.get('sdkManifestSha256') != observed['sdkManifestSha256'] or production.get('sdkManifestSha256') != observed['sdkManifestSha256'] or
            fixture.get('pairedProductionSha256') != observed['pairedProductionSha256'] or fixture.get('sdk') != manifest or production.get('sdk') != manifest or
            fixture.get('host') != production.get('host')):
        raise ValueError('CrossEvidenceIdentityInvalid')
    host_identity = fixture.get('host')
    if (not isinstance(host_identity, dict) or host_identity.get('sourceCommit') != host or
            host_identity.get('productVersion') != '0.4.0+' + host or host_identity.get('executableSha256') != observed['hostExeSha256'] or
            host_identity.get('dllSha256') != observed['hostDllSha256']):
        raise ValueError('CrossEvidenceHashMismatch')
    _zip_record(fixture, 'workbench-testing-source-acceptance-0.2.0.zip', observed['packageSha256'], sizes['packageSha256'])
    _zip_record(production, 'workbench-testing-plugin-0.2.0.zip', observed['pairedProductionSha256'], sizes['pairedProductionSha256'])
    _bindings(fixture, cross['hostCoreSdkSha256'], cross['hostWinUiSdkSha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', default=str(ROOT))
    parser.add_argument('--plugin-commit', default=None)
    args = parser.parse_args()
    result = verify_cross_acceptance_evidence(args.root, args.plugin_commit)
    print('Cross acceptance evidence verified: plugin ' + result['pluginCommit'] + ' host ' + result['hostCommit'] + ' sdk ' + result['sdkCommit'] + ' phases 4 checks 36')


if __name__ == '__main__':
    main()
