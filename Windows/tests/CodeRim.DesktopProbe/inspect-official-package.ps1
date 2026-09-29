param([Parameter(Mandatory=$true)][string]$Probe, [Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$QuitReopen)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:GITHUB_REPOSITORY -ne 'dlfkdLR/CodeRim') {
    throw 'This package inspection is restricted to disposable GitHub-hosted CodeRim runners.'
}
if ([Environment]::Is64BitProcess -ne $true) { throw 'Use a 64-bit Windows shell.' }
$packageName = 'OpenAI.Codex'
$publisher = 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B'
if (@(Get-AppxPackage -Name $packageName).Count -ne 0) { throw 'An existing official package must not be modified.' }
if (@(Get-Process -Name ChatGPT,codex -ErrorAction SilentlyContinue).Count -ne 0) { throw 'An existing application must not be inspected.' }
$defaultHome = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
if ((Test-Path $defaultHome) -or $env:CODEX_HOME) { throw 'Expected a fresh runner with no Codex home or login.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$download = Join-Path $env:RUNNER_TEMP ('coderim-official-' + [Guid]::NewGuid().ToString('N') + '.msix')
$headers = $download + '.headers'
$requestHeaders = $download + '.request-headers'
$url = 'https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix'
$expectedEtag = '"0x8DF1BB0D21B4BFB"'
$installed = $null
$installationAttempted = $false
$inspectionSucceeded = $false
$packageRemoved = $false
$failure = $null
$cleanupErrors = [Collections.Generic.List[Exception]]::new()
try {
    # Pin the previously inspected official package; do not silently test a newer release.
    # Header file preserves the literal ETag quotes through Windows PowerShell 5's
    # legacy native-command argument marshalling.
    [IO.File]::WriteAllText($requestHeaders, ('If-Match: ' + $expectedEtag + "`r`n"), [Text.Encoding]::ASCII)
    & curl.exe --fail --silent --show-error --max-time 300 --max-filesize 900000000 --proto '=https' --header ('@' + $requestHeaders) --dump-header $headers --output $download $url
    if ($LASTEXITCODE -ne 0 -or (Get-Item $download).Length -ne 876623361) { throw 'Official package download/version check failed.' }
    $etagMatches = [regex]::Matches((Get-Content $headers -Raw), '(?im)^etag:\s*([^\r\n]+)')
    if ($etagMatches.Count -ne 1) { throw 'Expected one official package entity tag.' }
    $observedEtag = $etagMatches[0].Groups[1].Value.Trim()
    if ($observedEtag -cne $expectedEtag) { throw 'Official package entity tag changed.' }
    $signature = Get-AuthenticodeSignature -FilePath $download
    if ($signature.Status -ne 'Valid') { throw ('Official package signature failed: ' + $signature.Status) }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($download)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if ($null -eq $entry -or $entry.Length -gt 131072) { throw 'Invalid package manifest.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $settings.MaxCharactersInDocument = 131072
        $stream = $entry.Open()
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try { $manifest = [Xml.XmlDocument]::new(); $manifest.XmlResolver = $null; $manifest.Load($reader) }
            finally { $reader.Dispose() }
        } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -ne $packageName -or $identity.Publisher -ne $publisher -or $identity.Version -ne '26.924.2738.0' -or $identity.ProcessorArchitecture -ne 'x64') {
        throw 'Package identity differs from the inspected official release.'
    }
    @{ url=$url; expectedEtag=$expectedEtag; observedEtag=$observedEtag; sha256=(Get-FileHash $download -Algorithm SHA256).Hash; signature=$signature.Status.ToString(); signer=$signature.SignerCertificate.Subject; version=$identity.Version } |
        ConvertTo-Json | Set-Content (Join-Path $output 'package.json') -Encoding UTF8
    $installationAttempted = $true
    Add-AppxPackage -Path $download
    $matches = @(Get-AppxPackage -Name $packageName)
    if ($matches.Count -ne 1) { throw 'Expected exactly one newly installed official package.' }
    $installed = $matches[0]
    if ($installed.Publisher -ne $publisher -or $installed.SignatureKind -ne 'Store' -or $installed.IsDevelopmentMode -or $installed.Status -ne 'Ok') {
        throw 'Installed package trust/status check failed.'
    }
    # Signed-out inspection is read-only by default. A separate opt-in permits one
    # standard Quit shortcut and reopen in this disposable VM; no account/model calls.
    $arguments = @($installed.PackageFullName, ($installed.PackageFamilyName + '!App'), ('"' + $output + '"'))
    if ($QuitReopen) { $arguments += '--quit-reopen-probe' }
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Probe)) -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(90000)) {
        $process.Kill() # only our hung read-only recorder, not the official app
        throw 'Accessibility recorder timed out.'
    }
    if ($process.ExitCode -ne 0) { throw 'Official app UI inspection failed; inspect the recorded error.' }
    $inspectionSucceeded = $true
} catch { $failure = $_.Exception } finally {
    # Package removal is fixture cleanup, never evidence of a normal Quit action.
    try {
        if ($installationAttempted -and $null -eq $installed) {
            $owned = @(Get-AppxPackage -Name $packageName | Where-Object { $_.Publisher -eq $publisher -and $_.Version -eq '26.924.2738.0' })
            if ($owned.Count -eq 1) { $installed = $owned[0] }
        }
        if ($null -ne $installed) {
            Remove-AppxPackage -Package $installed.PackageFullName
            $remaining = @(Get-AppxPackage -Name $packageName | Where-Object { $_.PackageFullName -eq $installed.PackageFullName })
            if ($remaining.Count -ne 0) { throw 'The fixture package remains installed.' }
            $packageRemoved = $true
        }
    } catch { $cleanupErrors.Add($_.Exception) }
    foreach ($temporary in @($download, $headers, $requestHeaders)) {
        try { if (Test-Path $temporary) { Remove-Item $temporary -Force } } catch { $cleanupErrors.Add($_.Exception) }
    }
    try {
        @{ inspectionSucceeded=$inspectionSucceeded; packageRemoved=$packageRemoved; cleanupErrors=@($cleanupErrors | ForEach-Object { $_.Message }); normalQuitVerified=$false; userPc=$false } |
            ConvertTo-Json | Set-Content (Join-Path $output 'cleanup.json') -Encoding UTF8
    } catch { $cleanupErrors.Add($_.Exception) }
}
if ($null -ne $failure) { $cleanupErrors.Insert(0, $failure) }
if ($cleanupErrors.Count -gt 0) { throw [AggregateException]::new('Official app inspection or fixture cleanup failed.', $cleanupErrors.ToArray()) }
