$ErrorActionPreference = 'Stop'
$extract = Join-Path $PSScriptRoot 'expand-release-archive.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('CodeRim-archive-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temporary | Out-Null
Push-Location $temporary
try {
    New-Item -ItemType Directory 'Artifacts/packaged', 'payload' | Out-Null
    Set-Content 'payload/CodeRim.exe' 'exact release payload'
    foreach ($architecture in 'x64', 'arm64') {
        $archive = "Artifacts/packaged/CodeRim-Windows-1.2.3-$architecture.zip"
        Compress-Archive 'payload/*' $archive
        $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksum = "$hash  $([IO.Path]::GetFileName($archive))"
        Set-Content "$archive.sha256" $checksum
        & $extract -Architecture $architecture
        $publish = "Windows/artifacts/publish/win-$architecture"
        if ((Get-Content "$publish/CodeRim.exe" -Raw).Trim() -cne 'exact release payload') { throw 'Archive payload changed.' }
        $evidence = Get-Content 'Artifacts/windows-native-archive.json' -Raw | ConvertFrom-Json
        if ($evidence.archive -cne [IO.Path]::GetFileName($archive) -or $evidence.sha256 -cne $hash) { throw 'Archive provenance changed.' }
        Remove-Item $publish -Recurse
        foreach ($invalid in 'mismatch', 'malformed', 'missing', 'duplicate') {
            Set-Content "$archive.sha256" $checksum
            switch ($invalid) {
                mismatch { Set-Content "$archive.sha256" ('0' * 64) }
                malformed { Set-Content "$archive.sha256" 'not-a-sha256' }
                missing { Remove-Item "$archive.sha256" }
                duplicate { Copy-Item $archive "Artifacts/packaged/CodeRim-Windows-9.9.9-$architecture.zip" }
            }
            $rejected = $false
            try { & $extract -Architecture $architecture } catch { $rejected = $true }
            if (-not $rejected -or (Test-Path $publish)) { throw "Accepted invalid $architecture archive: $invalid" }
        }
        Remove-Item 'Artifacts/packaged/*'
        $rejected = $false
        try { & $extract -Architecture $architecture } catch { $rejected = $true }
        if (-not $rejected -or (Test-Path $publish)) { throw "Accepted missing $architecture archive." }
    }
    'Release archive extraction checks passed.'
} finally {
    Pop-Location
    Remove-Item $temporary -Recurse -Force
}
