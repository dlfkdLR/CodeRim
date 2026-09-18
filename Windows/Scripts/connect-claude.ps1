param([switch]$ReplaceExistingStatusLine)
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'CodeRimCLI.exe'
if (-not (Test-Path $helper)) { throw 'Run this script from the installed or extracted Windows package.' }
$root = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
New-Item -ItemType Directory -Force $root | Out-Null
$path = Join-Path $root 'settings.json'
$config = if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($config.statusLine -and -not $ReplaceExistingStatusLine) { throw 'An existing statusLine is configured. Keep it, or rerun with -ReplaceExistingStatusLine to replace it after a backup.' }
if (Test-Path $path) { Copy-Item $path ($path + '.coderim-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff') + '.bak') }
$command = '"' + $helper + '" claude-status'
$config | Add-Member -NotePropertyName statusLine -NotePropertyValue ([pscustomobject]@{type='command';command=$command}) -Force
$temp = $path + '.coderim-' + [Guid]::NewGuid().ToString('N') + '.tmp'
try { [IO.File]::WriteAllText($temp, ($config | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false))); Move-Item $temp $path -Force }
finally { if (Test-Path $temp) { Remove-Item $temp } }
Write-Host 'Connected the CodeRim status line. Restart Claude Code to apply it.'
