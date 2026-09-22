$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('coderim-release-version-' + [Guid]::NewGuid().ToString('N'))
$files = @('Windows/Scripts/package.ps1', 'Windows/Directory.Build.props',
    'Windows/src/CodeRim.Windows/CodeRim.Windows.csproj', 'Windows/src/CodeRim.Windows/app.manifest', 'Config/Release.env')
$expected = [string]([xml](Get-Content (Join-Path $projectRoot $files[2]) -Raw)).Project.PropertyGroup.Version
$cases = @(
    @{ Name = 'consistent'; Pass = $true },
    @{ Name = 'matching-override'; Pass = $true; Override = $expected },
    @{ Name = 'consistent-prerelease'; Pass = $true; Prerelease = $expected + '-qa.1' },
    @{ Name = 'mismatched-override'; Pass = $false; Override = '9.9.9' },
    @{ Name = 'project-version'; Pass = $false; File = $files[2]; Pattern = '<Version>[^<]+</Version>'; Value = '<Version>9.9.9</Version>' },
    @{ Name = 'shared-version'; Pass = $false; File = $files[1]; Pattern = '<Version>[^<]+</Version>'; Value = '<Version>9.9.9</Version>' },
    @{ Name = 'release-version'; Pass = $false; File = $files[4]; Pattern = '(?m)^MARKETING_VERSION=.*$'; Value = 'MARKETING_VERSION=9.9.9' },
    @{ Name = 'missing-release-version'; Pass = $false; File = $files[4]; Pattern = '(?m)^MARKETING_VERSION=.*$'; Value = '' },
    @{ Name = 'duplicate-release-version'; Pass = $false; File = $files[4]; Duplicate = $true },
    @{ Name = 'assembly-version'; Pass = $false; File = $files[2]; Pattern = '<AssemblyVersion>[^<]+</AssemblyVersion>'; Value = '<AssemblyVersion>9.9.9.0</AssemblyVersion>' },
    @{ Name = 'file-version'; Pass = $false; File = $files[2]; Pattern = '<FileVersion>[^<]+</FileVersion>'; Value = '<FileVersion>9.9.9.0</FileVersion>' },
    @{ Name = 'manifest-version'; Pass = $false; File = $files[3]; Pattern = 'assemblyIdentity version="[^"]+"'; Value = 'assemblyIdentity version="9.9.9.0"' }
)
$completed = $false
try {
    foreach ($case in $cases) {
        $root = Join-Path $fixtureRoot $case.Name
        foreach ($file in $files) {
            $destination = Join-Path $root $file
            New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $destination
        }
        $sentinels = @('Windows/artifacts/publish/win-x64/preserve.txt', 'Artifacts/preserve.txt')
        foreach ($sentinel in $sentinels) {
            $path = Join-Path $root $sentinel
            New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
            [IO.File]::WriteAllText($path, 'existing-release-artifact')
        }
        $expectedResult = $expected
        if ($case.Prerelease) {
            $expectedResult = $case.Prerelease
            foreach ($file in @($files[1], $files[2], $files[4])) {
                $path = Join-Path $root $file
                $text = [IO.File]::ReadAllText($path)
                if ($file -eq $files[4]) { $text = [regex]::Replace($text, '(?m)^MARKETING_VERSION=.*$', ('MARKETING_VERSION=' + $expectedResult)) }
                else { $text = [regex]::Replace($text, '<Version>[^<]+</Version>', ('<Version>' + $expectedResult + '</Version>')) }
                [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
            }
        }
        if ($case.File) {
            $path = Join-Path $root $case.File
            $text = [IO.File]::ReadAllText($path)
            if ($case.Duplicate) { $text += [Environment]::NewLine + 'MARKETING_VERSION=' + $expected + [Environment]::NewLine }
            else {
                if (-not [regex]::IsMatch($text, $case.Pattern)) { throw ('Missing fixture field: ' + $case.Name) }
                $text = [regex]::Replace($text, $case.Pattern, $case.Value)
            }
            [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
        }
        $arguments = @{ CheckVersionOnly = $true }
        if ($case.Override) { $arguments.Version = $case.Override }
        $failure = $null
        $result = $null
        try { $result = @(& (Join-Path $root 'Windows/Scripts/package.ps1') @arguments) }
        catch { $failure = $_.Exception.Message }
        if ($case.Pass) {
            if ($failure -or $result.Count -ne 1 -or $result[0] -cne $expectedResult) {
                throw ('Expected validated version for ' + $case.Name + ': ' + $failure)
            }
        } elseif (-not $failure -or $failure -notmatch 'version') {
            throw ('Expected an explicit version rejection for ' + $case.Name)
        }
        foreach ($sentinel in $sentinels) {
            $path = Join-Path $root $sentinel
            if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne 'existing-release-artifact') {
                throw ('Version preflight changed an existing artifact: ' + $case.Name)
            }
        }
        Write-Host ('PASS ' + $case.Name)
    }
    $completed = $true
    Write-Host ("Release version preflight: {0}/{0} passed." -f $cases.Count)
} finally {
    if ($completed) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    else { Write-Warning ('Failed fixture retained at ' + $fixtureRoot) }
}
