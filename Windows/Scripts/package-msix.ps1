param(
    [ValidateSet("win-x64", "win-arm64")]
    [string[]]$RuntimeIdentifier = @("win-x64", "win-arm64"),
    # Partner Center's Product identity values; the repository file holds local test values until then.
    [string]$IdentityFile
)

# Builds the Microsoft Store package from the already published app (package.ps1). The output is unsigned:
# Partner Center signs it with Microsoft's certificate after certification, which is what removes the
# "unknown publisher" warning at no cost. test-msix.ps1 signs a copy with a throwaway certificate for CI only.
$ErrorActionPreference = "Stop"
$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$storeRoot = Join-Path $windowsRoot "Installer\Store"
if ([string]::IsNullOrWhiteSpace($IdentityFile)) { $IdentityFile = Join-Path $storeRoot "store-identity.json" }
$identity = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
foreach ($field in "IdentityName", "Publisher", "PublisherDisplayName") {
    if ([string]::IsNullOrWhiteSpace($identity.$field)) { throw "store identity is missing $field" }
}
$version = & (Join-Path $PSScriptRoot "package.ps1") -CheckVersionOnly
$packageVersion = ([regex]::Match($version, '^[0-9]+\.[0-9]+\.[0-9]+').Value) + ".0"

$kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$makeAppx = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw "makeappx.exe from the Windows SDK was not found." }

$artifacts = Join-Path $projectRoot "Artifacts"
New-Item -ItemType Directory -Force $artifacts | Out-Null
$packages = @()
foreach ($rid in $RuntimeIdentifier) {
    $architecture = $rid.Replace("win-", "")
    $publish = Join-Path $windowsRoot "artifacts\publish\$rid"
    if (-not (Test-Path (Join-Path $publish "CodeRim.exe"))) { throw "Publish $rid with package.ps1 first." }
    $layout = Join-Path $windowsRoot "artifacts\msix\$rid"
    if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
    New-Item -ItemType Directory -Force $layout | Out-Null
    # The Store owns installation and updates: leave out the MSI updater and the ZIP installer scripts.
    $excluded = @("CodeRim.UpdateWorker.exe", "CodeRim.UpdateWorker.dll", "install.ps1", "uninstall.ps1", ".coderim-install.json")
    Get-ChildItem $publish -Force | Where-Object { $excluded -notcontains $_.Name -and $_.Name -ne "bin" } |
        Copy-Item -Destination $layout -Recurse -Force
    Copy-Item (Join-Path $storeRoot "Assets") -Destination (Join-Path $layout "Assets") -Recurse -Force
    $manifest = (Get-Content (Join-Path $storeRoot "AppxManifest.template.xml") -Raw).
        Replace('$IdentityName$', [Security.SecurityElement]::Escape($identity.IdentityName)).
        Replace('$Publisher$', [Security.SecurityElement]::Escape($identity.Publisher)).
        Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
        Replace('$Version$', $packageVersion).Replace('$Architecture$', $architecture)
    if ($manifest -match '\$[A-Za-z]+\$') { throw "Unfilled manifest token: $($Matches[0])" }
    [IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))
    $output = Join-Path $artifacts "CodeRim-Windows-$version-$architecture.msix"
    & $makeAppx.FullName pack /o /d $layout /p $output /nv
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $rid" }
    $packages += $output
}
if ($packages.Count -gt 1) {
    # The upload Partner Center takes for both architectures at once.
    $bundleInput = Join-Path $windowsRoot "artifacts\msix\bundle"
    if (Test-Path $bundleInput) { Remove-Item -Recurse -Force $bundleInput }
    New-Item -ItemType Directory -Force $bundleInput | Out-Null
    $packages | Copy-Item -Destination $bundleInput
    $bundle = Join-Path $artifacts "CodeRim-Windows-$version.msixbundle"
    & $makeAppx.FullName bundle /o /d $bundleInput /p $bundle /bv $packageVersion
    if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed" }
    $packages += $bundle
}
$packages
