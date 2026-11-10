# Normalization language trial

**English** · [한국어](README.ko.md)

Decision: keep the production Swift and C# implementations. Rust matched both normalization contracts through real Swift C imports and C# P/Invoke calls, including Windows repaired-zero evidence. Its isolated arithmetic was faster, but adding a third production toolchain and two binding layers is not justified by this synthetic parse-pipeline result. No application target depends on this experiment.

## Reproduce

On macOS with Swift, Rust and .NET 10 installed, run from the repository root:

```sh
python3 Experiments/Normalization/run.py
```

The runner builds into `.build/normalization-trial`, runs the Rust unit check and all 13 shared scenarios with 31 state transitions through each actual binding, then checks benchmark checksums. The C layout is checked in Rust, Swift and C#. There are no added Cargo or NuGet dependencies. The Swift driver compiles the production value types, parser and normalizer independently of the app, database and provider frameworks. The C# project links the corresponding production source files.

Each process runs four passes, discarding the first timing sample. Normalization has 1,000,000 calls per pass; JSON parsing plus normalization has 20,000. .NET tiered compilation is disabled for these processes so late JIT promotion does not change the apparent pipeline speed. The workload remains synthetic, single-threaded and resident in memory; it does not include files, SQLite, UI, network or native Windows execution.

## Recorded result

The [baseline](baseline-macos-arm64.json) uses the original C# reference-record state and result. The [final result](results-macos-arm64.json) uses readonly value records. Reports contain toolchains, input hashes, all timing samples and process peak memory. The baseline can be reconstructed by changing the two value-record declarations back to `sealed record`, removing `in` from the state parameter, and restoring its null check before running the trial. Restore the production source afterward.

| Final paired run | Native normalization | Rust through binding | Native parse pipeline | Rust parse pipeline |
| --- | --- | --- | --- | --- |
| Swift | 26.94 ns/call | 6.87 ns/call | 5,710 ns/row | 5,786 ns/row |
| C# | 39.57 ns/call | 8.82 ns/call | 2,479 ns/row | 2,365 ns/row |

In this run, the parse pipeline was about 1.3% slower with Rust in Swift and 4.6% faster in C#. This is not a statistical confidence interval or an application speed claim. Separate native/Rust process peaks were approximately 62.5 MB for Swift and 63.3 MB for C#, with no meaningful memory advantage established for Rust. Peak RSS includes the runtime, fixtures, parsers and preflight checks; it is not a measurement of the arithmetic function's heap.

The C# value-record change removes state/result heap allocation: the baseline measured 180,903,272 managed bytes per million normalization calls, while the final run measured 40 bytes, attributable to the benchmark timer. A regression test separately checks zero allocations inside the warmed normalization loop. This is an allocation improvement, not a promise of faster normalization; the recorded reference-record arithmetic was faster in isolation.

## Maintenance decision

Keep the dependency-free Rust trial as reproducible evidence, outside production packaging. Continue sharing behavior fixtures and keeping platform work at existing boundaries. A future Rust proposal needs a demonstrated end-to-end bottleneck or enough eliminated duplicated maintenance to justify ABI ownership, release packaging and native x64/ARM64 checks. This trial does not establish those benefits.
