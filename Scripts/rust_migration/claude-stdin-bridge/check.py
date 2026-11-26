#!/usr/bin/env python3
"""Compare the installed status-line helpers on synthetic stdin and private files."""
import argparse
import json
import math
from pathlib import Path
import re
import random
import shutil
import stat
import subprocess
import tempfile
import time

LIMIT = 1_048_576
PATH = Path("Library/Application Support/CodexMeter/Claude/ClaudeLimits.json")
POLICY = """(version 1)(allow default)(deny network*)
(deny file-read-data (subpath (param "PERSONAL")))
(deny file-write* (subpath (param "PERSONAL")))
(deny mach-lookup (global-name "com.apple.securityd") (global-name "com.apple.securityd.xpc"))"""


def window(used, reset="null"):
    return f'{{"used_percentage":{used},"resets_at":{reset}}}'


def quota(short, long="null"):
    return f'{{"rate_limits":{{"five_hour":{short},"seven_day":{long}}}}}'


def documents():
    values = ["", "{}", "[]", "true", "13", "null", "{", '{"rate_limits":[]}',
              '{"rate_limits":{"five_hour":["one"]}}', '{"rate_limits":{}}',
              '{"rate_limits":{"five_hour":{"used_percentage":30,"used_percentage":98}}}',
              '{"rate_limits":{"five_hour":{"used_percentage":42}},"rate_limits":null}',
              '{"other":1e1000}', '{"rates":"\\uD800"}', '{"unused":"상태 🌒"}']
    used = ["-0", "-0.0", "-0.125", "0", "0.00001", "1e-7", "0.5", "1.5", "49.5",
            "99.99999999999997", "100", "100.00000000000003", "1e200",
            '"76"', "true", "null", "[]", "{}", "95.17", "2.99"]
    resets = ["null", "-0.0", "1e-20", "-1e17", "978307199.9999999",
              "1.7976931348623157e308", "1750000000.123456", "true"]
    for i, item in enumerate(used):
        for j, reset in enumerate(resets):
            values.append(quota(window(item, reset), window(used[(i + j) % len(used)], "1577836800")))
    values.extend([quota(window("26.25")), quota(window("14", "85.25"), window("52.5", "8666"))])
    # Include alternating halfway/adjacent fractions and reset values across the
    # binary64 range. JSONEncoder uses shortest decimal representations.
    rng = random.Random(41)
    for _ in range(180):
        fraction = repr(rng.randrange(1 << 52) * 100 / (1 << 52))
        reset = repr(math.ldexp(rng.uniform(-1, 1), rng.randint(-1020, 1020)))
        values.append(quota(window(fraction, reset), window("50")))
    values.extend([quota("[35, 100]"), '{"rate_limits":[{"used_percentage":12}]}',
                   '{"rate_limits":{"five_hour":{"used_percentage":3},"five_hour":{"used_percentage":8}}}',
                   quota(window("50"))[:-1] + ',"unused":{"synthetic":"합성 — 🌗"}}',
                   '{"rate_limits":{"seven_day":{"used_percentage":71}},"ignored":' + '[' * 180 + '0' + ']' * 180 + '}'])
    for reset in ("-1664935812735758.25", "860508882399600.25", "9007199254740992",
                  "9007199254740994", "0.0001", "0.00001", "-0.0"):
        values.append(quota(window("80.25", reset)))
    encoded = [v.encode() for v in values]
    for encoding in ("utf-8-sig", "utf-16", "utf-16-be", "utf-32", "utf-32-be"):
        encoded.extend(v.encode(encoding) for v in values[-2:])
    encoded.extend([b'\xff{}', b'\0', b'{}' + b' ' * (LIMIT - 3),
                    b'{}' + b' ' * (LIMIT - 2), b'{}' + b' ' * (LIMIT - 1)])
    return encoded


def invocation(binary, home, arguments, data, arch):
    command = ["/usr/bin/sandbox-exec", "-D", "PERSONAL=" + str(Path.home().resolve()),
               "-p", POLICY]
    if arch:
        command += ["/usr/bin/arch", "-" + arch]
    command.extend([binary, *arguments])
    env = {"CFFIXED_USER_HOME": str(home), "HOME": str(home), "PATH": "/usr/bin:/bin"}
    started = time.time()
    run = subprocess.run(command, input=data, capture_output=True, env=env, cwd=home, timeout=10)
    return run, started, time.time()


def execution(binary, home, location, data, arch, arguments=None):
    if arguments is None:
        arguments = ["--output", home / location]
    target = home / location
    previous = target.read_bytes() if target.is_file() else None
    run, started, ended = invocation(binary, home, arguments, data, arch)
    current = target.read_bytes() if target.is_file() else None
    saved = current is not None and current != previous
    shape = None
    if saved:
        view = json.loads(current)
        assert started - 0.01 <= view["fetchedAt"] <= ended + 0.01
        shape = re.sub(rb'("fetchedAt":)[-+0-9.eE]+', rb'\1CLOCK', current)
        assert stat.S_IMODE(target.stat().st_mode) == 0o600
        assert stat.S_IMODE(target.parent.stat().st_mode) == 0o700
    return run.returncode, run.stdout, run.stderr, saved, shape


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("reference", type=Path)
    ap.add_argument("candidate", type=Path)
    ap.add_argument("--arch", choices=["arm64", "x86_64"])
    a = ap.parse_args()
    # Foundation spells its temporary application support path using /tmp.
    with tempfile.TemporaryDirectory(dir="/tmp", prefix="coderim-claude-status-") as folder:
        root = Path(folder)
        modes = []
        for i, source in enumerate((a.reference, a.candidate)):
            binary = root / f"helper-{i}"
            shutil.copy2(source, binary)
            home = root / f"profile-{i}"
            (home / "Library/Application Support").mkdir(parents=True)
            modes.append((binary, home))
        for binary, home in modes:
            good = execution(binary, home, PATH, quota(window("32")).encode(), a.arch)
            assert good == (0, b"5h 68% left\n", b"", True,
                            b'{"fetchedAt":CLOCK,"fiveHour":{"usedPercentage":32}}'), good
        fixtures = documents()
        for index, data in enumerate(fixtures):
            observed = [execution(binary, home, PATH, data, a.arch) for binary, home in modes]
            assert observed[0] == observed[1], (index, data[:160], observed)
        valid = quota(window("22")).encode()
        for label, location, args in [
                ("development", str(PATH).replace("CodexMeter/", "CodexMeter-Development/"), None),
                ("wrong name", PATH.with_name("other.json"), None),
                ("other product", Path(str(PATH).replace("CodexMeter/", "AnotherApp/")), None),
                ("argument count", PATH, []), ("bad argument", PATH, ["--wrong", "/"]),
                ("relative", PATH, ["--output", PATH]),
                ("normalized", PATH, ["--output", str(PATH.parent) + "/../Claude/ClaudeLimits.json"])]:
            observed = [execution(binary, home, location, valid, a.arch, args) for binary, home in modes]
            assert observed[0] == observed[1], (label, observed)
    print(f"{len(fixtures)} stdin documents and 7 output location/argument scenarios matched.")


if __name__ == "__main__":
    main()
