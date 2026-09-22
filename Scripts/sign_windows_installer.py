#!/usr/bin/env python3
"""Sign exact Windows installer metadata using Sparkle's existing Keychain key; never export it."""
import argparse
import base64
import hashlib
import json
import pathlib
import re
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('installer', type=pathlib.Path)
    parser.add_argument('--sign-tool', type=pathlib.Path, required=True)
    parser.add_argument('--account', default='HechoLP')
    args = parser.parse_args()
    match = re.fullmatch(r'CodeRim-Windows-((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))-(x64|arm64)-Setup\.msi', args.installer.name)
    if not match:
        parser.error('Expected a canonical versioned Windows Setup.msi')
    with args.installer.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    payload = dict(schema=1, product='CodeRim.Windows', version=match[1], architecture=match[2], file=args.installer.name, size=args.installer.stat().st_size, sha256=digest)
    manifest = args.installer.with_suffix(args.installer.suffix + '.manifest.json')
    signature = args.installer.with_suffix(args.installer.suffix + '.manifest.sig')
    manifest.write_bytes((json.dumps(payload, separators=(',', ':'), sort_keys=True) + '\n').encode())
    result = subprocess.run([str(args.sign_tool), '--account', args.account, str(manifest)], check=True, capture_output=True, text=True)
    found = re.search(r'sparkle:edSignature="([A-Za-z0-9+/=]+)"', result.stdout)
    if not found or len(base64.b64decode(found[1], validate=True)) != 64:
        raise RuntimeError('The release signer did not return an Ed25519 signature')
    subprocess.run([str(args.sign_tool), '--account', args.account, '--verify', str(manifest), found[1]], check=True, capture_output=True)
    signature.write_text(found[1] + '\n')
    print(json.dumps(payload, indent=2))


if __name__ == '__main__':
    main()
