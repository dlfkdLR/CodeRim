param([ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64')
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $windowsRoot
$version = & (Join-Path $PSScriptRoot 'package.ps1') -CheckVersionOnly
$arch = $RuntimeIdentifier.Replace('win-','')
$publish = Join-Path $windowsRoot "artifacts\publish\$RuntimeIdentifier"
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'CodeRim.exe')).FileVersion -cne "$version.0") { throw 'Publish the matching application before building an MSI.' }
$bootstrapPath = Join-Path $publish 'CodeRim.bootstrap.json'
if (-not (Test-Path $bootstrapPath)) { throw 'MSI packaging requires the public build with a pinned installer worker; keep Required builds on the signed ZIP channel.' }
$bootstrap = Get-Content $bootstrapPath -Raw | ConvertFrom-Json
if ($bootstrap.version -cne $version -or
    $bootstrap.workerSha256 -cne (Get-FileHash (Join-Path $publish 'CodeRim.UpdateWorker.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -or
    $bootstrap.guiSha256 -cne (Get-FileHash (Join-Path $publish 'CodeRim.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'The MSI bootstrap identity is stale or modified.' }
$tools = Join-Path $windowsRoot 'artifacts\wix-5.0.2'
if (-not (Test-Path (Join-Path $tools 'wix.exe'))) {
    dotnet tool install wix --version 5.0.2 --allow-roll-forward --tool-path $tools
    if ($LASTEXITCODE -ne 0) { throw 'WiX installation failed.' }
}
$marker = @{product='CodeRim.Windows';version=$version;architecture=$arch;format='msi'} | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $publish 'CodeRim.install.json'),$marker,[Text.UTF8Encoding]::new($false))
$payload = Join-Path $windowsRoot "artifacts\payload-$arch.wxs"
python (Join-Path $PSScriptRoot 'generate_msi_payload.py') $publish $payload
if ($LASTEXITCODE -ne 0) { throw 'MSI payload generation failed.' }
$code = python -c 'import uuid,sys;print(uuid.uuid5(uuid.UUID("ab0d9f06-26bd-4cae-9d30-de386b872e81"),sys.argv[1]+"-"+sys.argv[2]))' $version $arch
if ($LASTEXITCODE -ne 0) { throw 'Product identity generation failed.' }
$installer = Join-Path $repo "Artifacts\CodeRim-Windows-$version-$arch-Setup.msi"
& (Join-Path $tools 'wix.exe') build (Join-Path $windowsRoot 'Installer\Package.wxs') $payload -arch $arch -d "Version=$version" -d "ProductCode=$code" -d "IconFile=$windowsRoot\src\CodeRim.Windows\Assets\CodeRim.ico" -o $installer
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
$hash=(Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$hash  $([IO.Path]::GetFileName($installer))`n",[Text.UTF8Encoding]::new($false))
Write-Output $installer
