"""Compare WPF geometry descriptions and rendered notch masks on the same Windows runner."""
import argparse
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference-source', required=True, type=Path)
    parser.add_argument('--library', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='coderim-wpf-outline-') as temporary:
        work = Path(temporary)
        bins = {}
        for name, source in [('release', args.reference_source.resolve(strict=True)), ('candidate', ROOT)]:
            out = work / name
            cmd = ['dotnet', 'build', HERE / 'NotchOutlineProbe.csproj', '-m:1', '-c', 'Release',
                   '-p:CodeRimSource=' + str(source), '--artifacts-path', out]
            if name == 'candidate':
                cmd += ['-p:CodeRimRustAccounting=true', '-p:CodeRimRustAccountingLibrary=' + str(args.library.resolve(strict=True))]
            subprocess.run(cmd, check=True)
            bins[name] = out / 'bin/NotchOutlineProbe/release/NotchOutlineProbe.dll'
        def run(name, *args):
            return json.loads(subprocess.check_output(['dotnet',bins[name],*args],
                              env={**os.environ, 'DOTNET_TieredCompilation':'0'}))
        masks = {k:run(k) for k in bins}
        for key in ('paths', 'images', 'vectors', 'pixels'):
            if masks['release'][key] != masks['candidate'][key]:
                raise ValueError('WPF output changed: ' + json.dumps(masks))
        if masks['candidate']['cached'] < 1200 or masks['release']['cached'] != 0:
            raise ValueError('Frozen geometry was not reused at the expected entry point')
        times = []
        for mode in ('steady','animation'):
            samples = {k:[] for k in bins}
            for i in range(7):
                for k in list(bins) if i%2 == 0 else list(reversed(bins)):
                    samples[k].append(run(k,mode))
            times.append({'workload':mode, 'samples':samples, 'medians':{k:{m:statistics.median(s[m] for s in v)
                          for m in ('ns','bytes')} for k,v in samples.items()}})
        args.output.write_text(json.dumps({'architecture':platform.machine(),'scope':'WPF offscreen geometry and native WPF raster',
                              'masks':masks,'timings':times},indent=2)+'\n')
        print(masks)

if __name__ == '__main__':
    main()
