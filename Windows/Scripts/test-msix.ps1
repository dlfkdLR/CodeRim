param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][ValidateSet("x64", "arm64")][string]$Architecture
)

# Installs the Store package the way Windows will after Microsoft signs it, then checks that the app,
# both CLI aliases and the packaged-only behaviour work. The throwaway certificate exists only on the
# CI machine; the published package is signed by Microsoft, never by this certificate.
$ErrorActionPreference = "Stop"
$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$identity = Get-Content (Join-Path $windowsRoot "Installer\Store\store-identity.json") -Raw | ConvertFrom-Json
$evidence = Join-Path $projectRoot "Artifacts\msix-$Architecture"
New-Item -ItemType Directory -Force $evidence | Out-Null
$results = New-Object 'System.Collections.Generic.List[string]'
# Runs a command with a hard limit; a hung step must fail the job with a reason, not stall it.
function Invoke-Bounded([string]$File, [string[]]$Arguments, [int]$Seconds, [string]$Name) {
    $out = Join-Path $evidence "$Name.out.txt"; $err = Join-Path $evidence "$Name.err.txt"
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    # Windows PowerShell only records ExitCode for a process whose handle was opened before it exited.
    $null = $process.Handle
    if (-not $process.WaitForExit($Seconds * 1000)) {
        try { $process.Kill() } catch { }
        Get-Content $out, $err -ErrorAction SilentlyContinue | Write-Host
        throw "$Name did not finish within $Seconds seconds."
    }
    $text = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
    if ($process.ExitCode -ne 0) { Get-Content $err -ErrorAction SilentlyContinue | Write-Host; throw "$Name failed with exit code $($process.ExitCode): $text" }
    return $text
}

$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $identity.Publisher -CertStoreLocation Cert:\CurrentUser\My `
    -KeyUsage DigitalSignature -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
try {
    $cer = Join-Path $evidence "test.cer"
    Export-Certificate -Cert $certificate -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $signed = Join-Path $evidence (Split-Path $Package -Leaf)
    Copy-Item $Package $signed -Force
    $signTool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    & $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $signed
    if ($LASTEXITCODE -ne 0) { throw "Test signing failed." }

    # Hosted Windows Server images do not allow sideloading by default, and Add-AppxPackage then waits
    # without an error. Allow trusted packages (CI machine only) and bound the install.
    $unlock = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    New-Item -Path $unlock -Force | Out-Null
    Set-ItemProperty -Path $unlock -Name AllowAllTrustedApps -Value 1 -Type DWord
    Set-ItemProperty -Path $unlock -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    try { Invoke-Bounded $powershell @('-NoProfile', '-Command', "Add-AppxPackage -Path '$signed' -ForceUpdateFromAnyVersion") 300 'add-appx' | Out-Null }
    catch {
        Get-WinEvent -LogName 'Microsoft-Windows-AppXDeploymentServer/Operational' -MaxEvents 40 -ErrorAction SilentlyContinue |
            Format-List TimeCreated, Id, Message | Out-String | Write-Host
        throw
    }
    $installed = Get-AppxPackage -Name $identity.IdentityName
    if (-not $installed) { throw "The package did not install." }
    $results.Add("installs-per-user-without-administrator")
    $aliases = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps"
    foreach ($alias in "coderim.exe", "CodeRimCLI.exe", "CodeRim.exe") {
        if (-not (Test-Path (Join-Path $aliases $alias))) { throw "Execution alias $alias is missing." }
    }
    $results.Add("cli-and-app-aliases-registered")

    $version = Invoke-Bounded (Join-Path $aliases "coderim.exe") @('version') 60 'alias-version'
    if ($version -notmatch "CodeRim CLI") { throw "The coderim alias did not run the CLI: $version" }
    $results.Add("coderim-alias-runs-cli")
    $python = (Get-Command python).Source
    Invoke-Bounded $python @((Join-Path $windowsRoot "tests\cli_regression_tests.py"), (Join-Path $aliases "CodeRimCLI.exe")) 600 'alias-cli-regressions' | Out-Null
    $results.Add("cli-regressions-through-alias")

    # The native UI smoke inside the package: same checks as the MSI build, with package identity.
    $capture = Join-Path $evidence "windows-msix-dashboard.png"
    $smoke = Start-Process (Join-Path $aliases "CodeRim.exe") -ArgumentList "--smoke-test", "--capture", ('"' + $capture + '"') -PassThru
    $null = $smoke.Handle
    if (-not $smoke.WaitForExit(240000)) { $smoke.Kill(); throw "Packaged smoke test timed out." }
    if ($smoke.ExitCode -ne 0 -or -not (Test-Path $capture)) {
        Get-Content ([IO.Path]::ChangeExtension($capture, ".error.txt")) -ErrorAction SilentlyContinue
        throw "Packaged native smoke test failed."
    }
    $results.Add("packaged-native-ui-smoke")

    Invoke-Bounded $powershell @('-NoProfile', '-Command', "Remove-AppxPackage -Package '$($installed.PackageFullName)'") 300 'remove-appx' | Out-Null
    if (Get-AppxPackage -Name $identity.IdentityName) { throw "The package did not uninstall." }
    if (Test-Path (Join-Path $aliases "coderim.exe")) { throw "Uninstall left the coderim alias." }
    $results.Add("uninstall-removes-app-and-aliases")
}
finally {
    Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint | Remove-Item -ErrorAction SilentlyContinue
    Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    $results | ConvertTo-Json | Set-Content (Join-Path $evidence "msix-checks.json")
}
$results
