param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version,
    [switch]$ResetManifest,
    [ValidateSet("Unsigned", "Required")][string]$SigningMode = "Unsigned",
    [string]$SigningCertificateThumbprint,
    [string]$PublisherSpkiSha256,
    [string]$SignToolPath,
    [uri]$TimestampServer = 'https://timestamp.digicert.com',
    [switch]$CheckVersionOnly
)

$ErrorActionPreference = "Stop"
$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$projectPath = Join-Path $windowsRoot "src\CodeRim.Windows\CodeRim.Windows.csproj"
$project = [xml](Get-Content $projectPath)
# Validate declarations before deleting a publish directory or reading a signing certificate.
$projectVersion = [string]$project.Project.PropertyGroup.Version
$sharedProps = [xml](Get-Content (Join-Path $windowsRoot 'Directory.Build.props') -Raw)
$applicationManifest = [xml](Get-Content (Join-Path $windowsRoot 'src\CodeRim.Windows\app.manifest') -Raw)
$releaseVersions = @(Get-Content (Join-Path $projectRoot 'Config\Release.env') | ForEach-Object {
    if ($_ -match '^MARKETING_VERSION=(.*)$') { $Matches[1] }
})
if ($projectVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$' -or
    [string]$sharedProps.Project.PropertyGroup.Version -cne $projectVersion -or
    $releaseVersions.Count -ne 1 -or $releaseVersions[0] -cne $projectVersion) {
    throw 'Release version declarations disagree: check Release.env, Directory.Build.props and the application project.'
}
$binaryVersion = [regex]::Match($projectVersion, '^[0-9]+\.[0-9]+\.[0-9]+').Value + '.0'
if ([string]$project.Project.PropertyGroup.AssemblyVersion -cne $binaryVersion -or
    [string]$project.Project.PropertyGroup.FileVersion -cne $binaryVersion -or
    [string]$applicationManifest.assembly.assemblyIdentity.version -cne $binaryVersion) {
    throw 'Binary version declarations disagree: check AssemblyVersion, FileVersion and app.manifest.'
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
} elseif ($Version -cne $projectVersion) {
    throw 'The requested package version must match the repository release version.'
}
if ($CheckVersionOnly) { return $Version }
$publishRoot = Join-Path $windowsRoot "artifacts\publish\$RuntimeIdentifier"
$artifactRoot = Join-Path $projectRoot "Artifacts"
$architecture = $RuntimeIdentifier.Replace("win-", "")
$archiveName = "CodeRim-Windows-$Version-$architecture.zip"
$archivePath = Join-Path $artifactRoot $archiveName
$checksumPath = "$archivePath.sha256"
$manifestPath = Join-Path $artifactRoot "SHA256SUMS-windows.txt"

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$') {
    throw "Unsupported version: $Version"
}

