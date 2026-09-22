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
$originalPath=[Environment]::GetEnvironmentVariable('Path','User')
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
function Build-Fixture([string]$Version,[string]$Publish,[string]$Template,[string]$Output) {
    $tools=Join-Path $windowsRoot 'artifacts\wix-7.0.0'
    if(-not(Test-Path (Join-Path $tools 'wix.exe'))){dotnet tool install wix --version 7.0.0 --tool-path $tools;if($LASTEXITCODE -ne 0){throw 'WiX restore failed'}}
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
    Compare-State $before (Product-State);$results.Add('injected-upgrade-failure-restores-binaries-registration-path-shortcut')
    [void](Invoke-Msi @('/i',('"'+$Installer+'"'),'LAUNCHAPP=0'))
    $after=Product-State
    if($after.version -ne $version -or [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'CodeRim.exe')).FileVersion -ne "$version.0"){throw 'Upgrade version mismatch'}
    $results.Add('upgrade-current-version')
    [void](Invoke-Msi @('/i',('"'+$oldMsi+'"'),'LAUNCHAPP=0') $false)
    Compare-State $after (Product-State);$results.Add('downgrade-refused-without-changes')
    $capture=Join-Path $repo 'Artifacts/windows-installed-dashboard.png'
    $smoke=Start-Process (Join-Path $app 'CodeRim.exe') -ArgumentList '--smoke-test','--capture',('"'+$capture+'"') -PassThru
    if(-not $smoke.WaitForExit(180000) -or $smoke.ExitCode -ne 0 -or -not(Test-Path $capture)){throw 'Installed native app smoke failed'}
    $results.Add('installed-native-ui-and-pinned-worker')
    New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null
    New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CodeRim -Value ('"'+(Join-Path $app 'CodeRim.exe')+'"') -PropertyType String -Force | Out-Null
    [void](Invoke-Msi @('/x',$after.product))
    if ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).CodeRim) { throw 'Uninstall left the owned startup entry.' }
    if(Test-Path (Join-Path $app 'CodeRim.exe')){throw 'Uninstall left application binary'}
    if([IO.File]::ReadAllText($sentinel) -cne 'preserve'){throw 'Uninstall changed user data'}
    $remaining=[Environment]::GetEnvironmentVariable('Path','User')
    if($remaining.TrimEnd(';') -cne $originalPath.TrimEnd(';')){throw 'Uninstall changed unrelated PATH values'}
    $results.Add('uninstall-preserves-user-data-and-unrelated-path')
} finally {
    @{architecture=$Architecture;passed=@($results);completed=($results.Count -eq 7);qaDirectory=$qa} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $repo 'Artifacts/windows-msi-lifecycle.json')
}
