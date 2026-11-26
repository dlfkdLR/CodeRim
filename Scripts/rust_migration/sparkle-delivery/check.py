#!/usr/bin/env python3
"""Verify the public stable artifacts and exercise upstream Sparkle's information-only probe."""
import argparse
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
from threading import Thread
import uuid
from xml.etree import ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
SPARKLE = "{http://www.andymatuschak.org/xml-namespaces/sparkle}"
FOOTER = re.compile(rb"<!-- sparkle-signatures:\nedSignature: ([A-Za-z0-9+/=]+)\nlength: (\d+)\n-->\s*\Z")
DISTRIBUTION = "52bf9e88cdd972fc0c81501377a880e90d47031bd8ca5462488f843e2609e192"
# Upstream sparkle-cli at the 2.9.6 release commit ac2def288cbff5cfc7df3ffef6abdf45b72bcb0a.
SOURCES = {
    "main.m": "6df90a46bc481a76261f1b5c1a3123572f316fa3dc4463ab4269e2f5ecd0051e",
    "SPUCommandLineDriver.h": "3f7d8efdd5b1be0e150ecb75795e70b0cd4fadbd1b18818b2dcf7c26f0dd6171",
    "SPUCommandLineDriver.m": "0924b26df77f726afafed2bb149998c4c27813574c0a9e67ddf69cc0419490b1",
    "SPUCommandLineUserDriver.h": "525cc91b76b7c88d3f0bc476cfdf0df72c50739c31d3ff71e9da9a6bb4773c42",
    "SPUCommandLineUserDriver.m": "7e23c34763c74afbdbb7095cdd02d23343e9d0836e358e9da1099ff198e51600",
}


def check(ok, description):
    if not ok:
        raise ValueError(description)


def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def call(*args, **kwargs):
    return subprocess.run(list(map(str, args)), check=True, capture_output=True, **kwargs).stdout


def verify_public(crypto, args, tmp):
    config = dict(line.split("=", 1) for line in (REPO / "Config/Release.env").read_text().splitlines() if "=" in line)
    marketing, build = config["MARKETING_VERSION"], config["BUILD_NUMBER"]
    key, repo = config["SPARKLE_PUBLIC_KEY"], config["RELEASE_REPOSITORY"]
    feed_url = f'https://raw.githubusercontent.com/{repo}/{config["UPDATE_FEED_BRANCH"]}/appcast.xml'
    name = f'CodeRim-{marketing}.zip'
    archive_url = f'https://github.com/{repo}/releases/download/v{marketing}/{name}'
    feed = args.feed.read_bytes()
    footer = FOOTER.search(feed)
    check(footer is not None and int(footer[2]) == footer.start(), "signed appcast length/footer mismatch")
    xml = tmp / "public-feed-body.xml"
    xml.write_bytes(feed[:footer.start()])
    call(crypto, "verify", key, footer[1].decode("ascii"), xml)
    items = [item for item in ET.fromstring(xml.read_bytes()).findall("channel/item")
             if item.findtext(SPARKLE+"version") == build and item.findtext(SPARKLE+"shortVersionString") == marketing]
    check(len(items) == 1, "expected one stable item for release version/build")
    enclosure = items[0].find("enclosure")
    check(enclosure is not None and enclosure.get("url") == archive_url, "unexpected release enclosure URL")
    check(int(enclosure.attrib["length"]) == args.archive.stat().st_size, "archive length mismatch")
    archive_sig = enclosure.attrib[SPARKLE+"edSignature"]
    call(crypto, "verify", key, archive_sig, args.archive)
    archive_sha = sha(args.archive)
    sums = [row.split() for row in args.checksums.read_text().splitlines()]
    check([row[0] for row in sums if len(row) == 2 and row[1] == name] == [archive_sha], "published SHA256SUMS mismatch")
    with zipfile.ZipFile(args.archive) as archive:
        plist = plistlib.loads(archive.read("CodeRim.app/Contents/Info.plist"))
    expected = {"CFBundleIdentifier": config["BUNDLE_IDENTIFIER"], "CFBundleVersion": build,
                "CFBundleShortVersionString": marketing, "SUPublicEDKey": key, "SUFeedURL": feed_url,
                "SURequireSignedFeed": True, "SUVerifyUpdateBeforeExtraction": True,
                "SUSignedFeedFailureExpirationInterval": 0}
    check(all(type(plist.get(k)) is type(v) and plist[k] == v for k, v in expected.items()), "archive trust policy mismatch")
    check(items[0].findtext(SPARKLE+"minimumSystemVersion") == plist["LSMinimumSystemVersion"], "macOS minimum mismatch")
    installed = tmp / "released"
    installed.mkdir()
    call("/usr/bin/ditto", "-x", "-k", args.archive, installed)
    release_app = installed / "CodeRim.app"
    requirement = f'anchor apple generic and identifier "{config["BUNDLE_IDENTIFIER"]}" and certificate leaf[field.1.2.840.113635.100.6.1.13] exists'
    call("/usr/bin/codesign", "--verify", "--strict", "--deep", release_app)
    call("/usr/bin/codesign", "--verify", "-R="+requirement, release_app)
    # An equal-length alteration must fail cryptographically, even if length still matches.
    edited = tmp / "corrupt"
    for original, signature in [(xml, footer[1].decode("ascii")), (args.archive, archive_sig)]:
        with original.open("rb") as src, edited.open("wb") as dst:
            dst.write(bytes([src.read(1)[0] ^ 1]))
            shutil.copyfileobj(src, dst)
        status = subprocess.run([crypto, "verify", key, signature, edited], capture_output=True).returncode
        check(status == 1, "an altered payload did not produce a bad signature")
    return {"version": marketing, "build": build, "archiveUrl": archive_url, "archiveBytes": args.archive.stat().st_size,
            "archiveSHA256": archive_sha, "appcastSHA256": sha(args.feed), "signedBodyBytes": footer.start(),
            "feedURL": feed_url, "publicKey": key, "verifiedDeveloperIDBundle": True, "invalidControls": ["appcast", "archive"]}