# No environment-based publisher pin, automatic certificate creation, or unsigned fallback in Required mode.
$certificate = $null
if ($SigningMode -eq 'Required') {
    if ($PSVersionTable.PSEdition -ne 'Core' -or -not $IsWindows) { throw 'Signed packaging requires PowerShell 7 on Windows.' }
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Signed updates require a canonical stable version.' }
    if ($PublisherSpkiSha256 -cnotmatch '^[0-9a-f]{64}$' -or $SigningCertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'An explicit publisher SPKI pin and existing certificate thumbprint are required.' }
    if ($TimestampServer.Scheme -cne 'https' -or $TimestampServer.UserInfo -or $TimestampServer.Fragment) { throw 'An HTTPS timestamp server is required.' }
    if (-not $SignToolPath -or -not [IO.Path]::IsPathFullyQualified($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf) -or
        [IO.Path]::GetFileName($SignToolPath) -ine 'signtool.exe' -or (Get-AuthenticodeSignature -LiteralPath $SignToolPath).Status -ne 'Valid') {
        throw 'An explicit validly signed Windows SDK signtool.exe is required.'
    }
    $certificate = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $SigningCertificateThumbprint) -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw 'The existing signing certificate has no private key.' }
    $actualPin = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    if ($actualPin -cne $PublisherSpkiSha256) { throw 'The existing signing certificate does not match the configured publisher pin.' }
} elseif ($SigningCertificateThumbprint -or $PublisherSpkiSha256 -or $SignToolPath) { throw 'Signing inputs require SigningMode Required.' }
function Sign-PayloadFile([string]$Path) {
    # RFC3161 SignTool timestamping supports the HTTPS endpoint; Set-AuthenticodeSignature's underlying API does not.
    & $SignToolPath sign /fd SHA256 /sha1 $SigningCertificateThumbprint /s My /tr $TimestampServer.AbsoluteUri /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw 'Windows SDK publisher signing failed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ine $certificate.Thumbprint) { throw 'Publisher signing or timestamp verification failed.' }
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
    -p:DebugSymbols=false `
    "-p:CodeRimPublisherSpkiSha256=$PublisherSpkiSha256"

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

$workerProject = Join-Path $windowsRoot 'src\CodeRim.UpdateWorker\CodeRim.UpdateWorker.csproj'
$resourceArguments = @('-p:CodeRimPublisherSpkiSha256=', '-p:CodeRimUpdateWin32Resource=')
if ($SigningMode -eq 'Required') {
    $installer = Join-Path $publishRoot 'install.ps1'
    $script = [IO.File]::ReadAllText($installer).Replace('$signedPackage = $false', '$signedPackage = $true')
    [IO.File]::WriteAllText($installer, $script, [Text.UTF8Encoding]::new($true))
    # Final signed application/CLI/script bytes are inventoried before the worker embeds their manifest.
    foreach ($file in @($executablePath, (Join-Path $publishRoot 'CodeRimCLI.exe'), $installer, (Join-Path $publishRoot 'connect-claude.ps1'))) { Sign-PayloadFile $file }
    $resourceRoot = Join-Path $windowsRoot ('artifacts\update-resource-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $resourceRoot | Out-Null
    $resourcePath = Join-Path $resourceRoot 'payload.res'
    python (Join-Path $PSScriptRoot 'update_payload_manifest.py') --payload $publishRoot --version $Version --architecture $architecture --json (Join-Path $resourceRoot 'payload.json') --resource $resourcePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $resourcePath)) { throw 'Signed payload manifest generation failed.' }
    $resourceArguments = @("-p:CodeRimPublisherSpkiSha256=$PublisherSpkiSha256", "-p:CodeRimUpdateWin32Resource=$resourcePath")
}
# The legacy ZIP entry remains disabled without its publisher pin. The MSI entry requires a release-key-signed manifest.
$workerRoot = Join-Path $windowsRoot ('artifacts\worker-publish-' + [Guid]::NewGuid().ToString('N'))
dotnet publish $workerProject --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $workerRoot -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false @resourceArguments
if ($LASTEXITCODE -ne 0) { throw 'Update worker publish failed.' }
$workerPath = Join-Path $workerRoot 'CodeRim.UpdateWorker.exe'
if (-not (Test-Path $workerPath)) { throw 'Published update worker is missing.' }
if ($SigningMode -eq 'Required') { Sign-PayloadFile $workerPath }
else {
    # Pin the already-installed bootstrap worker in the GUI before building a public MSI.
    $installerWorkerHash = (Get-FileHash $workerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    dotnet publish $projectPath --configuration Release --runtime $RuntimeIdentifier --self-contained true --output $publishRoot -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false "-p:CodeRimInstallerWorkerSha256=$installerWorkerHash" -p:CodeRimPublisherSpkiSha256=
    if ($LASTEXITCODE -ne 0) { throw 'Installer bootstrap pin publish failed.' }
    $bootstrap = @{version=$Version; workerSha256=$installerWorkerHash; guiSha256=(Get-FileHash $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()}
    [IO.File]::WriteAllText((Join-Path $publishRoot 'CodeRim.bootstrap.json'), ($bootstrap | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $workerPath -Destination $publishRoot

# Verify emitted PE metadata as well as source declarations before creating a release archive.
$versionedExecutables = @('CodeRim.exe', 'CodeRimCLI.exe')
# A Required worker replaces VERSIONINFO with its authenticated RT_RCDATA payload manifest.
# SignedInstallPayload/WindowsUpdateWorker compare the authenticated manifest with the release request.
if ($SigningMode -eq 'Unsigned') { $versionedExecutables += 'CodeRim.UpdateWorker.exe' }
foreach ($name in $versionedExecutables) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishRoot $name))
    if ($info.FileVersion -cne $binaryVersion -or ($info.ProductVersion -split '\+', 2)[0] -cne $Version) {
        throw ("Published binary version does not match the release: " + $name)
    }
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
