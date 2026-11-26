#!/usr/bin/env python3
"""Replay public synthetic Copilot quota bodies through the shipped Swift method and optional bridge."""

import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
TAG = "v2.1.41"


def fixture():
    def doc(quotas, **fields):
        return json.dumps(dict(quota_snapshots=quotas, **fields), separators=(",", ":"))

    quotas = {
        "completions": {"unlimited": True},
        "chat": {"entitlement": 300, "remaining": 213, "percent_remaining": 71.0},
        "premium_interactions": {"entitlement": 300, "used": 37},
    }
    common = doc(
        quotas,
        quota_reset_date_utc="2028-03-01T00:00:00Z",
        quota_reset_date="2028-03-01",
    )
    numeric = doc(quotas, quota_reset_date_utc=1835481600000, quota_reset_date="bad")
    plain = doc(quotas)
    bare = doc(quotas, quota_reset_date="2028-03-01")
    unusual = doc(
        {"한도": {"remaining": 8.5, "reset_date": "2028-03-01T00:00:00.123Z"}},
        extra="é",
    )
    cases = [
        common,
        numeric,
        plain,
        bare,
        unusual,
        "{}",
        "[]",
        "null",
        "{",
        doc({}),
        doc(None),
        doc({"chat": 9, "a": [], "B": True}),
        doc({"a": {"remaining": 9}}, extra=[[[]]]),
        '{"quota_snapshots":{"chat":{"used":8},"chat":{"used":9}}}',
        '{"quota_snapshots":{"z":{"used":7,"used":2}}}',
        '{"quota_snapshots":{"escape\\u005fid":{"used":2}}}',
        '{"quota_snapshots":{"chat":{"entitlement":0,"remaining":1}},"other":1e400}',
        doc({str(i): {"used": i} for i in range(33)}),
        doc({"": {"used": 0}, "a_b": {"remaining": 0}}),
        doc({"a": {"used": 23}}, ignored="a" * 1048576),
        doc({"a": {"used": 2}}, ignored=[[[[[[1]]]]]]),
    ]
    for ent in [
        None,
        False,
        True,
        -2,
        0,
        1,
        2,
        41,
        1.5,
        "3",
        [],
        500000000000001,
        2**53 + 1,
        2**63,
    ]:
        for rem in [None, -1, 0, 3, 8.5, True, False]:
            for used in [None, -3, 0, 2, 1.25, False]:
                cases.append(
                    doc({"chat": {"entitlement": ent, "remaining": rem, "used": used}})
                )
    for stamp in [
        0,
        1,
        10000000000,
        10000000001,
        -17,
        0.5,
        True,
        False,
        None,
        "",
        "bad",
        "2028-03-01",
        "2028-03-01T00:00:00Z",
        "2026-11-19T05:29:36+02:00",
        "2028-03-01T00:00:00.123456Z",
    ]:
        for field in ["reset_date", "reset_at", "resets_at", "quota_reset_at"]:
            cases.append(
                doc(
                    {"chat": {field: stamp, "used": 7, "entitlement": 9}},
                    quota_reset_date_utc=300,
                )
            )
        cases.append(
            doc(
                {"chat": {"reset_date": None, "reset_at": stamp, "remaining": 3}},
                quota_reset_date=10,
            )
        )
    for ids in [
        ["completions", "A", "chat", "a", "premium_interactions", "Z", "1"],
        ["é", "한도", "😀", "quota\\id", "word_word"],
    ]:
        cases.append(doc({key: {"used": i} for i, key in enumerate(ids)}))
    for token in [
        "1e-200",
        "1e-129",
        "2.3e-128",
        "9e127",
        "1.125e200",
        "1e201",
        "1e-400",
        "1e400",
        "-0",
        "-0.0",
        "-1e-200",
        "0.000001250",
        "3.5e+5",
        "01",
        "0e",
        "NaN",
    ]:
        for field in ["used", "percent_remaining"]:
            cases.append(
                '{"quota_snapshots":{"chat":{"remaining":7,"'
                + field
                + '":'
                + token
                + "}}}"
            )
    return cases, {
        "standard-iso": common,
        "numeric-reset": numeric,
        "no-reset": plain,
        "bare-reset": bare,
        "compatibility": unusual,
    }


def read(path, release):
    return (
        subprocess.check_output(["git", "-C", ROOT, "show", f"{TAG}:{path}"], text=True)
        if release
        else (ROOT / path).read_text()
    )


def declaration(source, prefix):
    return prefix + source.split(prefix, 1)[1].split("\n}", 1)[0] + "\n}\n"