def compile_sparkle(tmp, args):
    check(sha(args.sparkle_archive) == DISTRIBUTION, "Sparkle 2.9.6 distribution hash mismatch")
    for name, digest in SOURCES.items():
        check(sha(args.cli_source / name) == digest, f"pinned upstream source mismatch for {name}")
    call("/usr/bin/tar", "-xf", args.sparkle_archive, "-C", tmp, "./Sparkle.framework", "./bin/sign_update")
    cli = tmp / "sparkle-cli"
    call("xcrun", "clang", "-fobjc-arc", "-fmodules", "-DSPU_OBJC_DIRECT=__attribute__((objc_direct))",
         "-DSPU_OBJC_DIRECT_MEMBERS=__attribute__((objc_direct_members))", "-F", tmp, "-framework", "Sparkle",
         "-framework", "AppKit", "-Xlinker", "-rpath", "-Xlinker", tmp, *[args.cli_source / n for n in SOURCES if n.endswith(".m")], "-o", cli)
    return cli


def qa_probes(tmp, crypto, args, stable):
    cli = compile_sparkle(tmp, args)
    signer = tmp / "bin/sign_update"
    bundle = tmp / "Fixture.app"
    macos = bundle / "Contents/MacOS"
    macos.mkdir(parents=True)
    call("xcrun", "clang", "-x", "c", "-", "-o", macos / "Fixture", input=b"int main(void){return 0;}")
    seed = tmp / "ephemeral-key"
    public_key = call(crypto, "generate", seed).decode().strip()
    other_seed = tmp / "other-key"
    call(crypto, "generate", other_seed)
    qa_archive = tmp / "Fixture.zip"
    requests = []
    content = {}

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            path = self.path.split("?", 1)[0]
            requests.append(path)
            data = content.get(path)
            if data is None:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", "application/rss+xml")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *_):
            pass

    with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        host = f"http://127.0.0.1:{server.server_port}"
        feed_url = host + "/appcast.xml"
        identifier = "org.coderim.qa.sparkle." + uuid.uuid4().hex
        info = {"CFBundlePackageType": "APPL", "CFBundleIdentifier": identifier, "CFBundleExecutable": "Fixture",
                "CFBundleName": "Disposable Sparkle Fixture", "CFBundleVersion": "2", "CFBundleShortVersionString": "2.0",
                "SUFeedURL": feed_url, "SUPublicEDKey": public_key, "SURequireSignedFeed": True,
                "SUVerifyUpdateBeforeExtraction": True, "SUSignedFeedFailureExpirationInterval": 0,
                "SUEnableAutomaticChecks": False, "SUAutomaticallyUpdate": False}
        plist_path = bundle / "Contents/Info.plist"
        plist_path.write_bytes(plistlib.dumps(info))
        call("/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", bundle)
        call("/usr/bin/ditto", "-c", "-k", "--keepParent", bundle, qa_archive)
        ed = call(signer, "--ed-key-file", seed, "-p", qa_archive).decode().strip()
        call(crypto, "verify", public_key, ed, qa_archive)
        xml = tmp / "appcast.xml"

        def feed(key, version):
            xml.write_text(f'''<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle"><channel><title>QA</title>
<item><title>Fixture {version}</title><sparkle:version>{version}</sparkle:version>
<sparkle:shortVersionString>{version}.0</sparkle:shortVersionString><enclosure url="{host}/Fixture.zip"
length="{qa_archive.stat().st_size}" type="application/octet-stream" sparkle:edSignature="{ed}"/></item></channel></rss>''')
            if key is not None:
                call(signer, "--ed-key-file", key, xml)
            return xml.read_bytes()

        valid = feed(seed, 2)
        variations = [("newer", valid, 0, "Update available!"), ("same", feed(seed, 1), 4, "No new update"),
                      ("tampered", valid.replace(b"Fixture 2", b"Fikture 2"), 1, None),
                      ("wrongKey", feed(other_seed, 2), 1, None), ("unsigned", feed(None, 2), 1, None)]
        cases = [(name, data, status, message, public_key, "1") for name, data, status, message in variations]
        for label, current, status, message in [("publishedNewer", str(int(stable["build"])-1), 0, "Update available!"),
                                               ("publishedCurrent", stable["build"], 4, "No new update")]:
            cases.append((label, args.feed.read_bytes(), status, message, stable["publicKey"], current))
        # Pin outbound access to this single loopback port, including for the unmodified public feed.
        policy = f'(version 1) (allow default) (deny network-outbound) (allow network-outbound (remote ip "localhost:{server.server_port}"))'
        qa_home = tmp / "qa-home"
        qa_home.mkdir()
        env = dict(os.environ, CFFIXED_USER_HOME=str(qa_home), NO_PROXY="127.0.0.1", no_proxy="127.0.0.1")
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        results = []
        try:
            for name, data, expected, message, host_key, current in cases:
                info.update(CFBundleVersion=current, CFBundleShortVersionString=current+".0", SUPublicEDKey=host_key)
                plist_path.write_bytes(plistlib.dumps(info))
                call("/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", bundle)
                content["/appcast.xml"] = data
                requests.clear()
                proc = subprocess.run(["/usr/bin/sandbox-exec", "-p", policy, cli, "--probe", "--verbose", bundle], env=env, capture_output=True, text=True, timeout=35)
                detail = proc.stdout + proc.stderr
                check(proc.returncode == expected and (message is None or message in detail),
                      f"{name}: status={proc.returncode}, {detail}")
                check(requests and set(requests) == {"/appcast.xml"}, f"{name}: unexpected requests {requests}")
                if expected == 1:
                    check("improperly signed" in detail.lower(), f"{name}: expected a signing-related rejection: {detail}")
                check(plistlib.loads(plist_path.read_bytes())["CFBundleVersion"] == current, "QA host changed during probe")
                results.append({"case": name, "exit": proc.returncode, "requested": list(requests),
                                "diagnostic": [line for line in detail.splitlines() if any(
                                    word in line.lower() for word in ["update available", "signature", "unsigned", "signed"])]})
        finally:
            server.shutdown()
            thread.join()
    return {"mode": "upstream sparkle-cli --probe", "framework": "2.9.6", "qaBundle": identifier,
            "signedArchiveBytes": qa_archive.stat().st_size, "cases": results}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ["feed", "archive", "checksums", "sparkle-archive", "cli-source"]:
        ap.add_argument("--"+name, required=True, type=lambda value: Path(value).resolve(strict=True))
    args = ap.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-sparkle-qa-") as directory:
        tmp = Path(directory).resolve()
        crypto = tmp / "crypto"
        call("xcrun", "swiftc", "-O", "-warnings-as-errors", HERE / "crypto.swift", "-o", crypto)
        stable = verify_public(crypto, args, tmp)
        result = {"publicStable": stable, "localQA": qa_probes(tmp, crypto, args, stable)}
        print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
