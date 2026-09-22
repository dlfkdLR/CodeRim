#!/usr/bin/env python3
"""Deterministic binary-payload inventory + neutral Win32 RCDATA input. This does not sign or trust files."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct

MAX_FILES = 4095  # Reserve the signed worker entry appended by the verified loader.
MAX_FILE = 512 * 1024 * 1024
MAX_TOTAL = 512 * 1024 * 1024  # Reserve at most 512 MiB for the final self-contained worker.
MAX_MANIFEST = 1024 * 1024 - 1024  # Reserve the final worker entry.
WORKER = "CodeRim.UpdateWorker.exe"
RECEIPT = ".coderim-install.json"


def check_path(value: str) -> None:
    if not 1 <= len(value) <= 240 or any(c in value for c in "\\:") or value.startswith("/") or value.endswith("/"):
        raise ValueError("Payload path is not portable and relative")
    parts = value.split("/")
    if len(parts) > 16:
        raise ValueError("Payload path is too deep")
    for part in parts:
        if not 1 <= len(part) <= 120 or part in (".", "..") or part != part.strip() or part.endswith(".") or part.lower() == RECEIPT:
            raise ValueError("Unsupported payload path component")
        if any(ord(c) < 32 or ord(c) == 127 or c in '<>"|?*' for c in part):
            raise ValueError("Unsupported payload path character")
        name = part.split(".")[0].upper()
        if name in ("CON", "PRN", "AUX", "NUL") or (len(name) == 4 and name[:3] in ("COM", "LPT") and name[3] in "123456789¹²³"):
            raise ValueError("Reserved Windows device path")


def no_links(path: Path) -> None:
    for item in (path, *path.parents):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked/reparse payload or output paths are not supported")


def inventory(root: Path, version: str, architecture: str) -> dict:
    if not root.is_absolute() or not root.is_dir():
        raise ValueError("An absolute payload directory is required")
    no_links(root)
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version) or any(int(p) > 2147483647 for p in version.split(".")):
        raise ValueError("A canonical stable version is required")
    if architecture not in ("x64", "arm64"):
        raise ValueError("Unsupported payload architecture")
    files: list[dict] = []
    names: set[str] = set()
    total = 0
    directories = 0

    def visit(directory: Path) -> None:
        nonlocal total, directories
        with os.scandir(directory) as entries:
            for entry in sorted(entries, key=lambda item: item.name):
                relative = Path(entry.path).relative_to(root).as_posix()
                check_path(relative)
                folded = relative.casefold()
                if folded in names:
                    raise ValueError("Case-ambiguous payload path")
                names.add(folded)
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                    raise ValueError("Linked/reparse payload entry")
                if stat.S_ISDIR(info.st_mode):
                    directories += 1
                    if directories > MAX_FILES:
                        raise ValueError("Too many payload directories")
                    visit(Path(entry.path))
                    continue
                if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_FILE:
                    raise ValueError("Unsupported payload entry or size")
                if relative.lower() == WORKER.lower():
                    raise ValueError("Generate the payload manifest before publishing the worker")
                total += info.st_size
                if len(files) >= MAX_FILES or total > MAX_TOTAL:
                    raise ValueError("Payload exceeds its bounds")
                digest = hashlib.sha256()
                flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
                with os.fdopen(os.open(entry.path, flags), "rb") as stream:
                    opened = os.fstat(stream.fileno())
                    if (opened.st_dev, opened.st_ino, opened.st_size) != (info.st_dev, info.st_ino, info.st_size):
                        raise ValueError("Payload identity changed during inventory")
                    read = 0
                    while chunk := stream.read(65536):
                        read += len(chunk)
                        if read > opened.st_size:
                            raise ValueError("Payload grew during inventory")
                        digest.update(chunk)
                    after = os.fstat(stream.fileno())
                if read != info.st_size or (after.st_size, after.st_mtime_ns, after.st_ctime_ns) != (opened.st_size, opened.st_mtime_ns, opened.st_ctime_ns):
                    raise ValueError("Payload changed during inventory")
                files.append({"path": relative, "size": read, "sha256": digest.hexdigest()})
    visit(root)
    if not {"CodeRim.exe", "CodeRimCLI.exe"}.issubset({file["path"] for file in files}):
        raise ValueError("Both application and CLI must be in the payload")
    return {"schema": 1, "product": "CodeRim", "version": version, "architecture": architecture, "files": sorted(files, key=lambda file: file["path"])}


def encode(manifest: dict) -> bytes:
    data = json.dumps(manifest, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    if len(data) > MAX_MANIFEST:
        raise ValueError("Manifest exceeds its bounds")
    return data


def resource(data: bytes) -> bytes:
    if not 1 <= len(data) <= MAX_MANIFEST:
        raise ValueError("Invalid manifest resource size")
    # Each .res entry has DWORD-aligned data and a 32-byte numeric-type/name header.
    def header(length: int, kind: int, identifier: int) -> bytes:
        return struct.pack("<IIHHHHIHHII", length, 32, 0xFFFF, kind, 0xFFFF, identifier, 0, 0x1030 if length else 0, 0, 0, 0)
    return header(0, 0, 0) + header(len(data), 10, 21001) + data + bytes((-len(data)) % 4)


def write_new(path: Path, data: bytes, root: Path) -> None:
    if not path.is_absolute() or path != Path(os.path.abspath(path)) or path.exists() or path.is_symlink():
        raise ValueError("A new absolute output path is required")
    no_links(path.parent)
    if path.is_relative_to(root):
        raise ValueError("Manifest outputs must stay outside the inventoried payload")
    with path.open("xb") as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--architecture", choices=("x64", "arm64"), required=True)
    parser.add_argument("--json", type=Path, required=True)
    parser.add_argument("--resource", type=Path, required=True)
    options = parser.parse_args()
    data = encode(inventory(options.payload, options.version, options.architecture))
    write_new(options.json, data, options.payload)
    write_new(options.resource, resource(data), options.payload)
    print(json.dumps({"files": len(json.loads(data)["files"]), "manifestSha256": hashlib.sha256(data).hexdigest()}))


if __name__ == "__main__":
    main()
