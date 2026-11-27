#!/usr/bin/env python3
"""Exercise original MobileAppModel registration methods with a local scripted relay and virtual sleeps."""
from pathlib import Path
import argparse
import hashlib
import json
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
MODEL = "iOS/CodeRimMobile/MobileAppModel.swift"

def method(source, signature, *, static=False):
    prefix = "    private static func " if static else "    private func "
    start = source.index(prefix + signature)
    end = source.index("\n    }", start) + len("\n    }")
    return source[start:end].replace("private func ", "func ", 1)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", choices=["arm64","x86_64"], default="arm64")
    parser.add_argument("--benchmark", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-registration-") as temporary:
        root = Path(temporary)
        binaries = {}
        for name in ["release","native","rust"]:
            source = subprocess.check_output(["git","-C",ROOT,"show","v2.1.41:"+MODEL],text=True) if name=="release" else (ROOT/MODEL).read_text()
            methods = "\n".join([method(source,"registerStarter("),method(source,"register(")])
            if name != "release":
                methods += "\n" + method(source, "pushTokenText(", static=True)
            methods = methods.replace("Activity<CodeRimActivityAttributes>", "FixtureActivity")
            methods = methods.replace("Task.sleep(for: .seconds(delay))", "FixtureClock.sleep(delay)")
            policy = ""
            if name == "rust":
                start = source.index("@MainActor\nenum MobilePushRegistration")
                end = source.index("\n#endif",start)
                policy = source[start:end].replace("Task.sleep(for: .seconds($0))", "FixtureClock.sleep($0)")
            swift = root/(name+".swift")
            swift.write_text((HERE/"fixture.swift").read_text().replace("/* product methods */",methods).replace("/* policy */",policy))
            flags = (["-D","CODERIM_RUST_MOBILE_SELECTION","-D","CODERIM_RUST_MOBILE_DIGEST","-I",ROOT/"Rust/bridge/include",
                      args.archive.resolve(),"-framework","CoreGraphics","-lsqlite3"] if name=="rust" else [])
            exe = root/name
            subprocess.run(["swiftc","-O","-swift-version","6","-parse-as-library","-target",args.arch+"-apple-macosx14.0",swift,*flags,"-o",exe],check=True)
            binaries[name] = exe
        def run(name,*commands):
            return subprocess.check_output(["sandbox-exec","-p","(version 1)(allow default)(deny network*)",binaries[name],*commands])
        records={k:run(k) for k in binaries}
        assert records["release"]==records["native"]==records["rust"]
        print(args.arch,len(records["release"].splitlines()),hashlib.sha256(records["release"]).hexdigest())
        if args.benchmark:
            samples={k:[] for k in binaries}; checksums=set()
            for i in range(9):
                modes=list(binaries)
                for key in modes[i%3:]+modes[:i%3]:
                    checksum,ns,count=map(int,run(key,"bench").split())
                    samples[key].append(ns/count);checksums.add(checksum)
            assert len(checksums)==1
            args.benchmark.write_text(json.dumps({"nsPerRegistration":samples,"medians":{k:statistics.median(v) for k,v in samples.items()},"checksum":checksums.pop()},indent=2)+"\n")
if __name__=="__main__":
    main()
