#!/usr/bin/env python3
"""Reuse source mtimes only when checkout bytes still match the cached build."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def unlinked(path, root):
    current = root
    for part in path.relative_to(root).parts:
        current /= part
        if current.is_symlink():
            return False
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['save', 'restore'])
    parser.add_argument('--manifest', type=Path, default=Path('.build/ci-source-mtimes.json'))
    args = parser.parse_args()
    root = Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip()).resolve()
    manifest = Path(os.path.abspath(root / args.manifest))
    if not manifest.is_relative_to(root) or not unlinked(manifest, root):
        parser.error('The mtime manifest must stay inside this checkout without symlinks')
    if args.command == 'restore' and not manifest.exists():
        print('restore: 0 tracked file mtimes (cache miss)')
        return
    files = [os.fsdecode(name) for name in subprocess.check_output(['git', 'ls-files', '-z'], cwd=root).split(b'\0') if name]
    if manifest.relative_to(root).as_posix() in files:
        parser.error('The mtime manifest cannot replace a tracked file')
    state = json.loads(manifest.read_text()) if args.command == 'restore' else {}
    count = 0
    for name in files:
        path = root / name
        if not unlinked(path, root) or not path.is_file():
            continue
        try:
            with path.open('rb') as stream:
                digest = hashlib.file_digest(stream, 'sha256').hexdigest()
                info = os.fstat(stream.fileno())
            if args.command == 'save':
                state[name] = {'sha256': digest, 'mtime_ns': info.st_mtime_ns}
            else:
                saved = state.get(name, {})
                mtime = saved.get('mtime_ns')
                if saved.get('sha256') != digest or type(mtime) is not int or mtime < 0:
                    continue
                os.utime(path, ns=(info.st_atime_ns, mtime), follow_symlinks=False)
            count += 1
        except OSError:
            continue
    if args.command == 'save':
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps(state, sort_keys=True) + '\n')
    print(f'{args.command}: {count} tracked file mtimes')


if __name__ == '__main__':
    main()
