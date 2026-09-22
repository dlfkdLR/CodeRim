param([ValidateSet('x64','arm64')][string]$Architecture='x64',[Parameter(Mandatory)][string]$ReleaseDirectory)
$ErrorActionPreference='Stop'
if($env:CI -ne 'true'){throw 'Requires a disposable CI runner.'}
$ReleaseDirectory=(Resolve-Path $ReleaseDirectory).Path
$windowsRoot=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $env:TEMP ('CodeRim-Handoff-'+[Guid]::NewGuid().ToString('N'))
$publish=Join-Path $qa 'publish';$worker=Join-Path $qa 'worker'
$app=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CodeRim'
if(Test-Path (Join-Path $app 'CodeRim.exe')){throw 'Existing app cannot be used by CI.'}
New-Item -ItemType Directory -Force $publish,$worker | Out-Null
$env:CODERIM_DATA_DIR=Join-Path $qa 'Data 한글'
New-Item -ItemType Directory $env:CODERIM_DATA_DIR | Out-Null
[IO.File]::WriteAllText((Join-Path $env:CODERIM_DATA_DIR 'settings.json'),'{}')
$sentinel=Join-Path $env:CODERIM_DATA_DIR 'preserve-sentinel';[IO.File]::WriteAllText($sentinel,'settings and credentials stay outside MSI')
$argsCommon=@('--configuration','Release','--runtime',"win-$Architecture",'--self-contained','true','-p:Version=2.1.8','-p:AssemblyVersion=2.1.8.0','-p:FileVersion=2.1.8.0','-p:DebugType=None','-p:DebugSymbols=false','-p:CodeRimPublisherSpkiSha256=','-p:CodeRimUpdateWin32Resource=')
# QA source uses the same production worker; only the old GUI/Core fixture includes the test entry.
dotnet publish (Join-Path $windowsRoot 'src\CodeRim.UpdateWorker\CodeRim.UpdateWorker.csproj') @argsCommon --output $worker
if($LASTEXITCODE -ne 0){throw 'Old worker fixture publish failed.'}
$pin=(Get-FileHash (Join-Path $worker 'CodeRim.UpdateWorker.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
dotnet publish (Join-Path $windowsRoot 'src\CodeRim.Windows\CodeRim.Windows.csproj') @argsCommon --output $publish -p:CodeRimMsiQa=true "-p:CodeRimInstallerWorkerSha256=$pin"
if($LASTEXITCODE -ne 0){throw 'Old GUI fixture publish failed.'}
dotnet publish (Join-Path $windowsRoot 'src\CodeRim.CLI\CodeRim.CLI.csproj') @argsCommon --output $publish
if($LASTEXITCODE -ne 0){throw 'Old CLI fixture publish failed.'}
Copy-Item (Join-Path $worker 'CodeRim.UpdateWorker.exe') $publish
New-Item -ItemType Directory (Join-Path $publish 'bin') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'coderim.cmd') (Join-Path $publish 'bin')
[IO.File]::WriteAllText((Join-Path $publish 'CodeRim.install.json'),'{"product":"CodeRim.Windows","version":"2.1.8","format":"msi"}')
$tools=Join-Path $qa 'tools';dotnet tool install wix --version 5.0.2 --allow-roll-forward --tool-path $tools
if($LASTEXITCODE -ne 0){throw 'WiX restore failed.'}
$payload=Join-Path $qa 'payload.wxs';python (Join-Path $PSScriptRoot 'generate_msi_payload.py') $publish $payload
if($LASTEXITCODE -ne 0){throw 'Payload generation failed.'}
$code=python -c 'import uuid,sys;print(uuid.uuid5(uuid.UUID("ab0d9f06-26bd-4cae-9d30-de386b872e81"),"2.1.8-"+sys.argv[1]))' $Architecture
$oldMsi=Join-Path $qa 'old.msi'
& (Join-Path $tools 'wix.exe') build (Join-Path $windowsRoot 'Installer\Package.wxs') $payload -arch $Architecture -d Version=2.1.8 -d "ProductCode=$code" -d "IconFile=$windowsRoot\src\CodeRim.Windows\Assets\CodeRim.ico" -o $oldMsi
if($LASTEXITCODE -ne 0){throw 'Old MSI fixture build failed.'}
$msi=Join-Path $env:WINDIR 'System32\msiexec.exe'
$install=Start-Process $msi -ArgumentList @('/i',('"'+$oldMsi+'"'),'/qn','/norestart','LAUNCHAPP=0',('/l*v "'+(Join-Path $ReleaseDirectory 'old-install.log')+'"')) -PassThru
if(-not $install.WaitForExit(180000) -or $install.ExitCode -ne 0){throw 'Old MSI installation failed.'}
$parent=Start-Process (Join-Path $app 'CodeRim.exe') -ArgumentList '--qa-msi-update',('"'+$ReleaseDirectory+'"') -PassThru
if(-not $parent.WaitForExit(90000)){throw 'Original GUI did not exit after the worker became ready.'}
if($parent.ExitCode -ne 0){Get-Content (Join-Path $ReleaseDirectory 'handoff-error.txt') -ErrorAction SilentlyContinue;throw 'GUI handoff failed.'}
$id=(Get-Content (Join-Path $ReleaseDirectory 'handoff-operation.txt') -Raw).Trim()
$private=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\.CodeRimUpdate'
$result=Join-Path $private "Results\msi-$id.json"
$deadline=[DateTime]::UtcNow.AddMinutes(4)
while(-not(Test-Path $result) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250}
Copy-Item (Join-Path $private "Launchers\$id\install.log") (Join-Path $ReleaseDirectory 'worker-install.log') -ErrorAction SilentlyContinue
if(-not(Test-Path $result)){throw 'Worker did not finish; no success or rollback is assumed.'}
$outcome=Get-Content $result -Raw | ConvertFrom-Json
if($outcome.Status -ne 0 -or $outcome.ExitCode -ne 0){throw 'Worker did not verify an applied update.'}
$manifest=Get-Content (Join-Path $ReleaseDirectory "CodeRim-Windows-2.1.9-$Architecture-Setup.msi.manifest.json") -Raw | ConvertFrom-Json
$deadline=[DateTime]::UtcNow.AddSeconds(30);$relaunched=$null
while(-not $relaunched -and [DateTime]::UtcNow -lt $deadline){$relaunched=Get-Process CodeRim -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $app 'CodeRim.exe')};Start-Sleep -Milliseconds 250}
if(-not $relaunched -or @($relaunched).Count -ne 1){throw 'Expected exactly one automatically relaunched app.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($relaunched.Path).FileVersion -ne '2.1.9.0'){throw 'Relaunch used the old binary.'}
if((Get-ItemProperty 'HKCU:\Software\CodeRim\Installer').Version -ne '2.1.9'){throw 'Registration was not upgraded.'}
if([IO.File]::ReadAllText($sentinel) -cne 'settings and credentials stay outside MSI'){throw 'User data was changed.'}
if(-not(Test-Path (Join-Path $env:CODERIM_DATA_DIR 'usage.sqlite'))){throw 'Relaunched app did not retain the isolated data-directory environment.'}
# This is the process started by this test on an ephemeral runner, not a user application.
$relaunched | Stop-Process
$receipt=@{architecture=$Architecture;version='2.1.9';sha256=$manifest.sha256;operation=$id;verified=@('real-release-Ed25519-signature','cached-MSI-rehash','compiled-worker-pin','anonymous-pipe-environment','parent-ready-confirm-exit','MSI-service-completion','registered-version-and-product','automatic-new-version-relaunch','separate-user-data-preserved');status='PASS'}
$receipt | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ReleaseDirectory 'handoff-result.json')
