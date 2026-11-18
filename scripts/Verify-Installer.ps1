param([Parameter(Mandatory)][string]$Setup,[Parameter(Mandatory)][string]$InstallDirectory,[Parameter(Mandatory)][string]$ModelFile,[Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$target=[IO.Path]::GetFullPath($InstallDirectory)
if(Test-Path -LiteralPath $target){throw 'Installer acceptance requires a new empty target.'}
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
$model=[IO.Path]::GetFullPath($ModelFile)
$before=(Get-FileHash -LiteralPath $model).Hash.ToLowerInvariant()
function Run([string]$Executable,[string[]]$Arguments,[string]$Name){
 $info=[Diagnostics.ProcessStartInfo]::new($Executable);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.RedirectStandardError=$true;$info.RedirectStandardOutput=$true
 foreach($arg in $Arguments){$info.ArgumentList.Add($arg)}
 $process=[Diagnostics.Process]::Start($info);$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync()
 if(-not $process.WaitForExit(600000)){$process.Kill($true);throw "$Name timeout"}
 $out.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence ($Name+'.stdout.log'))
 $err.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence ($Name+'.stderr.log'))
 if($process.ExitCode -ne 0){throw "$Name failed with exit $($process.ExitCode)"}
 Write-Output "${Name}: exit 0"
}
Run $Setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR='+$target),('/LOG='+(Join-Path $evidence 'installer.log'))) 'install'
$exe=Join-Path $target 'AI-BLACK-HOLE.exe'
if(-not (Test-Path -LiteralPath (Join-Path $target 'installed.marker'))){throw 'Installed data-location marker missing'}
$state=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AI BLACK HOLE'
New-Item -ItemType Directory -Path (Join-Path $state 'data') -Force | Out-Null
$sentinel=Join-Path $state ('data\acceptance-preserve-'+[guid]::NewGuid().ToString('N')+'.txt')
Set-Content -LiteralPath $sentinel -Value 'ABH uninstall acceptance: preserve user-generated data'
$link=Join-Path ([Environment]::GetFolderPath('Programs')) 'AI BLACK HOLE\AI BLACK HOLE.lnk'
$shortcut=Test-Path -LiteralPath $link
$binary=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
Run $exe @('--acceptance','--model-file',$model,'--expected-hash',$before,'--browser','--output',(Join-Path $evidence 'ABH-INSTALLED-INFERENCE.json')) 'installed-inference'
$acceptance=Get-Content (Join-Path $evidence 'ABH-INSTALLED-INFERENCE.json') -Raw | ConvertFrom-Json
Run (Join-Path $target 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG='+(Join-Path $evidence 'uninstall.log'))) 'uninstall'
$preserved=(Test-Path -LiteralPath $sentinel) -and (Test-Path -LiteralPath (Join-Path $state 'data\models.json'))
$modelPreserved=(Get-FileHash -LiteralPath $model).Hash.ToLowerInvariant() -eq $before
$removed=-not (Test-Path -LiteralPath $exe)
$result=[ordered]@{status=if($acceptance.status -eq 'VERIFIED' -and $preserved -and $modelPreserved -and $removed -and $shortcut){'VERIFIED'}else{'PARTIAL'};timestamp=(Get-Date -AsUTC -Format o);installer_sha256=(Get-FileHash -LiteralPath $Setup).Hash.ToLowerInvariant();installed_executable_sha256=$binary;checks=@(@{name='Silent installation in selected new directory';status='VERIFIED'},@{name='Start-menu shortcut created';passed=$shortcut},@{name='Installed edition actual inference and browser chat';status=$acceptance.status},@{name='Application removed by actual uninstaller';passed=$removed},@{name='User-generated data and registry preserved';passed=$preserved},@{name='External original model SHA-256 preserved';passed=$modelPreserved;sha256=$before},@{name='Installed settings stored outside installation directory';passed=$preserved});inference=$acceptance;limitations='Tested under the current Windows account. Standard-user account and other Windows versions were not separately available.'}
$result | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $evidence 'ABH-INSTALLER-TESTS.json')
if($result.status -ne 'VERIFIED'){throw 'Installer acceptance is partial; inspect evidence'}
