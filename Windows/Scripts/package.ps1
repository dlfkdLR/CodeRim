param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version,
    [switch]$ResetManifest
)

$ErrorActionPreference = "Stop"
$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$projectPath = Join-Path $windowsRoot "src\CodeRim.Windows\CodeRim.Windows.csproj"
$project = [xml](Get-Content $projectPath)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $project.Project.PropertyGroup.Version
}
$publishRoot = Join-Path $windowsRoot "artifacts\publish\$RuntimeIdentifier"
$artifactRoot = Join-Path $projectRoot "Artifacts"
$architecture = $RuntimeIdentifier.Replace("win-", "")
$archiveName = "CodeRim-Windows-$Version-$architecture.zip"
$archivePath = Join-Path $artifactRoot $archiveName
$checksumPath = "$archivePath.sha256"
$manifestPath = Join-Path $artifactRoot "SHA256SUMS-windows.txt"

if ($Version -notmatch '^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') {
    throw "Unsupported version: $Version"
}

if (Test-Path $publishRoot) {
    Remove-Item -Recurse -Force $publishRoot
}
New-Item -ItemType Directory -Force -Path $publishRoot, $artifactRoot | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publishRoot `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
dotnet publish (Join-Path $windowsRoot "src\CodeRim.CLI\CodeRim.CLI.csproj") --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $publishRoot -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'connect-claude.ps1') $publishRoot
New-Item -ItemType Directory -Force (Join-Path $publishRoot 'bin') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'coderim.cmd') (Join-Path $publishRoot 'bin')
Copy-Item (Join-Path $windowsRoot 'ThirdParty') $publishRoot -Recurse
Copy-Item (Join-Path $projectRoot 'Documentation\WINDOWS.md') (Join-Path $publishRoot 'README.md')
$executablePath = Join-Path $publishRoot "CodeRim.exe"
if (-not (Test-Path $executablePath)) {
    throw "Published executable is missing: $executablePath"
}

if (Test-Path $archivePath) {
    Remove-Item -Force $archivePath
}
Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archivePath

$hash = (Get-FileHash -Algorithm SHA256 $archivePath).Hash.ToLowerInvariant()
$checksumLine = "$hash  $archiveName"
$ascii = [System.Text.Encoding]::ASCII
[System.IO.File]::WriteAllText($checksumPath, "$checksumLine`n", $ascii)
if ($ResetManifest -and (Test-Path $manifestPath)) {
    Remove-Item -Force $manifestPath
}
[System.IO.File]::AppendAllText($manifestPath, "$checksumLine`n", $ascii)

Write-Host "Packaged $archivePath"
Write-Host "SHA-256 $hash"
