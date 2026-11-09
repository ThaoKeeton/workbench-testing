#!/usr/bin/env python3
"""Build the two SDK packages from an explicitly pinned, verified host source checkout."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / 'eng/host-sdk.lock.json'


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def verify_host(host, lock):
    host = Path(host).resolve()
    commit = subprocess.check_output(['git', '-C', str(host), 'rev-parse', 'HEAD'], text=True).strip()
    if commit != lock['sourceCommit']:
        raise ValueError('HostSourceCommitMismatch: expected ' + lock['sourceCommit'])
    for record in lock['sources']:
        path = host / record['path']
        if path.is_symlink() or sha256(path) != record['sha256']:
            raise ValueError('HostSdkSourceHashMismatch: ' + record['path'])
    return host


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-root', default=str(ROOT / '.host/project-workbench'))
    parser.add_argument('--reference-only', action='store_true', help='Explicit non-Windows compilation only; never a native delivery')
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    lock = json.loads(LOCK.read_text())
    host = verify_host(args.host_root, lock)
    if args.verify_only:
        print('Pinned host SDK source verified: ' + lock['sourceCommit'])
        return
    if os.name != 'nt' and not args.reference_only:
        raise ValueError('NativeSdkRequiresWindows: use --reference-only on other systems')
    output = ROOT / 'artifacts/host-sdk'
    source = output / 'source'
    packages = output / 'packages'
    # The directory is owned by this script and excluded from version control.
    if output.exists():
        if output.is_symlink():
            raise ValueError('SdkOutputLinkRefused')
        shutil.rmtree(output)
    packages.mkdir(parents=True)
    for record in lock['sources']:
        destination = source / record['path']
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(host / record['path'], destination)
    for name in ['Workbench.Sdk', 'Workbench.WinUI.Sdk']:
        command = ['dotnet', 'pack', str(source / 'src' / name / (name + '.csproj')), '-c', 'Release', '--nologo', '-o', str(packages), '-p:ContinuousIntegrationBuild=true', '-p:SourceRevisionId=' + lock['sourceCommit'], '-p:PathMap=' + str(source) + '=/_/']
        if args.reference_only:
            command += ['-p:UseWinUI=false', '-p:AppxGeneratePriEnabled=false', '-p:IncludeProjectPriFile=false', '-p:WindowsAppSDKSelfContained=false']
        subprocess.run(command, cwd=ROOT, check=True)
    records = [{'path': 'packages/' + p.name, 'length': p.stat().st_size, 'sha256': sha256(p)} for p in sorted(packages.glob('*.nupkg'))]
    if len(records) != 2:
        raise ValueError('ExactlyTwoHostSdkPackagesRequired')
    manifest = {**lock, 'referenceOnly': args.reference_only, 'packages': records}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps({'sourceCommit': lock['sourceCommit'], 'referenceOnly': args.reference_only, 'packages': records}, indent=2))


if __name__ == '__main__':
    main()
