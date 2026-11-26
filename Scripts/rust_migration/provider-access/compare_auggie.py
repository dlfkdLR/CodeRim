"""Differential using the installed dependency checkout's original private parser in a separate type."""
import argparse
import hashlib
import itertools
import json
import platform
from pathlib import Path
import random
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--products", required=True, type=Path)
parser.add_argument("--upstream", required=True, type=Path, help="Pinned CodexBar dependency checkout")
parser.add_argument("--archive", type=Path, help="Rust accounting bridge static archive")
parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
parser.add_argument("--benchmark-output", type=Path, help="Write paired release and candidate parser timings")
args = parser.parse_args()
fixture = Path(__file__).resolve().parent
source = fixture.parents[2]
archive = (args.archive or source / "Rust/target/release/libcoderim_accounting_bridge.a").resolve(strict=True)
products = args.products.resolve()
upstream = args.upstream.resolve()
baseline = upstream / "Sources/CodexBarCore/Providers/Augment/AuggieCLIProbe.swift"
original = baseline.read_text()
method = original[original.index("    func parse("):original.index("\n}\n\n#else")]
# Preserve every parser statement, including both diagnostic sites. A noop logger receives synthetic text only.
logger = "struct DiscardedLog { func error(_ message: String) {} }\nprivate static let log = DiscardedLog()\n"
amounts = ["0", "1", "2", "1,011", "999,999,999", "9223372036854775807",
           "9,223,372,036,854,775,808", "３", "١٢", ",", "-1", "", "1.5", "1,00,0"]
dates = ["6/9/2026", "2/29/2024", "2/29/2025", "2040/2/9", "", "13/35/2027", "1/1/2038"]
fixtures = ["", "auggie login", "Authentication failed", "\u001b[32m", "Max Plan", "…", "\n"*500]
rng = random.Random(2141)
for amount, total, layout in itertools.product(amounts, amounts, ["legacy", "current", "plan"]):
    quota = {"legacy": f"{amount} remaining · {amount} / {total} credits used",
             "current": f"{amount} credits remaining         Max Plan",
             "plan": f"{amount} credits remaining"}[layout]
    monthly = f"Max Plan {total} credits" if layout == "plan" else f"{total} credits / month"
    cycle = "9 days remaining in this billing cycle (ends " + rng.choice(dates) + ")"
    for separator in ["\n", "\r\n"]:
        lines = [monthly, quota, cycle]
        if separator == "\r\n": rng.shuffle(lines)
        fixtures.append(separator.join(lines))
for _ in range(512):
    parts = [f"{rng.choice(amounts)} credits / month", f"{rng.choice(amounts)} credits remaining",
             f"{rng.choice(amounts)} remaining · {rng.choice(amounts)} / {rng.choice(amounts)} credits used",
             "billing cycle ends " + rng.choice(dates), "  ", "unrelated synthetic text"]
    fixtures.append("\n".join(rng.choice(parts) for _ in range(rng.randint(0, 8))))
# The original regexes accept more whitespace than their literal replacements
# remove. Exercise that difference and Swift's treatment of CRLF as a grapheme.
for ws in [" ", "  ", "\t", "\v", "\f", "\r", "\r\n", "\u00a0", "\u2028", " \t", "\t "]:
    for tail in [f"1,2{ws}credits{ws}/{ws}month", f"1,2{ws}credits{ws}remaining",
                 f"1,2{ws}remaining · 7{ws}/{ws}4{ws}credits used", f"billing cycle ends{ws}1/2/2026"]:
        fixtures.append("2 credits remaining\nMax Plan 8 credits\n" + tail + "\n9 credits / month")
        fixtures.append("2 credits remaining\r\nMax Plan 8 credits\r\n" + tail + "\r\n9 credits / month")
fixtures += [u + " 7 credits / month\n2 credits remaining" for u in
             ["구독", "🎁", "\ufeff", "3\u0301", "3\u200d", "३", "١٢", "²", "ㆍ", "\x00", "3,９,Ω"]]
fixtures += ["7 credits / month\n2 credits remaining " + label for label in
             ["(프로모션)", "가입:한글", "한국/지점", "공유 [限定]", "États-Unis", "📊", "숫자(１２)", "\u0301"]]
fixtures += ["," * 65000 + " credits remaining\n12 credits / month",
             "x" * 65490 + "\n2 credits remaining\n7 credits / month"]
with tempfile.TemporaryDirectory(dir="/private/tmp", prefix="coderim-auggie-quota-oracle-") as folder:
    tmp = Path(folder)
    oracle = tmp / "original.swift"
    oracle.write_text("import Foundation\nimport CodexBarCore\nstruct Oracle {\n" + logger + method + "\n}\n")
    module = tmp / "module.modulemap"
    header = upstream / "Sources/CQuickJS/include/CQuickJSHost.h"
    module.write_text(f'module CQuickJS {{ header "{header}" export * }}')
    app = tmp / "oracle-check"
    candidate = source / "Sources/CodeRim/Providers/OfficialUsageCLI.swift"
    subprocess.run(["swiftc", "-target", args.arch + "-apple-macos14.0", "-O", "-parse-as-library", "-D", "CODERIM_RUST_ACCOUNTING", "-I", products, "-I", source / "Rust/bridge/include", archive,
                    "-Xcc", "-fmodule-map-file=" + str(module), candidate, oracle, fixture / "auggie-quota-comparison.swift",
                    *[products / (name + ".o") for name in ["CodexBarCore", "Crypto", "CQuickJS", "SweetCookieKit", "Logging"]],
                    "-lsqlite3", "-lc++", "-lz", "-Xlinker", "-dead_strip", "-o", app], check=True)
    print("original parser file SHA256", hashlib.sha256(baseline.read_bytes()).hexdigest(), flush=True)
    print("candidate source SHA256", hashlib.sha256(candidate.read_bytes()).hexdigest(), flush=True)
    policy = '(version 1)(allow default)(deny network*)(deny process-exec)(allow process-exec (literal (param "APP")))(deny file-read-data (subpath (param "PERSONAL")))(deny mach-lookup (global-name "com.apple.securityd") (global-name "com.apple.securityd.xpc"))'
    for tz in ["UTC", "Asia/Seoul", "America/Los_Angeles"]:
        subprocess.run(["/usr/bin/sandbox-exec", "-D", f"APP={app}", "-D", "PERSONAL=" + str(Path.home()),
                        "-p", policy, app], input=json.dumps(fixtures).encode(), cwd=tmp,
                       env={"TZ": tz, "HOME": str(tmp), "PATH": "/usr/bin:/bin", "LC_CTYPE": "en_US.UTF-8"}, check=True)
    if args.benchmark_output:
        samples = subprocess.check_output(["/usr/bin/sandbox-exec", "-D", f"APP={app}",
            "-D", "PERSONAL=" + str(Path.home()), "-p", policy, app, "benchmark"], cwd=tmp,
            env={"TZ": "UTC", "HOME": str(tmp), "PATH": "/usr/bin:/bin", "LC_CTYPE": "en_US.UTF-8"})
        args.benchmark_output.write_bytes(samples)
