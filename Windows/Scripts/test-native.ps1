param([Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture)
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true') { throw 'Native installation tests require a disposable CI runner.' }
$exe = Join-Path $PWD "Windows/artifacts/publish/win-$Architecture/CodeRim.exe"
$capture = Join-Path $PWD 'Artifacts/windows-dashboard.png'
$process = Start-Process $exe -ArgumentList '--smoke-test', '--capture', $capture -PassThru
if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'Native smoke test timed out.' }
if ($process.ExitCode -ne 0 -or -not (Test-Path $capture)) { Get-Content (Join-Path $PWD 'Artifacts/windows-dashboard.error.txt') -ErrorAction SilentlyContinue; throw 'Native smoke test failed.' }
if ($Architecture -eq 'arm64') {
    $hostEvidence = Get-Content 'Artifacts/windows-native-host.json' -Raw | ConvertFrom-Json
    if ($hostEvidence.os_architecture -ne 'Arm64' -or $hostEvidence.process_architecture -ne 'Arm64') { throw 'ARM64 smoke must execute natively.' }
}
python Windows/tests/update_worker_regression_tests.py "./Windows/artifacts/publish/win-$Architecture/CodeRim.UpdateWorker.exe" --output Artifacts/windows-update-worker.json
if ($LASTEXITCODE -ne 0) { throw 'Unsigned update worker regression checks failed.' }
& "./Windows/artifacts/publish/win-$Architecture/CodeRimCLI.exe" version
if ($LASTEXITCODE -ne 0) { throw 'CLI startup failed.' }
python Windows/tests/cli_regression_tests.py "./Windows/artifacts/publish/win-$Architecture/CodeRimCLI.exe"
if ($LASTEXITCODE -ne 0) { throw 'CLI regression checks failed.' }
& "./Windows/artifacts/publish/win-$Architecture/install.ps1" -AddCliToPath
$cliBin = Join-Path $env:LOCALAPPDATA 'Programs\CodeRim\bin'
$env:PATH = $cliBin + ';' + $env:PATH
if ((Get-Command coderim).Source -ne (Join-Path $cliBin 'coderim.cmd')) { throw 'CLI is shadowed.' }
$text = cmd /d /c 'coderim version'
if ($LASTEXITCODE -ne 0 -or $text -notmatch 'CodeRim CLI') { throw 'Installed CLI failed.' }
