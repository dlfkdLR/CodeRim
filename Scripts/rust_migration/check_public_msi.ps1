# Called by the disposable Windows installer suite after its last uninstall.
function Test-PublicMsiTransition {
    $stableVersion = '2.1.41'
    # Only MSI registration advances. The payload is the unmodified candidate publish directory.
    $fixtureVersion = '2.1.42'
    $hashes = @{
        x64 = '1816cf6866cce5be8f626b5d85af1757ec890a8d9b8677f80a3385d602bf298d'
        arm64 = '393302b1f679fdf1bf82147906c39eb4a045b2a70ed6b7cbb126cb0b0a401073'
    }
    $previousRoot = $env:CODERIM_DATA_DIR
    $phases = [Collections.Generic.List[string]]::new()
    $finished = $false
    $footprint = @{}
    function Measure-InstalledFiles {
        $all = @(Get-ChildItem -LiteralPath $app -Recurse -File)
        return [ordered]@{files=$all.Count; bytes=($all | Measure-Object Length -Sum).Sum; publicCliBytes=(Get-Item (Join-Path $app 'CodeRimCLI.exe')).Length}
    }
    try {
        $stableMsi = Join-Path $qa 'published-2.1.41.msi'
        $url = "https://github.com/dlfkdLR/CodeRim/releases/download/v$stableVersion/CodeRim-Windows-$stableVersion-$Architecture-Setup.msi"
        Invoke-WebRequest $url -OutFile $stableMsi
        if ((Get-FileHash $stableMsi).Hash.ToLowerInvariant() -cne $hashes[$Architecture]) { throw 'Published 2.1.41 MSI hash mismatch.' }
        $upgrade = Join-Path $qa 'migration-upgrade.msi'
        $failure = Join-Path $qa 'migration-rollback.msi'
        Build-Fixture $fixtureVersion $current $template $upgrade
        Build-Fixture $fixtureVersion $current $failureTemplate $failure

        # Use the real default profile location on the ephemeral runner. Preserve unexpected preexisting files.
        $profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodeRim'
        $settings = Join-Path $profileRoot 'settings.json'
        $snapshot = Join-Path $profileRoot 'snapshot.json'
        if ((Test-Path $settings) -or (Test-Path $snapshot)) { throw 'Published MSI test needs an unused default CodeRim profile.' }
        [void][IO.Directory]::CreateDirectory($profileRoot)
        Remove-Item Env:CODERIM_DATA_DIR -ErrorAction Stop
        $stamp = '2024-01-02T03:04:05+00:00'
        $usage = @{inputTokens=100;cachedInputTokens=25;outputTokens=13;cacheWriteInputTokens=4}
        $provider = @{
            id='codex';name='CodeRim QA';enabled=$true
            localUsage=@{scope='this-pc';state='partial';updatedAt=$stamp;periodsAsOf=$stamp;timeZoneIdentifier='UTC';totals=@{today=$usage}}
            limits=@{id='codex';state='ready';updatedAt=$stamp;windows=@()}
        }
        [IO.File]::WriteAllText($snapshot, (@{schemaVersion=1;generatedAt=$stamp;providers=@($provider)} | ConvertTo-Json -Depth 10))
        [IO.File]::WriteAllText($settings, '{"AutomaticRefresh":false,"ShowSessionTokens":true,"ResetTime":"Absolute","EnabledProviders":["codex"]}')
        $settingsHash = (Get-FileHash $settings).Hash
        $snapshotHash = (Get-FileHash $snapshot).Hash
        function Assert-ProfileFiles {
            if ((Get-FileHash $settings).Hash -cne $settingsHash -or (Get-FileHash $snapshot).Hash -cne $snapshotHash) {
                throw 'The installer changed the CodeRim settings or usage snapshot.'
            }
        }
        function Read-InstalledTokens {
            Assert-ProfileFiles
            $output = & (Join-Path $app 'CodeRimCLI.exe') tokens --provider codex --period today --format json
            if ($LASTEXITCODE -ne 0) { throw "Installed CLI failed with $LASTEXITCODE." }
            $json = $output -join "`n"
            $record = ($json | ConvertFrom-Json).providers
            $tokens = $record[0].localUsage.totals.today
            if (@($record).Count -ne 1 -or $record[0].name -cne 'CodeRim QA' -or $tokens.totalTokens -ne 113 -or
                $tokens.cachedInputTokens -ne 25 -or $tokens.cacheWriteInputTokens -ne 4) { throw 'Installed CLI lost synthetic usage data.' }
            return $json
        }
        function Get-InstalledInventory {
            $entries = foreach ($item in Get-ChildItem -LiteralPath $app -Recurse -File | Sort-Object FullName) {
                $relative = [IO.Path]::GetRelativePath($app, $item.FullName)
                "$relative $((Get-FileHash -LiteralPath $item.FullName).Hash)"
            }
            return $entries -join "`n"
        }
        [void](Invoke-Msi @('/i', ('"'+$stableMsi+'"'), 'LAUNCHAPP=0'))
        $stable = Product-State
        if ($stable.version -cne $stableVersion) { throw 'Published MSI registered the wrong version.' }
        Assert-RustFiles $false
        Require-OneCliPath
        $publicInventory = Get-InstalledInventory
        $expected = Read-InstalledTokens
        $footprint.published = Measure-InstalledFiles
        $footprint.publishedMsiBytes = (Get-Item -LiteralPath $stableMsi).Length
        $phases.Add('published-installed-and-profile-read')

        [void](Invoke-Msi @('/i', ('"'+$failure+'"'), 'LAUNCHAPP=0') $false)
        Require-InjectedRollback
        Compare-State $stable (Product-State)
        Assert-RustFiles $false
        if ((Get-InstalledInventory) -cne $publicInventory) { throw 'Failed upgrade changed the published file inventory.' }
        if ((Read-InstalledTokens) -cne $expected) { throw 'Failed upgrade changed CLI output.' }
        $phases.Add('failed-upgrade-restored-public-registration-binaries-and-profile')

        [void](Invoke-Msi @('/i', ('"'+$upgrade+'"'), 'LAUNCHAPP=0'))
        $candidate = Product-State
        if ($candidate.version -cne $fixtureVersion -or $candidate.product -eq $stable.product) { throw 'Candidate registration did not replace the release.' }
        foreach ($file in Get-ChildItem -LiteralPath $current -Recurse -File) {
            if ($file.Name -ieq 'install.ps1') { continue }  # excluded by the MSI payload generator
            $relative = [IO.Path]::GetRelativePath($current, $file.FullName)
            $actual = Get-FileHash -LiteralPath (Join-Path $app $relative)
            if ($actual.Hash -cne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Candidate payload mismatch: $relative" }
        }
        Assert-RustFiles $true
        Require-OneCliPath
        if ((Read-InstalledTokens) -cne $expected) { throw 'Candidate changed CLI output.' }
        $footprint.candidate = Measure-InstalledFiles
        $footprint.candidateMsiBytes = (Get-Item -LiteralPath $Installer).Length
        $phases.Add('upgraded-candidate-loaded-preserved-profile')

        $refused = Invoke-Msi @('/i', ('"'+$stableMsi+'"'), 'LAUNCHAPP=0') $false
        $downgradeLog = Get-Content (Join-Path $repo "Artifacts/windows-msi-$Architecture-$script:logIndex.log") -Raw
        if ($refused -ne 1603 -or $downgradeLog -notmatch 'A newer CodeRim version is already installed\.' -or
            $downgradeLog -notmatch 'WIX_DOWNGRADE_DETECTED') { throw 'Published MSI failed without confirming the downgrade guard.' }
        Compare-State $candidate (Product-State)
        if ((Read-InstalledTokens) -cne $expected) { throw 'Refused downgrade changed CLI output.' }
        $phases.Add('direct-downgrade-refused-with-data-intact')

        [void](Invoke-Msi @('/x', $candidate.product))
        Assert-RustFiles $false
        Assert-ProfileFiles
        [void](Invoke-Msi @('/i', ('"'+$stableMsi+'"'), 'LAUNCHAPP=0'))
        Compare-State $stable (Product-State)
        Require-OneCliPath
        if ((Get-InstalledInventory) -cne $publicInventory) { throw 'Restored file inventory differs from the published MSI.' }
        if ((Read-InstalledTokens) -cne $expected) { throw 'Restored release changed CLI output.' }
        $phases.Add('published-msi-reinstalled-and-profile-read')

        [void](Invoke-Msi @('/x', $stable.product))
        Require-OriginalUnrelatedPath
        if ((Test-Path (Join-Path $app 'CodeRim.exe')) -or
            ([string][Environment]::GetEnvironmentVariable('Path','User')).TrimEnd(';') -cne $originalPath.TrimEnd(';')) {
            throw 'Published release uninstall left binaries or changed PATH.'
        }
        Assert-ProfileFiles
        $phases.Add('published-uninstalled-and-data-preserved')
        Remove-Item -LiteralPath $settings,$snapshot
        $finished = $true
    } finally {
        $env:CODERIM_DATA_DIR = $previousRoot
        $receipt = @{architecture=$Architecture;publishedVersion=$stableVersion;publishedMsiSha256=$hashes[$Architecture];
            testMsiVersion=$fixtureVersion;candidatePayloadVersion=$version;footprint=$footprint;phases=@($phases);completed=$finished}
        $receipt | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $repo "Artifacts/windows-public-msi-$Architecture.json")
    }
}
