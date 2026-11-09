#!/usr/bin/env python3
"""Verify that plugin restores consumed the prepared SDK archives and exact DLL bytes."""
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ['Workbench.Testing.Domain', 'Workbench.Testing']
PACKAGES = {'Workbench.Sdk': 'lib/net10.0/Workbench.Sdk.dll', 'Workbench.WinUI.Sdk': 'lib/net10.0-windows10.0.19041/Workbench.WinUI.Sdk.dll'}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify(root=ROOT):
    root = Path(root).resolve()
    prepared = root / 'artifacts/host-sdk'
    manifest = json.loads((prepared / 'manifest.json').read_text())
    lock = json.loads((root / 'eng/host-sdk.lock.json').read_text())
    if manifest['sourceCommit'] != lock['sourceCommit']:
        raise ValueError('SdkConsumedSourceMismatch')
    cache = (root / 'artifacts/nuget-packages').resolve()
    records = {r['path']: r for r in manifest['packages']}
    for name, dll in PACKAGES.items():
        filename = name + '.0.4.0.nupkg'
        record = records['packages/' + filename]
        archive = prepared / 'packages' / filename
        data = archive.read_bytes()
        if digest(data) != record['sha256'] or len(data) != record['length']:
            raise ValueError('SdkPreparedPackageHashMismatch: ' + name)
        directory = cache / name.lower() / '0.4.0'
        cached = directory / filename.lower()
        if cached.is_symlink() or digest(cached.read_bytes()) != record['sha256']:
            raise ValueError('SdkConsumedPackageHashMismatch: ' + name)
        with zipfile.ZipFile(archive) as package:
            expected = package.read(dll)
        actual = directory / dll
        if actual.is_symlink() or digest(actual.read_bytes()) != digest(expected):
            raise ValueError('SdkConsumedAssemblyHashMismatch: ' + name)
    projects = PROJECTS + (['Workbench.TestCases.Sample'] if (root / 'plugins/Workbench.TestCases.Sample/obj/project.assets.json').exists() else [])
    for project in projects:
        assets = json.loads((root / 'plugins' / project / 'obj/project.assets.json').read_text())
        folders = {str(Path(p).resolve()) for p in assets['packageFolders']}
        if folders != {str(cache)}:
            # Do not print local folder names: they can contain a user's name.
            equivalent_ignoring_case = sum(p.casefold() == str(cache).casefold() for p in folders)
            raise ValueError('SdkConsumedCacheMismatch: ' + project +
                             ' (folders=' + str(len(folders)) + ', casefoldMatches=' + str(equivalent_ignoring_case) + ')')
        expected = ['Workbench.Sdk/0.4.0'] if project.endswith('.Domain') else ['Workbench.Sdk/0.4.0', 'Workbench.WinUI.Sdk/0.4.0']
        for identity in expected:
            if assets['libraries'][identity]['type'] != 'package':
                raise ValueError('SdkConsumedReferenceMismatch: ' + project)
    print('SDK consumption verified: isolated cache, exact nupkg and DLL bytes, all plugin restore folders.')


if __name__ == '__main__':
    verify()
