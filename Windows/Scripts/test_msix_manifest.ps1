# Synthetic manifest checks only: no SDK, packaging, certificates, or installation.
$ErrorActionPreference = 'Stop'
$template = Get-Content (Join-Path $PSScriptRoot '../Installer/Store/AppxManifest.template.xml') -Raw
$packager = Get-Content (Join-Path $PSScriptRoot 'package-msix.ps1') -Raw
# Exercise the exact pre-packaging validation block against valid and colliding fixtures.
$start = $packager.IndexOf('    $aliases = ')
if ($start -lt 0) { throw 'Manifest alias validation block not found.' }
$end = $packager.IndexOf('    [IO.File]::WriteAllText', $start)
if ($end -lt $start) { throw 'Manifest alias validation block not found.' }
$validate = [scriptblock]::Create($packager.Substring($start, $end - $start))
$manifest = $template
& $validate
$document = [xml]$template
$gui = $document.SelectSingleNode("//*[local-name()='Application' and @Id='CodeRim']")
if ($gui.Executable -cne 'CodeRim.exe' -or $gui.SelectNodes(".//*[local-name()='ExecutionAlias']").Count -ne 0) {
    throw 'The GUI must use application identity, without an execution alias.'
}
$cli = $document.SelectSingleNode("//*[local-name()='Application' and @Id='CodeRimCLI']")
$aliases = @($cli.SelectNodes(".//*[local-name()='ExecutionAlias']/@Alias") | ForEach-Object Value)
if ($aliases.Count -ne 2 -or $aliases -cnotcontains 'coderim.exe' -or $aliases -cnotcontains 'CodeRimCLI.exe') {
    throw 'Both CLI aliases must remain available.'
}
foreach ($duplicate in 'CodeRim.exe', 'CODERIM.EXE', 'coderim.exe') {
    $manifest = $template.Replace('<uap5:ExecutionAlias Alias="coderim.exe" />',
        ('<uap5:ExecutionAlias Alias="coderim.exe" /><uap5:ExecutionAlias Alias="' + $duplicate + '" />'))
    $rejected = $false
    try { & $validate } catch {
        if ($_.Exception.Message -notlike 'Duplicate execution alias:*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Case-equivalent alias was accepted: $duplicate" }
}
# Compile the same activation interop used by the native smoke, without invoking Windows COM.
$smoke = Get-Content (Join-Path $PSScriptRoot 'test-msix.ps1') -Raw
$interop = [regex]::Match($smoke, "(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if (-not $interop) { throw 'Packaged activation interop not found.' }
Add-Type -TypeDefinition $interop
if (-not $smoke.Contains('!CodeRim"') -or -not $smoke.Contains('[CodeRimMsixActivation]::Launch($appId,')) {
    throw 'Native smoke must launch the GUI by its package application identity.'
}
'MSIX manifest and activation interop checks passed.'
