param([ValidateSet('x64','arm64')][string]$Architecture='x64',[Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference='Stop'
if ($env:CI -ne 'true') { throw 'MSI lifecycle tests require a disposable CI runner.' }
$windowsRoot=Split-Path -Parent $PSScriptRoot
$repo=Split-Path -Parent $windowsRoot
$Installer=(Resolve-Path -LiteralPath $Installer).Path
$app=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CodeRim'
$qa=Join-Path $env:TEMP ('CodeRim-MSI-QA-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $qa,(Join-Path $repo 'Artifacts') | Out-Null
$results=[Collections.Generic.List[string]]::new()
$originalPath=[string][Environment]::GetEnvironmentVariable('Path','User')
$data=Join-Path $qa 'Data 한글'; New-Item -ItemType Directory $data | Out-Null
$env:CODERIM_DATA_DIR=$data
$sentinel=Join-Path $data 'account-settings-history-sentinel';[IO.File]::WriteAllText($sentinel,'preserve')
$msi=Join-Path $env:WINDIR 'System32\msiexec.exe'
$logIndex=0
function Invoke-Msi([string[]]$Arguments,[bool]$Success=$true) {
    $script:logIndex++
    $log=Join-Path $repo "Artifacts/windows-msi-$Architecture-$script:logIndex.log"
    $process=Start-Process $msi -ArgumentList ($Arguments+@('/qn','/norestart','REBOOT=ReallySuppress',('/l*v "'+$log+'"'))) -PassThru
    if (-not $process.WaitForExit(180000)) { throw 'MSI is still running; no rollback or app restart is assumed.' }
    if ($Success -and $process.ExitCode -ne 0) { throw "MSI failed with $($process.ExitCode). See $log" }
    if (-not $Success -and $process.ExitCode -eq 0) { throw 'MSI did not reject the failure fixture.' }
    return $process.ExitCode
}
function Product-State {
    $key=Get-ItemProperty 'HKCU:\Software\CodeRim\Installer' -ErrorAction Stop
    $files=@{};foreach($name in @('CodeRim.exe','CodeRimCLI.exe','CodeRim.UpdateWorker.exe')){$files[$name]=(Get-FileHash (Join-Path $app $name) -Algorithm SHA256).Hash}
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'CodeRim.lnk')).TargetPath
    return @{version=$key.Version;product=$key.ProductCode;files=$files;path=[Environment]::GetEnvironmentVariable('Path','User');shortcut=$shortcut}
}
function Compare-State($Before,$After) {
    if ($Before.version -cne $After.version -or $Before.product -cne $After.product -or $Before.path -cne $After.path -or $Before.shortcut -cne $After.shortcut) { throw 'MSI rollback did not restore registration, PATH or shortcut.' }
    foreach($name in $Before.files.Keys){if($Before.files[$name] -cne $After.files[$name]){throw "MSI rollback did not restore $name"}}
}
function Require-OneCliPath {
    $entries=@([string]([Environment]::GetEnvironmentVariable('Path','User')) -split ';' | Where-Object { $_.TrimEnd('\') -ieq (Join-Path $app 'bin') })
    if($entries.Count -ne 1){throw "Expected one CodeRim CLI path; found $($entries.Count)."}
    if($entries[0] -cne (Join-Path $app 'bin')){throw 'MSI did not use the canonical portable CLI path.'}
}
function Require-OriginalUnrelatedPath {
    $remaining=@([string]([Environment]::GetEnvironmentVariable('Path','User')) -split ';' | Where-Object { $_.TrimEnd('\') -ine (Join-Path $app 'bin') }) -join ';'
    if($remaining.TrimEnd(';') -cne $originalPath.TrimEnd(';')){throw 'MSI changed unrelated PATH entries.'}
}
function Require-InjectedRollback {
    $rollbackLog=Get-Content (Join-Path $repo "Artifacts/windows-msi-$Architecture-$script:logIndex.log") -Raw
    foreach($required in @('Intentional QA rollback fixture','Action start [^\r\n]*: QaFail\.','Action ended [^\r\n]*: InstallExecute\. Return value 1\.','Action start [^\r\n]*: InstallFiles\.','Action start [^\r\n]*: RemoveExistingProducts\.','ScriptType=2','Executing op: FileCopy\(SourceName=[^\r\n]*\.rbf')) {
        if($rollbackLog -notmatch $required){throw "Rollback fixture did not reach the expected transaction stage: $required"}
    }
}
function Build-Fixture([string]$Version,[string]$Publish,[string]$Template,[string]$Output) {
    $tools=Join-Path $windowsRoot 'artifacts\wix-5.0.2'
    if(-not(Test-Path (Join-Path $tools 'wix.exe'))){dotnet tool install wix --version 5.0.2 --allow-roll-forward --tool-path $tools;if($LASTEXITCODE -ne 0){throw 'WiX restore failed'}}
    $payload=Join-Path $qa ([IO.Path]::GetFileNameWithoutExtension($Output)+'.wxs')
    python (Join-Path $PSScriptRoot 'generate_msi_payload.py') $Publish $payload
    if($LASTEXITCODE -ne 0){throw 'Fixture payload failed'}
    $code=python -c 'import uuid,sys;print(uuid.uuid5(uuid.UUID("ab0d9f06-26bd-4cae-9d30-de386b872e81"),sys.argv[1]+"-"+sys.argv[2]))' $Version $Architecture
    & (Join-Path $tools 'wix.exe') build $Template $payload -arch $Architecture -d "Version=$Version" -d "ProductCode=$code" -d "IconFile=$windowsRoot\src\CodeRim.Windows\Assets\CodeRim.ico" -o $Output
    if($LASTEXITCODE -ne 0){throw 'Fixture MSI build failed'}
}
try {
    if (Test-Path (Join-Path $app 'CodeRim.exe')) { throw 'CI must start without an existing application.' }
    New-Item -ItemType Directory -Force $app | Out-Null
    $legacyMarker=Join-Path $app '.coderim-install.json';[IO.File]::WriteAllText($legacyMarker,'legacy signed installation sentinel')
    [void](Invoke-Msi @('/i',('"'+$Installer+'"'),'LAUNCHAPP=0') $false)
    if(Test-Path (Join-Path $app 'CodeRim.exe')){throw 'Legacy signed installation was overwritten'}
    [IO.File]::Delete($legacyMarker);$results.Add('signed-legacy-installation-rejected-before-write')
    $oldZip=Join-Path $qa 'previous.zip'
    Invoke-WebRequest "https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.8/CodeRim-Windows-2.1.8-$Architecture.zip" -OutFile $oldZip
    $expected=if($Architecture -eq 'x64'){'e6003944eff3be3421d92682732157f4f895d2919be46838e3fe3309deb37371'}else{'614e6e76cf96a1a3e70dc77ba30287c4fecc4d9639a58bc91c5755508974d6a4'}
    if((Get-FileHash $oldZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected){throw 'Previous release hash mismatch'}
    $old=Join-Path $qa 'previous';Expand-Archive $oldZip $old
    $template=Join-Path $windowsRoot 'Installer\Package.wxs'
    $oldMsi=Join-Path $qa 'previous.msi';Build-Fixture '2.1.8' $old $template $oldMsi
    [void](Invoke-Msi @('/i',('"'+$oldMsi+'"'),'LAUNCHAPP=0'))
    $before=Product-State
    if($before.version -ne '2.1.8'){throw 'Initial installation version mismatch'}
    $results.Add('initial-msi-install')
    # Execute the queued file/registry changes before injecting a deterministic failure.
    [xml]$failure=Get-Content $template
    $ns=$failure.DocumentElement.NamespaceURI
    $action=$failure.CreateElement('CustomAction',$ns);$action.SetAttribute('Id','QaFail');$action.SetAttribute('Error','Intentional QA rollback fixture');[void]$failure.Wix.Package.AppendChild($action)
    $execute=$failure.CreateElement('InstallExecute',$ns);$execute.SetAttribute('Before','InstallFinalize');[void]$failure.Wix.Package.InstallExecuteSequence.AppendChild($execute)
    $call=$failure.CreateElement('Custom',$ns);$call.SetAttribute('Action','QaFail');$call.SetAttribute('After','InstallExecute');$call.SetAttribute('Condition','1');[void]$failure.Wix.Package.InstallExecuteSequence.AppendChild($call)
    $failureTemplate=Join-Path $qa 'failure-template.wxs';$failure.Save($failureTemplate)
    $version=[string]([xml](Get-Content (Join-Path $windowsRoot 'src\CodeRim.Windows\CodeRim.Windows.csproj'))).Project.PropertyGroup.Version
    $current=Join-Path $windowsRoot "artifacts\publish\win-$Architecture"
    $failedMsi=Join-Path $qa 'failure.msi';Build-Fixture $version $current $failureTemplate $failedMsi
    [void](Invoke-Msi @('/i',('"'+$failedMsi+'"'),'LAUNCHAPP=0') $false)
    Require-InjectedRollback
    Compare-State $before (Product-State);$results.Add('injected-upgrade-failure-restores-binaries-registration-path-shortcut')
    [void](Invoke-Msi @('/i',('"'+$Installer+'"'),'LAUNCHAPP=0'))
    $after=Product-State
    if($after.version -ne $version -or [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'CodeRim.exe')).FileVersion -ne "$version.0"){throw 'Upgrade version mismatch'}
    $results.Add('upgrade-current-version')
    [void](Invoke-Msi @('/i',('"'+$oldMsi+'"'),'LAUNCHAPP=0') $false)
    Compare-State $after (Product-State);$results.Add('downgrade-refused-without-changes')
    $capture=Join-Path $repo 'Artifacts/installed/windows-installed-dashboard.png'
    $smoke=Start-Process (Join-Path $app 'CodeRim.exe') -ArgumentList '--smoke-test','--capture',('"'+$capture+'"') -PassThru
    if(-not $smoke.WaitForExit(180000) -or $smoke.ExitCode -ne 0 -or -not(Test-Path $capture)){throw 'Installed native app smoke failed'}
    $results.Add('installed-native-ui-and-pinned-worker')
    New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null
    New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CodeRim -Value ('"'+(Join-Path $app 'CodeRim.exe')+'"') -PropertyType String -Force | Out-Null
    [void](Invoke-Msi @('/x',$after.product))
    if ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).CodeRim) { throw 'Uninstall left the owned startup entry.' }
    if(Test-Path (Join-Path $app 'CodeRim.exe')){throw 'Uninstall left application binary'}
    if([IO.File]::ReadAllText($sentinel) -cne 'preserve'){throw 'Uninstall changed user data'}
    $remaining=[string][Environment]::GetEnvironmentVariable('Path','User')
    if($remaining.TrimEnd(';') -cne $originalPath.TrimEnd(';')){throw 'Uninstall changed unrelated PATH values'}
    $results.Add('uninstall-preserves-user-data-and-unrelated-path')
    # The public ZIP installer creates a CLI path without a trailing separator.
    # This must remain a single path when adopting the portable files into MSI.
    & (Join-Path $old 'install.ps1') -AddCliToPath
    $portablePath=[Environment]::GetEnvironmentVariable('Path','User')
    Require-OneCliPath
    $portableFiles=@{};foreach($name in @('CodeRim.exe','CodeRimCLI.exe','CodeRim.UpdateWorker.exe')){$portableFiles[$name]=(Get-FileHash (Join-Path $app $name) -Algorithm SHA256).Hash}
    $portableShortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'CodeRim.lnk')).TargetPath
    [void](Invoke-Msi @('/i',('"'+$failedMsi+'"'),'LAUNCHAPP=0') $false)
    Require-InjectedRollback
    if([Environment]::GetEnvironmentVariable('Path','User') -cne $portablePath){throw 'Failed portable migration changed PATH.'}
    foreach($name in $portableFiles.Keys){if((Get-FileHash (Join-Path $app $name) -Algorithm SHA256).Hash -cne $portableFiles[$name]){throw "Failed portable migration changed $name"}}
    if((Get-ItemProperty 'HKCU:\Software\CodeRim\Installer' -ErrorAction SilentlyContinue).ProductCode){throw 'Failed portable migration left MSI registration.'}
    if((New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'CodeRim.lnk')).TargetPath -cne $portableShortcut){throw 'Failed portable migration changed the shortcut.'}
    $results.Add('portable-migration-failure-restores-files-path-shortcut')
    [void](Invoke-Msi @('/i',('"'+$Installer+'"'),'LAUNCHAPP=0'))
    Require-OneCliPath;Require-OriginalUnrelatedPath
    $portableAfter=Product-State
    foreach($name in $portableAfter.files.Keys){if($portableAfter.files[$name] -cne (Get-FileHash (Join-Path $current $name) -Algorithm SHA256).Hash){throw "Portable migration retained old $name"}}
    [void](Invoke-Msi @('/fvamus',('"'+$Installer+'"'),'LAUNCHAPP=0'))
    Require-OneCliPath;Require-OriginalUnrelatedPath
    $results.Add('portable-migration-and-repair-keep-one-cli-path')
    [void](Invoke-Msi @('/x',$portableAfter.product))
    if(Test-Path (Join-Path $app 'CodeRim.exe')){throw 'Portable migration uninstall left the application binary.'}
    if(([string][Environment]::GetEnvironmentVariable('Path','User')).TrimEnd(';') -cne $originalPath.TrimEnd(';')){throw 'Portable migration uninstall left CLI path or changed other entries.'}
    if([IO.File]::ReadAllText($sentinel) -cne 'preserve'){throw 'Portable migration changed user data.'}
    $results.Add('portable-migration-uninstall-preserves-data-and-unrelated-path')
    # Reproduce the two-path state observed on the user's PC with the exact public MSI.
    [Environment]::SetEnvironmentVariable('Path',($originalPath.TrimEnd(';')+';'+(Join-Path $app 'bin')),'User')
    if ([version]$version -le [version]'2.1.11') { throw 'Public MSI upgrade requires a version newer than 2.1.11.' }
    $publicMsi=Join-Path $qa 'public-2.1.11.msi'
    Invoke-WebRequest "https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.11/CodeRim-Windows-2.1.11-$Architecture-Setup.msi" -OutFile $publicMsi
    $publicHash=if($Architecture -eq 'x64'){'493165b349879fe4ff7d74ea35e2b6932e8e5d447785f79d52647ed834d746fb'}else{'79798bad8089765fcc3bee66f8e78bd7486ac5d35b09ff287a856d9e1a161343'}
    if((Get-FileHash $publicMsi -Algorithm SHA256).Hash.ToLowerInvariant() -cne $publicHash){throw 'Public 2.1.11 MSI hash mismatch.'}
    [void](Invoke-Msi @('/i',('"'+$publicMsi+'"'),'LAUNCHAPP=0'))
    $publicBefore=Product-State
    if($publicBefore.version -cne '2.1.11'){throw 'Public MSI initial version mismatch.'}
    $duplicates=@($publicBefore.path -split ';' | Where-Object { $_.TrimEnd('\') -ieq (Join-Path $app 'bin') })
    if($duplicates.Count -ne 2){throw 'Public MSI fixture did not reproduce the portable CLI path duplication.'}
    $results.Add('public-2.1.11-msi-reproduces-duplicate-portable-path')
    [void](Invoke-Msi @('/i',('"'+$failedMsi+'"'),'LAUNCHAPP=0') $false)
    Require-InjectedRollback
    Compare-State $publicBefore (Product-State)
    $results.Add('public-2.1.11-failed-upgrade-restores-installation')
    [void](Invoke-Msi @('/i',('"'+$Installer+'"'),'LAUNCHAPP=0'))
    Require-OneCliPath;Require-OriginalUnrelatedPath
    $publicAfter=Product-State
    if($publicAfter.version -cne $version -or $publicAfter.product -eq $publicBefore.product){throw 'Public MSI upgrade retained its previous version or product identity.'}
    foreach($name in $publicAfter.files.Keys){
        if($publicAfter.files[$name] -cne (Get-FileHash (Join-Path $current $name) -Algorithm SHA256).Hash){throw "Public MSI upgrade did not replace $name with the candidate."}
    }
    if([IO.File]::ReadAllText($sentinel) -cne 'preserve'){throw 'Public MSI upgrade changed user data.'}
    $results.Add('public-2.1.11-upgrade-replaces-all-candidate-binaries')
    [void](Invoke-Msi @('/x',$publicAfter.product))
    if(Test-Path (Join-Path $app 'CodeRim.exe')){throw 'Public upgrade uninstall left the application binary.'}
    if([IO.File]::ReadAllText($sentinel) -cne 'preserve'){throw 'Public upgrade uninstall changed user data.'}
    $remaining=[string][Environment]::GetEnvironmentVariable('Path','User')
    if($remaining.TrimEnd(';') -cne $originalPath.TrimEnd(';')){throw 'Public upgrade uninstall changed unrelated PATH values.'}
    $results.Add('public-upgrade-uninstall-preserves-user-data-and-path')
} finally {
    @{architecture=$Architecture;passed=@($results);completed=($results.Count -eq 14);qaDirectory=$qa} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $repo 'Artifacts/windows-msi-lifecycle.json')
}
