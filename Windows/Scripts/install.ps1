param([switch]$AddCliToPath, [switch]$Launch)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'CodeRim.exe'))) { throw 'Run install.ps1 from the extracted Windows package.' }
$destination = Join-Path $env:LOCALAPPDATA 'Programs\CodeRim'
if (Get-Process CodeRim -ErrorAction SilentlyContinue) { throw 'Quit CodeRim from its tray menu before installing.' }
New-Item -ItemType Directory -Force $destination | Out-Null
Get-ChildItem $source -File | Copy-Item -Destination $destination -Force
if (Test-Path (Join-Path $source 'ThirdParty')) { Copy-Item (Join-Path $source 'ThirdParty') $destination -Recurse -Force }
$cliDirectory = Join-Path $destination 'bin'
New-Item -ItemType Directory -Force $cliDirectory | Out-Null
Copy-Item (Join-Path $source 'bin\coderim.cmd') $cliDirectory -Force
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
