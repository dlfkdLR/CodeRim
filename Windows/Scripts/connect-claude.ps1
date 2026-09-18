param([switch]$ReplaceExistingStatusLine)
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'CodeRimCLI.exe'
if (-not (Test-Path $helper)) { throw 'Run this script from the installed or extracted Windows package.' }
$arguments = @('claude-connect')
if ($ReplaceExistingStatusLine) { $arguments += '--replace-statusline' }
& $helper @arguments
if ($LASTEXITCODE -ne 0) { throw 'Claude connection was not changed. Check the message above.' }
