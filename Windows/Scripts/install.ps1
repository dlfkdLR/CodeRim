param([switch]$AddCliToPath, [switch]$Launch, [string]$RecoverOperationId)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'CodeRim.exe'))) { throw 'Run install.ps1 from the extracted Windows package.' }
$destination = Join-Path $env:LOCALAPPDATA 'Programs\CodeRim'
if (Get-Process CodeRim -ErrorAction SilentlyContinue) { throw 'Quit CodeRim from its tray menu before installing.' }
$signedPackage = $false # The Required signing pipeline changes this literal before signing the script.
if ($signedPackage) {
    $destination = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CodeRim'
    # This is an explicit manual installer launch. The worker independently checks its compiled publisher pin and signed payload.
    $worker = Join-Path $source 'CodeRim.UpdateWorker.exe'
    $scriptSignature = Get-AuthenticodeSignature -LiteralPath $PSCommandPath
    $workerSignature = Get-AuthenticodeSignature -LiteralPath $worker
    if ($scriptSignature.Status -ne 'Valid' -or $workerSignature.Status -ne 'Valid' -or
        [Convert]::ToBase64String($scriptSignature.SignerCertificate.RawData) -cne [Convert]::ToBase64String($workerSignature.SignerCertificate.RawData)) {
        throw 'The signed installer and update worker must have the same verified publisher certificate.'
    }
    if ($RecoverOperationId) {
        if ($RecoverOperationId -cnotmatch '^[0-9a-f]{32}$') { throw 'Recovery requires the exact operation ID from the previous attempt.' }
        $result = (& $worker --bootstrap-recover-install $RecoverOperationId | Out-String | ConvertFrom-Json)
    } else { $result = (& $worker --bootstrap-install $source | Out-String | ConvertFrom-Json) }
    if ($LASTEXITCODE -ne 0 -or $null -eq $result -or $result.Status -notin @(0, 1)) {
        if ($result.OperationId -cmatch '^[0-9a-f]{32}$') { Write-Warning ('Preserved operation: ' + $result.OperationId + '. Use this signed installer with -RecoverOperationId to recover it.') }
        throw 'The signed binary installation was not committed. Existing files are preserved; inspect the updater result before retrying.'
    }
    $cliDirectory = Join-Path $destination 'bin'
} else {
    if ($RecoverOperationId) { throw 'Recovery requires the signed package that owns the preserved installation.' }
    if (Test-Path -LiteralPath (Join-Path $destination '.coderim-install.json')) {
        throw 'This is a managed signed installation. Use CodeRim Information to update/recover it, or repair with the matching signed package; an unsigned installer cannot overwrite its manifest.'
    }
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem $source -File | Copy-Item -Destination $destination -Force
    if (Test-Path (Join-Path $source 'ThirdParty')) { Copy-Item (Join-Path $source 'ThirdParty') $destination -Recurse -Force }
    $cliDirectory = Join-Path $destination 'bin'
    New-Item -ItemType Directory -Force $cliDirectory | Out-Null
    Copy-Item (Join-Path $source 'bin\coderim.cmd') $cliDirectory -Force
}
$shortcut = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\CodeRim.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $destination 'CodeRim.exe'
$link.WorkingDirectory = $destination
$link.IconLocation = Join-Path $destination 'CodeRim.exe'
$link.Save()
if ($AddCliToPath) {
    $current = [string][Environment]::GetEnvironmentVariable('Path', 'User')
    # Remove the legacy GUI directory so CodeRim.exe cannot shadow coderim.cmd.
    $entries = @($current -split ';' | Where-Object { $_ -and $_.TrimEnd('\') -ine $destination.TrimEnd('\') -and $_.TrimEnd('\') -ine $cliDirectory.TrimEnd('\') })
    [Environment]::SetEnvironmentVariable('Path', (($entries + $cliDirectory) -join ';'), 'User')
    Write-Host 'Open a new terminal to use coderim.'
}
Write-Host "Installed CodeRim in $destination. User data remains in $env:LOCALAPPDATA\CodeRim."
if ($Launch) { Start-Process (Join-Path $destination 'CodeRim.exe') }
