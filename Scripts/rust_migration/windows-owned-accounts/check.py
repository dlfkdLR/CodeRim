"""Exercise app startup and provider connections on a private native Windows profile."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--library', required=True, type=Path)
parser.add_argument('--rid', required=True, choices=['win-x64', 'win-arm64'])
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
if os.name != 'nt':
    parser.error('The WPF application and DPAPI fixture require native Windows.')
with tempfile.TemporaryDirectory(prefix='coderim-owned-account-qa-') as tmp:
    scratch = Path(tmp)
    out = scratch / 'app'
    library = args.library.resolve(strict=True)
    project = root / 'Windows/src/CodeRim.Windows/CodeRim.Windows.csproj'
    subprocess.run(['dotnet', 'build', project, '-c', 'Release', '-r', args.rid, '--self-contained', 'false',
                    '-m:1', '-p:PublishSingleFile=false', '-p:CodeRimRustAccounting=true',
                    '-p:CodeRimRustAccountingLibrary=' + str(library), '-o', out], check=True)
    receipt = scratch / 'result.json'
    process = subprocess.run([out / 'CodeRim.exe', '--smoke-test-owned-accounts', receipt], timeout=90)
    if process.returncode != 0 or not receipt.is_file():
        raise RuntimeError(receipt.read_text(encoding='utf-8-sig') if receipt.is_file() else
                           f'No app receipt for exit {process.returncode}.')
    report = json.loads(receipt.read_text(encoding='utf-8-sig'))
    assert report['providers'] > 40 and report['missingSentRequests'] == 0
    assert report['historicalLabelPreserved']
    assert report['officialDevice'] and report['vaultRestored'] and report['apiKey'] and report['httpRequests'] == 4
    args.output.write_text(json.dumps(dict(rid=args.rid, **report), indent=2) + '\n', encoding='utf8')
    print(f"{args.rid}: {report['providers']} provider routes, device approval, DPAPI restore and explicit API key passed")
