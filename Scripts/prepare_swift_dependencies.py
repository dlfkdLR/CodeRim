#!/usr/bin/env python3
"""Apply pinned fixes only inside an exclusively owned SwiftPM build directory."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(argv, **kwargs):
    return subprocess.run(argv, check=True, **kwargs)


def git(path, *args):
    return run(["git", "-C", str(path), *args], capture_output=True, text=True).stdout.rstrip("\n")


def load_spec(root):
    directory = root / "Config/DependencyPatches"
    spec = json.loads((directory / "CodexBar.json").read_text())
    patch = directory / "CodexBar.patch"
    if digest(patch) != spec["patch_sha256"]:
        raise ValueError("Dependency patch digest mismatch")
    pins = json.loads((root / "Package.resolved").read_text())["pins"]
    pin = next(p for p in pins if p["identity"] == spec["identity"])
    if pin["state"]["revision"] != spec["revision"]:
        raise ValueError("CodexBar pin changed; review and regenerate its patch first")
    declaration = re.search(r'\.package\(url:\s*"https://github.com/steipete/CodexBar",\s*revision:\s*"([0-9a-f]+)"\)',
                            (root / "Package.swift").read_text())
    if not declaration or declaration.group(1) != spec["revision"]:
        raise ValueError("CodexBar manifest revision does not match the patch")
    for name in spec["files"]:
        path = Path(name)
        if path.is_absolute() or ".." in path.parts:
            raise ValueError("Invalid dependency patch path")
    artifact = spec["binary_cache"]
    pin = next(p for p in pins if p["identity"] == artifact["identity"])
    if pin["state"]["revision"] != artifact["revision"]:
        raise ValueError("Binary cache pin changed; review its checksum first")
    return spec, patch


def validate_copy(path, spec, patched, independent=True):
    if path.is_symlink() or (path / ".git").is_symlink() or not (path / ".git").is_dir():
        raise ValueError("Dependency copy must be a Git checkout with local metadata")
    if git(path, "rev-parse", "HEAD") != spec["revision"]:
        raise ValueError("Dependency checkout revision mismatch")
    alternates = path / ".git/objects/info/alternates"
    if independent and alternates.exists() and alternates.read_text().strip():
        raise ValueError("Dependency copy must not rely on shared Git objects")
    status = git(path, "status", "--porcelain", "--untracked-files=all").splitlines()
    expected = sorted(" M " + name for name in spec["files"]) if patched else []
    if sorted(status) != expected:
        raise ValueError("Unexpected dependency edits or untracked files")
    for name, hashes in spec["files"].items():
        file = path / name
        if (file.is_symlink() or not file.resolve().is_relative_to(path.resolve())
                or file.stat().st_nlink != 1 or digest(file) != hashes["after" if patched else "before"]):
            raise ValueError("Dependency source digest mismatch: " + name)


def dependency_state(scratch, identity):
    state = scratch / "workspace-state.json"
    if not state.exists():
        return None
    dependencies = json.loads(state.read_text())["object"]["dependencies"]
    return next((d for d in dependencies if d["packageRef"]["identity"] == identity), None)


def preflight_paths(root, scratch):
    for path in [root / ".build", root / "Packages", scratch]:
        if path.is_symlink() or (path.exists() and not path.is_dir()):
            raise ValueError("Generated dependency directories cannot be links or files")
    link = root / "Packages/codexbar"
    if link.exists() or link.is_symlink():
        raise ValueError("A manual editable CodexBar dependency is already present")
    lock = root / ".build/.coderim-dependencies.lock"
    if lock.is_symlink() or (lock.exists() and not lock.is_file()):
        raise ValueError("Dependency lock must be a regular file")
    owner = scratch / ".coderim-owner.json"
    if owner.is_symlink():
        raise ValueError("Build ownership marker cannot be a link")
    expected = {"schema": 1, "repository": str(root.resolve())}
    if owner.exists():
        if json.loads(owner.read_text()) != expected:
            raise ValueError("Build directory belongs to a different project")
    elif scratch.exists() and any(scratch.iterdir()):
        raise ValueError("Refusing an existing build directory without CodeRim ownership")
    return expected


def reuse_verified_archive(scratch, artifact):
    # Copy a valid cached archive, never the global repository/cache lock state.
    # SwiftPM independently verifies this checksum again during resolution.
    name = re.sub(r"[^a-zA-Z0-9]", "_", artifact["url"])
    source = Path.home() / "Library/Caches/org.swift.swiftpm/artifacts" / name
    if source.is_file() and not source.is_symlink() and digest(source) == artifact["sha256"]:
        target = scratch / "dependency-cache/artifacts" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            shutil.copyfile(source, target)


def owned_checkout(scratch, spec):
    dep = dependency_state(scratch, spec["identity"])
    if (not dep or dep["state"]["name"] != "sourceControlCheckout"
            or dep["state"]["checkoutState"]["revision"] != spec["revision"]
            or dep["packageRef"]["location"] != "https://github.com/steipete/CodexBar"
            or dep["subpath"] != "CodexBar"):
        raise ValueError("SwiftPM dependency identity, revision, or checkout location mismatch")
    checkout = scratch / "checkouts" / dep["subpath"]
    if checkout.is_symlink() or not checkout.resolve().is_relative_to(scratch.resolve()):
        raise ValueError("SwiftPM checkout must belong to this build directory")
    alternates = checkout / ".git/objects/info/alternates"
    if not alternates.is_file() or alternates.is_symlink():
        raise ValueError("SwiftPM checkout must retain its scratch-owned Git object relationship")
    paths = alternates.read_text().splitlines()
    repositories = scratch / "repositories"
    if repositories.is_symlink() or not repositories.resolve().is_relative_to(scratch.resolve()):
        raise ValueError("SwiftPM repositories must belong to this build directory")
    if not paths or any(not Path(path).is_absolute() or not Path(path).is_dir()
                        or not Path(path).resolve().is_relative_to(repositories.resolve()) for path in paths):
        raise ValueError("SwiftPM Git objects must belong to this build directory")
    return checkout


def verify(root, scratch, spec=None):
    preflight_paths(root, scratch)
    spec = spec or load_spec(root)[0]
    checkout = owned_checkout(scratch, spec)
    validate_copy(checkout, spec, patched=True, independent=False)
    result = {"revision": spec["revision"], "patch_sha256": spec["patch_sha256"],
              "source_path": str(checkout), "files": {p: h["after"] for p, h in spec["files"].items()}}
    receipt = scratch / "coderim-dependency-receipt.json"
    if receipt.is_symlink() or not receipt.is_file() or json.loads(receipt.read_text()) != result:
        raise ValueError("Prepared dependency receipt does not match the current patch")
    return result


def prepare(root, scratch):
    owner = preflight_paths(root, scratch)
    spec, patch = load_spec(root)
    scratch.mkdir(parents=True, exist_ok=True)
    (scratch / ".coderim-owner.json").write_text(json.dumps(owner) + "\n")
    dep = dependency_state(scratch, spec["identity"])
    if dep is None:
        reuse_verified_archive(scratch, spec["binary_cache"])
        run(["swift", "package", "--package-path", str(root), "--scratch-path", str(scratch),
             "--cache-path", str(scratch / "dependency-cache"), "--force-resolved-versions", "resolve"])
        dep = dependency_state(scratch, spec["identity"])
    checkout = owned_checkout(scratch, spec)
    receipt = scratch / "coderim-dependency-receipt.json"
    if receipt.is_symlink():
        raise ValueError("Dependency receipt cannot be a link")
    if not receipt.exists():
        validate_copy(checkout, spec, patched=False, independent=False)
        # Preserve SwiftPM's checkout/bare-repository relationship. Git objects
        # are read-only inputs; only the explicitly listed unshared working files are patched.
        backup = scratch / "coderim-upstream-CodexBar"
        if backup.exists() or backup.is_symlink():
            raise ValueError("An interrupted dependency preparation needs inspection")
        for name in spec["files"]:
            destination = backup / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(checkout / name, destination)
        run(["git", "-C", str(checkout), "apply", "--check", str(patch)])
        run(["git", "-C", str(checkout), "apply", str(patch)])
    validate_copy(checkout, spec, patched=True, independent=False)
    result = {"revision": spec["revision"], "patch_sha256": spec["patch_sha256"],
              "source_path": str(checkout), "files": {p: h["after"] for p, h in spec["files"].items()}}
    if receipt.exists() and json.loads(receipt.read_text()) != result:
        raise ValueError("Prepared dependency receipt does not match the current patch")
    receipt.write_text(json.dumps(result, indent=2) + "\n")
    verify(root, scratch, spec)
    print(json.dumps({"prepared": True, "receipt": str(receipt), **result}))


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scratch-path", type=Path, default=root / ".build/coderim-build")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--verify-only", action="store_true", help="Check build inputs without repairing a reset")
    group.add_argument("--run-swift", nargs=argparse.REMAINDER, help="Prepare, run build/test, and verify under one lock")
    group.add_argument("--show-bin-path", nargs=argparse.REMAINDER, help="Query Swift's product directory under the same source guards")
    args = parser.parse_args()
    command = args.run_swift
    if command is not None and (not command or command[0] not in ["build", "test"]):
        raise ValueError("Use build/test and the wrapper's scratch path")
    arguments = command if command is not None else args.show_bin_path
    if arguments is not None and any(argument.split("=")[0] in
            ["--package-path", "--scratch-path", "--cache-path"] for argument in arguments):
        raise ValueError("Use the wrapper's package, scratch, and cache paths")
    # Check paths before resolve() can hide a symlink and before any mutation.
    preflight_paths(root, args.scratch_path.expanduser().absolute())
    scratch = args.scratch_path.expanduser().resolve()
    lock = root / ".build/.coderim-dependencies.lock"
    lock.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(lock, os.O_CREAT | os.O_APPEND | os.O_WRONLY | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
    with os.fdopen(descriptor, "a") as handle:
        if not stat.S_ISREG(os.fstat(handle.fileno()).st_mode):
            raise ValueError("Dependency lock must be a regular file")
        fcntl.flock(handle, fcntl.LOCK_EX)
        if args.verify_only:
            print(json.dumps({"verified": True, **verify(root, scratch)}))
        elif args.show_bin_path is not None:
            resolved_hash = digest(root / "Package.resolved")
            verify(root, scratch)
            result = run(["swift", "build", *args.show_bin_path, "--show-bin-path",
                          "--scratch-path", str(scratch), "--cache-path", str(scratch / "dependency-cache")],
                         cwd=root, capture_output=True, text=True)
            verify(root, scratch)
            if digest(root / "Package.resolved") != resolved_hash:
                raise ValueError("SwiftPM changed the pinned dependency lockfile")
            directory = result.stdout.strip()
            if "\n" in directory or not Path(directory).is_absolute() or not Path(directory).resolve().is_relative_to(scratch):
                raise ValueError("Swift product directory must belong to this build")
            print(directory)
        else:
            resolved_hash = digest(root / "Package.resolved")
            prepare(root, scratch)
            if digest(root / "Package.resolved") != resolved_hash:
                raise ValueError("SwiftPM changed the pinned dependency lockfile")
            if args.run_swift is not None:
                status = subprocess.run(["swift", *command, "--scratch-path", str(scratch),
                                         "--cache-path", str(scratch / "dependency-cache")], cwd=root).returncode
                verify(root, scratch)
                if digest(root / "Package.resolved") != resolved_hash:
                    raise ValueError("SwiftPM changed the pinned dependency lockfile")
                if status:
                    sys.exit(status)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, StopIteration, OSError, subprocess.CalledProcessError) as error:
        print("Dependency preparation failed: " + str(error), file=sys.stderr)
        sys.exit(1)
