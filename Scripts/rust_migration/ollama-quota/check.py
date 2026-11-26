"""Compare the shipping Ollama responses against the v2.1.41 parser, using synthetic data only."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import platform
import random
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
NOTCH = "Sources/CodeRim/Notch/"


def payload(limits, **other):
    return json.dumps(dict(limits=limits, **other), separators=(",", ":"), ensure_ascii=False).encode()


def fixtures():
    def models(n):
        return [{"name": f"synthetic-{i}:1b", "request_count": i + 1} for i in range(n)]
    common = {
        "monthly-three-models": payload({"monthly": {"usage": 0.152, "models": models(3)}},
                                        activity={"period": {"type": "last_4_weeks"}, "models": []}),
        "legacy-two-periods": payload({"session": {"usage": 0.12, "models": models(1)},
                                       "weekly": {"usage": 0.41, "models": models(1)}}),
        "monthly-32-models": payload({"monthly": {"usage": 0.42, "models": models(32)}}),
        "ordinary-empty": payload({}),
        "unicode-model": payload({"monthly": {"usage": 0.50, "models": [{"name": "한도", "request_count": 4}]}}),
        "foundation-escaped-model": br'{"limits":{"weekly":{"models":[{"name":"a\u002eb","request_count":9}]}}}',
    }
    values = list(common.values()) + [b"", b"not JSON", b"{}", b"null", b"[]", b"\xff",
                                      b'{"limits":{"monthly":{"usage":1}},"extra":"\xff"}',
                                      b'\xef\xbb\xbf' + common["legacy-two-periods"]]
    scalars = [None, True, False, -1, 0, 1, 1.5, 0.1, 12, 2**31, 2**53 - 1, 2**53 + 1, 2**63,
               "1", "\u0000", "", [], {}, [0.3, []]]
    for key in ("monthly", "session", "weekly"):
        for v in scalars:
            for n in scalars:
                values.append(payload({key: {"usage": v, "models": [{"name": "m", "request_count": n}]}}))
        for n in (None, "", "한글", "e\u0301", 'q"q', "a/b", "\n", 129, [], {}, False):
            values.append(payload({key: {"usage": 0.25, "models": [{"name": n, "request_count": 3}]}}))
        for invalid in (True, [], [[0.5, []]], 1, "x", [[], {}, 1], {}, None):
            values.extend([payload({key: invalid}), payload({key: {"usage": 0.125, "models": invalid}})])
    for token in ("0e4", "1e-200", "1e-129", "1.125e200", "1e400", "1e-400", "-0", "-0.0", "1.00",
                  ".5", "01", "1e", "NaN", "0.123456789012345678901"):
        values.extend([b'{"limits":{"monthly":{"usage":' + token.encode() + b'}}}',
                       b'{"limits":{"monthly":{"usage":0.5}},"extra":' + token.encode() + b'}'])
    for usage in (0, 0.5):
        for session in (0, 0.25):
            for week in (0, 0.75):
                values.append(payload({p: {"usage": v, "models": models(2)} for p, v in
                                       (("weekly", week), ("monthly", usage), ("session", session))}))
    for name in ("", "é", "e\u0301", "서울/지점", "🌘", "a.b", "a\n", "a\x00", "\u2028",
                 "\ufeff", "\ufeffstart", "middle\ufeffend", "\ufffe", "\ufdd0", "\U0010fffd"):
        values.append(payload({"weekly": {"models": [{"name": name, "request_count": 11}]}}))
    values.append(json.dumps({"\ufefflimits": {"monthly": {"usage": 0.5}}}, ensure_ascii=False).encode())
    rng = random.Random(1543)
    for _ in range(1500):
        digits = str(rng.randint(1, 999_999_999_999_999))
        point = rng.randrange(1, len(digits) + 1)
        number = digits[:point] + (("." + digits[point:]) if point < len(digits) else "")
        if rng.randrange(3) == 0:
            number += "e" + str(rng.randint(-199, 199))
        values.append(b'{"limits":{"weekly":{"usage":' + number.encode() + b'}}}')
    values += [b'{"limits":{"monthly":{"usage":0.9,"usage":0.3}}}',
               b'{"limits":{"monthly":{"usage":0.9}},"limits":{}}',
               b'{"limits":{"monthly":{"models":[{"name":"first","name":"second","request_count":1}]}}}',
               b'{"l\\u0069mits":{"monthly":{"usage":0.5}}}', common["monthly-three-models"].decode().encode("utf-16"),
               payload({"monthly": {"usage": 0.3}}, padding="x" * 1048600),
               payload({"monthly": {"usage": 0.3}}, padding=[[[[[[]]]]]]),
               payload({"monthly": {"usage": 0.3, "models": [*models(1), []]}})]
    return values, common


def read(name, original):
    path = NOTCH + name + ".swift"
    if original:
        return subprocess.check_output(["git", "-C", ROOT, "show", "v2.1.41:" + path], text=True)
    return (ROOT / path).read_text()


def build(args, temp, original):
    model = read("NotchUsageModel", original)
    error = read("NotchProvider", original)
    model = model[model.index("struct LimitWindow:"):].split("    /// Compact counts", 1)[0] + "}\n"
    error = error[error.index("enum NotchProviderError:"):].split("\n}", 1)[0] + "\n}\n"
    source = temp / ("original.swift" if original else "candidate.swift")
    source.write_text("import Foundation\n" + model + error + read("OllamaUsage", original))
    flags = [] if original else ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", args.archive]
    out = source.with_suffix("")
    subprocess.run(["swiftc", "-O", "-whole-module-optimization", "-swift-version", "6", "-warnings-as-errors",
                    "-target", args.arch + "-apple-macos14.0", "-Xlinker", "-fatal_warnings",
                    *flags, source, HERE / "main.swift", "-o", out], check=True)
    return out


def run(exe, values, *args):
    data = json.dumps([base64.b64encode(v).decode() for v in values]).encode()
    return subprocess.check_output([exe, *args], input=data).splitlines()


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--archive", type=Path, required=True)
    ap.add_argument("--arch", default=platform.machine())
    ap.add_argument("--benchmark", type=Path)
    args = ap.parse_args()
    values, common = fixtures()
    with tempfile.TemporaryDirectory() as tmp:
        original, candidate = (build(args, Path(tmp), before) for before in (True, False))
        expected, actual = (run(exe, values) for exe in (original, candidate))
        assert len(expected) == len(actual) == len(values)
        for index, (a, b) in enumerate(zip(expected, actual)):
            if a != b:
                raise ValueError(f"Fixture {index} {values[index][:150]!r}: {a} vs {b}")
        coverage = run(candidate, values, "coverage")
        assert coverage[:6] == [b"1"] * 5 + [b"0"]
        print(args.arch, len(values), "matched", coverage.count(b"1"), "Rust",
              hashlib.sha256(b"\n".join(actual)).hexdigest(), flush=True)
        if args.benchmark:
            measurements = {}
            for label, body in common.items():
                samples = {"original": [], "candidate": []}
                for trial in range(7):
                    checksums = []
                    pairs = [("original", original), ("candidate", candidate)]
                    for side, exe in pairs if trial % 2 == 0 else pairs[::-1]:
                        checksum, elapsed = run(exe, [body], "1000")[0].split()
                        samples[side].append(int(elapsed) / 1000)
                        checksums.append(checksum)
                    assert checksums[0] == checksums[1]
                measurements[label] = dict(samplesNS=samples, medianNS={s: statistics.median(v) for s, v in samples.items()})
            args.benchmark.write_text(json.dumps(measurements, indent=2) + "\n")


if __name__ == "__main__":
    main()
