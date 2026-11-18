param([Parameter(Mandatory)][string]$CoreDirectory,[Parameter(Mandatory)][string]$RelocatedDirectory,[Parameter(Mandatory)][string]$EvidenceDirectory,[Parameter(Mandatory)][string]$PreparedState)
$ErrorActionPreference='Stop'
$core=[IO.Path]::GetFullPath($CoreDirectory)
$relocated=[IO.Path]::GetFullPath($RelocatedDirectory)
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $relocated){throw 'Relocation test requires a new destination.'}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
function Invoke-Abh([string]$Executable,[string[]]$Arguments,[string]$Name,[switch]$Restricted){
 $info=[Diagnostics.ProcessStartInfo]::new($Executable)
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 foreach($arg in $Arguments){$info.ArgumentList.Add($arg)}
 if($Restricted){$info.Environment['PATH']=$env:SystemRoot+'\System32;'+$env:SystemRoot;$info.Environment['DOTNET_ROOT']=Join-Path $evidence 'nonexistent-dotnet';$info.Environment['DOTNET_MULTILEVEL_LOOKUP']='0';$info.Environment['PYTHONHOME']=Join-Path $evidence 'nonexistent-python';$info.Environment.Remove('PYTHONPATH') | Out-Null}
 $process=[Diagnostics.Process]::Start($info);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
 $until=(Get-Date).AddMinutes(12)
 while(-not $process.HasExited){if((Get-Date) -gt $until){$process.Kill($true);throw "$Name exceeded its test deadline"};Start-Sleep -Milliseconds 500}
 $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence ($Name+'.stdout.log'))
 $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence ($Name+'.stderr.log'))
 if($process.ExitCode -ne 0){throw "$Name failed: exit $($process.ExitCode). See the isolated stderr/report."}
 Write-Output "${Name}: VERIFIED process exit"
}
$exe=Join-Path $core 'AI-BLACK-HOLE.exe'
$binary=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
Invoke-Abh $exe @('--self-test','--state',(Join-Path $evidence 'final-unit-state'),'--output',(Join-Path $evidence 'ABH-SECURITY-TESTS-final.json')) 'selftest'
$sdk=Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\mcp-client.mjs'
Invoke-Abh $exe @('--protocol-test',$sdk,'--state',(Join-Path $evidence 'final-mcp-state'),'--output',(Join-Path $evidence 'ABH-MCP-SDK-TESTS-final.json')) 'mcp-sdk'
Copy-Item -LiteralPath $core -Destination $relocated -Recurse
$copy=Join-Path $relocated 'AI-BLACK-HOLE.exe'
if((Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant() -ne $binary){throw 'Relocated executable fingerprint mismatch'}
$testState=Join-Path $evidence 'final-ui-state'
New-Item -ItemType Directory -Path (Join-Path $testState 'data\calibration') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PreparedState 'data\models.json') -Destination (Join-Path $testState 'data\models.json')
Get-ChildItem -LiteralPath (Join-Path $PreparedState 'data\calibration') -File -Filter '*.json' | Copy-Item -Destination (Join-Path $testState 'data\calibration')
Invoke-Abh $copy @('--ui-test','--state',$testState,'--output',(Join-Path $evidence 'ABH-UI-ACCEPTANCE-final.json')) 'relocated-native' -Restricted
$ui=Get-Content -LiteralPath (Join-Path $evidence 'ABH-UI-ACCEPTANCE-final.json') -Raw | ConvertFrom-Json
if($ui.status -ne 'VERIFIED'){throw 'Relocated native acceptance did not pass'}
$coreDisk=Get-Partition -DriveLetter ([IO.Path]::GetPathRoot($core)[0]) | Select-Object -First 1 -ExpandProperty DiskNumber
$targetDisk=Get-Partition -DriveLetter ([IO.Path]::GetPathRoot($relocated)[0]) | Select-Object -First 1 -ExpandProperty DiskNumber
[ordered]@{status='VERIFIED';timestamp=(Get-Date -AsUTC -Format o);executable_sha256=$binary;build=(Get-Content (Join-Path $core 'build-info.json') -Raw | ConvertFrom-Json);checks=@(@{name='Self-contained core synthetic and API tests';status='VERIFIED'},@{name='Official MCP SDK transports';status='VERIFIED'},@{name='Executable hash after relocation';status='VERIFIED'},@{name='Different fixed physical disk';status=if($coreDisk -ne $targetDisk){'VERIFIED'}else{'PARTIAL'};source_disk=$coreDisk;target_disk=$targetDisk},@{name='Directory containing spaces';status=if($relocated.Contains(' ')){'VERIFIED'}else{'NOT TESTED'}},@{name='Two external models through relocated native and browser interfaces';status=$ui.status},@{name='No Python or dotnet on child PATH, nonexistent DOTNET_ROOT/PYTHONHOME';status='VERIFIED';limitation='Host installations were not uninstalled; this verifies self-contained execution in an isolated environment'},@{name='Physical removable USB media';status='NOT TESTED';reason='No removable media available'});model_locations='Original external models retained on separate drives; no original weights copied';usb_equivalence_claim=$false} | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence 'ABH-PORTABILITY-TESTS.json')
