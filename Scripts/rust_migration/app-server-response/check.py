"""Compare last selected Codex response and the child-completion condition with v2.1.41."""
import argparse
import base64
import json
from pathlib import Path
import platform
import random
import subprocess
import tempfile

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[2]
def source(file, original):
    name="Sources/CodeRim/Limits/"+file+".swift"
    return subprocess.check_output(["git","-C",ROOT,"show","v2.1.41:"+name],text=True) if original else (ROOT/name).read_text()

def fixtures():
    suffix=b',"result":{"rateLimits":{"primary":{"usedPercent":24.5,"windowDurationMins":300,"resetsAt":1893456000}}}}'
    good=b'{"id":2'+suffix
    failure=b'{"id":2,"error":{"message":"synthetic denial"}}'
    simple=[b"",b"x",b"null",b"[]",b"{}",good,failure,b'{"id":2}',b'{"id":{}}',b'{"params":{"id":2}}',
            b'{"id":2,"id":3'+suffix,b'{"id":3,"id":2'+suffix,b'{"\\u0069d":2'+suffix,
            b'{"i\\u0064":2'+suffix,b'{"\xef\xbb\xbfid":2'+suffix,
            b'{"id":2,"bad":"\xff"'+suffix,b'{"id":2,"large":1e900'+suffix,
            b'{"id":2,"value":"\\ud800"'+suffix,good.decode().encode("utf-16"),b'\xef\xbb\xbf'+good]
    for token in [b'null',b'true',b'false',b'"2"',b'-2',b'0',b'-0',b'1',b'2',b'3',b'1.99',b'2.01',
                  b'2.75',b'2.0',b'2e0',b'20e-1',b'2.999999999999999999',b'1e-400',b'1e400',
                  b'18446744073709551618',b'{}',b'[]',b'{"id":2}',b'""',b'NaN',b'02']:
        simple.append(b'{"id":'+token+suffix)
    rng=random.Random(23582)
    values=list(simple)
    for a in simple:
        for b in [b"",failure,good,b'{"id":2.7'+suffix,b'{"id":1}']:
            values.extend([a+b'\n'+b,b+b'\r\n'+a+b'\n'])
    for _ in range(1400):
        values.append(b'\n'.join(rng.choices(simple,k=rng.randint(1,13))))
    for n in (0,1,31,128,1024,8192):
        notices=[b'{"method":"progress","params":{"index":'+str(i).encode()+b'}}' for i in range(n)]
        values.extend([b'\n'.join([*notices,good]),b'\n'.join([good,*notices]),b'\n'.join(notices)])
    values.extend([b' '*(2_097_152-len(good))+good,b'x'*2_097_153])
    return values

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--archive",type=Path,required=True)
    ap.add_argument("--arch",default=platform.machine())
    args=ap.parse_args()
    with tempfile.TemporaryDirectory() as folder:
        work=Path(folder)
        defs=['import Foundation','import CCodeRimAccounting','enum AppServerLimitProvider { static let maximumResponseBytes=2_097_152 }']
        for original,title in [(True,"OriginalGate"),(False,"ProductGate")]:
            raw=source("AppServerLimitProvider",original)
            method=raw[raw.index("    private func containsResponseID2"):].split("\n}",1)[0].replace("private func containsResponseID2","func containsResponseID2")
            defs.append("struct "+title+" {\n"+method+"\n}")
        old=source("AccountLimitProvider",True)
        defs.append("struct OriginalParser:"+old.split("struct AccountLimitsResponseParser:",1)[1])
        test=work/"Sources.swift";test.write_text("\n".join(defs))
        app=work/"test"
        subprocess.run(["swiftc","-target",args.arch+"-apple-macos14.0","-O","-whole-module-optimization","-swift-version","6",
                        "-warnings-as-errors","-Xlinker","-fatal_warnings","-D","CODERIM_RUST_ACCOUNTING","-I",ROOT/"Rust/bridge/include",
                        args.archive,test,ROOT/"Sources/CodeRim/Limits/AccountLimitsModels.swift",
                        ROOT/"Sources/CodeRim/Limits/AccountLimitProvider.swift",HERE/"main.swift","-o",app],check=True)
        input=json.dumps([base64.b64encode(v).decode() for v in fixtures()]).encode()
        out=subprocess.check_output([app],input=input)
        print(args.arch,out.decode().strip())

if __name__=="__main__":
    main()
