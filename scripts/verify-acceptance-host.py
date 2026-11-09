#!/usr/bin/env python3
"""Verify the pinned acceptance host and its SDK sources without changing either checkout."""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location('prepare_host_sdk', ROOT / 'scripts/prepare-host-sdk.py')
_prepare = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_prepare)


def verify_acceptance_host(host, sdk_lock, host_lock):
    valid_hash = lambda value: isinstance(value, str) and re.fullmatch(r'[0-9a-f]{40}', value) is not None
    if (host_lock.get('schemaVersion') != 1 or
            host_lock.get('repository') != 'https://github.com/ThaoKeeton/project-workbench.git' or
            not valid_hash(host_lock.get('sourceCommit')) or not valid_hash(sdk_lock.get('sourceCommit')) or
            host_lock.get('sdkSourceCommit') != sdk_lock['sourceCommit'] or
            host_lock.get('sdkVersion') != '0.4.0' or sdk_lock.get('sdkVersion') != '0.4.0'):
        raise ValueError('HostAcceptanceLockInvalid')
    path = Path(host).absolute()
    for part in (path, *path.parents):
        if part.is_symlink():
            raise ValueError('HostAcceptanceSourceLinkRefused')
    pinned_sources = dict(sdk_lock)
    pinned_sources['sourceCommit'] = host_lock['sourceCommit']
    checked = _prepare.verify_host(path, pinned_sources)
    if subprocess.check_output(['git', '-C', str(checked), 'status', '--porcelain', '--untracked-files=normal'], text=True).strip():
        raise ValueError('HostAcceptanceSourceDirty')
    return checked


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-root', default=str(ROOT / '.host/acceptance-host'))
    args = parser.parse_args()
    sdk = json.loads((ROOT / 'eng/host-sdk.lock.json').read_text())
    host = json.loads((ROOT / 'eng/host-acceptance.lock.json').read_text())
    verify_acceptance_host(args.host_root, sdk, host)
    print('Acceptance host source and locked SDK source verified: ' + host['sourceCommit'])


if __name__ == '__main__':
    main()
