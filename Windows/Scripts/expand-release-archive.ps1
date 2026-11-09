param([Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture)
$ErrorActionPreference = 'Stop'
$archives = @(Get-ChildItem 'Artifacts/packaged' -Filter "CodeRim-Windows-*-$Architecture.zip" -Recurse)
if ($archives.Count -ne 1) { throw "Expected exactly one $Architecture release archive." }
$archive = $archives[0]
$checksum = Get-Content ($archive.FullName + '.sha256') -Raw
$expected = ($checksum.Trim() -split '\s+')[0]
$actual = (Get-FileHash $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -notmatch '^[0-9a-f]{64}$' -or $actual -ne $expected) { throw "$Architecture archive checksum mismatch." }
$publish = "Windows/artifacts/publish/win-$Architecture"
New-Item -ItemType Directory -Force $publish | Out-Null
Expand-Archive $archive.FullName $publish
@{ archive = $archive.Name; sha256 = $actual; source = 'CodeRim-Windows packaging artifact' } |
    ConvertTo-Json | Set-Content 'Artifacts/windows-native-archive.json' -Encoding utf8
