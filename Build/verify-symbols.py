#!/usr/bin/env python3
"""Verify staged release symbols against shipped binaries, without starting any service."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def elf_id(path):
    return re.search(r'Build ID: ([0-9a-f]+)', subprocess.check_output(
        ['readelf', '-n', str(path)], text=True)).group(1)


def gateway(runtime, debug):
    binary = runtime / 'gateway-rs'
    identity = elf_id(binary)
    assert identity == elf_id(debug), 'gateway build IDs differ'
    sections = subprocess.check_output(['readelf', '-SW', str(binary)], text=True)
    assert '.debug_info' not in sections and '.gnu_debuglink' in sections
    assert '.debug_info' in subprocess.check_output(['readelf', '-SW', str(debug)], text=True)
    return dict(buildId=identity, binarySha256=sha256(binary), symbolsSha256=sha256(debug))


if sys.argv[1] == '--gateway':
    runtime = Path(sys.argv[2])
    result = gateway(runtime, runtime / 'gateway-rs.debug')
    result['version'] = (runtime / 'VERSION').read_text().splitlines()
    (runtime / 'symbols.json').write_text(json.dumps(result, indent=2) + '\n')
    subprocess.run(['sha256sum', '-c', 'SYMBOL-SHA256SUMS'], cwd=runtime, check=True)
else:
    runtime, symbols = map(Path, sys.argv[1:3])
    manifest = json.loads((symbols / 'symbols.json').read_text())
    for category, directory in (('symbols', symbols), ('binaries', runtime)):
        assert manifest[category], f'No {category} recorded'
        for name, digest in manifest[category].items():
            assert sha256(directory / name) == digest, name
    gateway(runtime / 'gateway-rs', symbols / 'gateway-rs/gateway-rs.debug')
    assert not list(runtime.rglob('*.pdb')) and not list(runtime.rglob('*.debug'))
    print('Verified exact release binary/symbol hashes and gateway build ID/DWARF split')