def build(side, args, root):
    # Include the real parsing method and its data/error/date dependencies without
    # compiling any request, credential or process integration into this fixture.
    release = side == "release"
    provider = read("Sources/CodeRim/Notch/CopilotNotchProvider.swift", release)
    if side in ["previous-rust", "shared-date-control"]:
        # Before this change the opt-in product already used Rust's timestamp fast path.
        provider = read("Sources/CodeRim/Notch/CopilotNotchProvider.swift", True)
    if side == "shared-date-control":
        provider = provider.replace(
            "        let keys = order +",
            """        lazy var rootReset = date(root["quota_reset_date_utc"]) ?? date(root["quota_reset_date"])
        let keys = order +""",
        ).replace(
            "quota: quota, root: root)", "quota: quota, rootReset: { rootReset })"
        )
        provider = provider.replace(
            "root: [String: Any]) -> LimitWindow?",
            "rootReset: () -> Date?) -> LimitWindow?",
        )
        provider = provider.replace(
            '?? date(root["quota_reset_date_utc"])\n            ?? date(root["quota_reset_date"])',
            "?? rootReset()",
        )
    domain = read("Sources/CodeRim/Notch/NotchUsageModel.swift", release)
    error = read("Sources/CodeRim/Notch/NotchProvider.swift", release)
    dates = read("Sources/CodeRim/Utilities/TranscriptLines.swift", release)
    text = "import Foundation\n#if CODERIM_RUST_ACCOUNTING\nimport CCodeRimAccounting\n#endif\n"
    text += (
        declaration(domain, "struct LimitWindow:").split("    /// Compact counts", 1)[0]
        + "}\n"
    )
    text += declaration(error, "enum NotchProviderError:")
    text += "enum TranscriptLines {\n" + declaration(
        dates, "    static func parse(_ stamp: String)"
    )
    text += declaration(provider, "enum GitHubCopilotUsage")
    source = root / (side + ".swift")
    source.write_text(text)
    exe = root / side
    flags = (
        [
            "-D",
            "CODERIM_RUST_ACCOUNTING",
            "-I",
            ROOT / "Rust/bridge/include",
            args.archive.resolve(),
        ]
        if side != "release"
        else []
    )
    subprocess.run(
        [
            "swiftc",
            "-O",
            "-whole-module-optimization",
            "-swift-version",
            "6",
            "-target",
            args.arch + "-apple-macos14.0",
            "-warnings-as-errors",
            source,
            HERE / "main.swift",
            *flags,
            "-o",
            exe,
        ],
        check=True,
    )
    return exe


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive", type=Path, required=True)
    p.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    p.add_argument("--benchmark", type=Path)
    args = p.parse_args()
    cases, workloads = fixture()
    with tempfile.TemporaryDirectory(prefix="coderim-copilot-") as tmp:
        bins = {
            s: build(s, args, Path(tmp))
            for s in ["release", "previous-rust", "shared-date-control", "rust"]
        }
        rows = {
            s: subprocess.check_output(
                [exe], input=json.dumps(cases).encode()
            ).splitlines()
            for s, exe in bins.items()
        }
        for s, out in rows.items():
            if len(out) != len(cases):
                raise ValueError(f"Missing {s} results")
            for i, (ref, result) in enumerate(zip(rows["release"], out)):
                if ref != result:
                    raise ValueError(f"Case {i}: {ref!r} differs from {s} {result!r}")
        print(
            args.arch,
            len(cases),
            hashlib.sha256(b"\n".join(rows["release"])).hexdigest(),
            flush=True,
        )
        native_counts = list(
            map(
                int,
                subprocess.check_output(
                    [bins["rust"], "coverage"], input=json.dumps(cases).encode()
                ).splitlines(),
            )
        )
        if native_counts[:5] != [2, 2, 2, 2, -1] or len(native_counts) != len(cases):
            raise ValueError(
                "Expected metered bodies in Rust and escaped Unicode in Swift"
            )
        print(
            "Rust handled",
            sum(x >= 0 for x in native_counts),
            "of",
            len(cases),
            flush=True,
        )
        # These distinct JSON keys are canonically equal to Swift. Its released
        # dictionary bridge can keep either spelling/value across process hash seeds.
        collision = json.dumps(
            {"quota_snapshots": {"é": {"used": 0}, "é": {"used": 1}}}
        )
        payload = json.dumps([collision]).encode()
        assert (
            subprocess.check_output([bins["rust"], "coverage"], input=payload).strip()
            == b"-1"
        )
        for binary in bins.values():
            [row] = json.loads(subprocess.check_output([binary], input=payload))
            if (
                row["id"] not in ("é", "é")
                or row["used"] not in (0, 1)
                or set(row) != {"id", "label", "used"}
            ):
                raise ValueError(
                    "Unexpected canonically equal Foundation dictionary key result"
                )
        if args.benchmark:
            measurements = {}
            for name, body in workloads.items():
                samples = {s: [] for s in bins}
                for pair in range(7):
                    order = list(bins)
                    order = order[pair % len(order) :] + order[: pair % len(order)]
                    if pair % 2:
                        order.reverse()
                    sums = set()
                    for s in order:
                        checksum, ns = map(
                            int,
                            subprocess.check_output(
                                [bins[s], "80"], input=json.dumps([body]).encode()
                            ).split(),
                        )
                        sums.add(checksum)
                        samples[s].append(ns / (80 * 1000))
                    if len(sums) != 1:
                        raise ValueError(f"Benchmark checksum differs: {name}")
                measurements[name] = {
                    "samplesUs": samples,
                    "medianUs": {s: statistics.median(v) for s, v in samples.items()},
                }
            args.benchmark.write_text(json.dumps(measurements, indent=2) + "\n")


if __name__ == "__main__":
    main()
