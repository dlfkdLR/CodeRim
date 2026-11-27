#!/usr/bin/env python3
"""Compare release and candidate UsageStore scheduling against isolated native defaults."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STORE = "Sources/CodeRim/App/UsageStore.swift"


def member(source, signature):
    first = source.index("    " + signature)
    end = source.index("\n    }", first) + len("\n    }")
    return source[first:end]


def governed_store(source, released):
    # Substitute only the OS notification centers and the source refresh sink.
    # Each invocation of a real scheduled Task leaves its force flag in the trace.
    settings = member(source, "func startAutomaticRefresh()").replace(
        "NSWorkspace.shared.notificationCenter", "wakeCenter"
    ).replace("NotificationCenter.default", "defaultsCenter").replace(
        "Task.sleep(for: .seconds(5))", "Task.sleep(for: .seconds(3_600))"
    )
    # Acknowledge each notification after the complete product handler returns.
    settings = settings.replace("                if self?.currentRefresh",
        "                defer { self?.wakeReceived += 1 }\n                if self?.currentRefresh")
    settings = settings.replace("                let weekStart = self.storedWeekStartRawValue",
        "                defer { self.defaultsReceived += 1 }\n                let weekStart = self.storedWeekStartRawValue")
    selectors = ["private var storedWeekStartRawValue", "private func refreshIfScheduled("]
    selectors += (["private var currentRefreshMode:", "private func updateWatcherForRefreshMode()"]
                  if released else ["private var currentRefreshPolicy:", "private func updateWatcher(for "])
    selected = [member(source, s) for s in selectors]
    fields = (['refreshPending','pendingForceAnalytics','previousRefreshModeRawValue'] if released else
              ['refreshRequests','previousRefreshPolicy'])
    fields += ["previousWeekStartRawValue"]
    declarations = [next(l for l in source.splitlines() if "private var " + f + " " in l)
                    for f in fields]
    refresh = member(source,"func refresh(forceAnalytics:")
    preflight = refresh[refresh.index("{") + 1:refresh.index("        isRefreshing = true")]
    completion = ("let followUp = refreshPending\n        refreshPending = false" if released
                  else "let followUp = refreshRequests.takePending()")
    # The admitted request returns its force decision, with database work driven separately.
    requests = "func admission(_ forceAnalytics: Bool) -> Bool? {" + preflight.replace("return\n","return nil\n").replace(
        "else { return }","else { return nil }") + "        return forceAnalytics\n    }"
    requests += "\n    func consumePending() -> Bool {\n        " + completion + "\n        return followUp\n    }"
    mode = "currentRefreshMode" if released else "currentRefreshPolicy"
    current = ("func preferenceCost() -> Int { let mode = " + mode
               + "; return week() + Int(mode.pollingInterval ?? 0) + (mode.usesFileEvents ? 1 : 0) }")
    return "\n".join(declarations + selected + [settings, requests, current])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--report", type=Path, help="Save traces and run seven rotated preference lookup measurements")
    args = parser.parse_args()
    original = subprocess.check_output(["git", "-C", ROOT, "show", "v2.1.41:" + STORE], text=True)
    current = (ROOT / STORE).read_text()
    fixture = (HERE / "fixture.swift").read_text()
    with tempfile.TemporaryDirectory(prefix="coderim-usage-refresh-") as temporary:
        tmp = Path(temporary)
        executables = {}
        for name, source in [("release", original), ("swift", current), ("rust", current)]:
            extracted = fixture.replace("/* STORE */", governed_store(source, name == "release"))
            enum_source = source[source.index("enum RefreshMode:"):]
            program = tmp / (name + ".swift")
            program.write_text(extracted + "\n" + enum_source)
            files = [] if name == "release" else [ROOT / "Sources/CodeRim/App/UsageRefreshPolicy.swift"]
            flags = ["-D","CODERIM_RUST_ACCOUNTING","-I",ROOT/"Rust/bridge/include",
                     args.archive.resolve(),"-framework","CoreGraphics","-lsqlite3"] if name == "rust" else []
            exe = tmp / name
            subprocess.run(["swiftc","-O","-swift-version","6","-parse-as-library","-target",
                            args.arch + "-apple-macosx14.0",program,*files,*flags,"-o",exe],check=True)
            executables[name] = exe
        def run(name, *options):
            return subprocess.check_output(["sandbox-exec","-p","(version 1)(allow default)(deny network*)",
                                            executables[name],*options])
        records = {k:run(k) for k in executables}
        for key, data in records.items():
            if args.report:
                (args.report.parent / (args.report.stem+"-"+key+".txt.gz")).write_bytes(gzip.compress(data,mtime=0))
            if records["release"] != data:
                original_lines = records["release"].splitlines()
                print([(i,l,r) for i,(l,r) in enumerate(zip(original_lines,data.splitlines())) if l!=r][:10])
                raise AssertionError(key + " differs from release")
        if args.report is None:
            print(args.arch, len(records["rust"].splitlines()), hashlib.sha256(records["rust"]).hexdigest())
            return
        bench = {k:[] for k in executables}
        settings = {k:[] for k in executables}
        checksums=set()
        for i in range(7):
            order=list(executables)
            for key in order[i%3:]+order[:i%3]:
                checksum,cost,mixed=run(key,"bench").split()
                checksums.add(checksum)
                bench[key].append(float(cost))
                settings[key].append(float(mixed))
        assert len(checksums)==1
        lines=records["rust"].decode().splitlines()
        result={"arch":args.arch,"records":len(lines),"sha256":hashlib.sha256(records["rust"]).hexdigest(),
                "counts":{kind:sum(row.startswith(kind+" ") for row in lines)
                          for kind in ["mode","week","poll","queue","wake","change"]},
                "defaultsLookupNs":bench,"medians":{k:statistics.median(v) for k,v in bench.items()},
                "combinedSettingsNs":settings,"combinedMedians":{k:statistics.median(v) for k,v in settings.items()},
                "checksum":checksums.pop().decode()}
        args.report.write_text(json.dumps(result, indent=2)+"\n")
        print(json.dumps(result["counts"]),result["sha256"])


if __name__ == "__main__":
    main()
