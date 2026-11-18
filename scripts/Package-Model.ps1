param([Parameter(Mandatory)][string]$CoreDirectory,[Parameter(Mandatory)][string]$ModelFile,[Parameter(Mandatory)][string]$Destination,[switch]$CreateZip)
$ErrorActionPreference='Stop'
$source=[IO.Path]::GetFullPath($ModelFile)
$core=[IO.Path]::GetFullPath($CoreDirectory)
$target=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $target){throw 'Use a new destination; existing files are never overwritten.'}
if(-not (Test-Path -LiteralPath (Join-Path $core 'AI-BLACK-HOLE.exe'))){throw 'Select a built and verified ABH core.'}
if((Get-ChildItem -LiteralPath (Join-Path $core 'data') -File -Recurse -ErrorAction SilentlyContinue).Count -gt 0){throw 'Select a clean distribution core, without credentials or user state.'}
$info=Get-Item -LiteralPath $source
$before=[ordered]@{bytes=$info.Length;mtime=$info.LastWriteTimeUtc;sha256=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()}
if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($target))).AvailableFreeSpace -lt ($before.bytes+2GB)){throw 'Insufficient destination free space.'}
Copy-Item -LiteralPath $core -Destination $target -Recurse
$models=Join-Path $target 'models'
New-Item -ItemType Directory -Path $models -Force | Out-Null
$copy=Join-Path $models 'selected-model.gguf'
Copy-Item -LiteralPath $source -Destination $copy
if((Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant() -ne $before.sha256){throw 'Model copy checksum mismatch.'}
$exe=Join-Path $target 'AI-BLACK-HOLE.exe'
$profile=Join-Path $target 'model-profile.json'
$process=Start-Process -FilePath $exe -ArgumentList @('--register',('"'+$copy+'"'),'--output',('"'+$profile+'"')) -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw 'Copied model registration failed.'}
$model=Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json
$process=Start-Process -FilePath $exe -ArgumentList @('--verify',$model.id,'--output',('"'+(Join-Path $target 'model-integrity.json')+'"')) -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw 'Copied model verification failed.'}
$after=Get-Item -LiteralPath $source
if($after.Length -ne $before.bytes -or $after.LastWriteTimeUtc -ne $before.mtime -or (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $before.sha256){throw 'Source preservation check failed.'}
[ordered]@{schema='abh-model-package/1';timestamp=(Get-Date -AsUTC -Format o);model_sha256=$before.sha256;model_bytes=$before.bytes;source_preserved=$true;runtime_acceptance='NOT TESTED; run --acceptance against the copied model before distribution'} | ConvertTo-Json | Set-Content (Join-Path $target 'model-package.json')
if($CreateZip){
 $zipTool=Get-Command 7z -ErrorAction SilentlyContinue
 if(-not $zipTool){$zipPath=Join-Path $env:ProgramFiles '7-Zip\7z.exe';if(Test-Path -LiteralPath $zipPath){$zipTool=Get-Item -LiteralPath $zipPath}}
 if(-not $zipTool){throw 'Package created. ZIP64 archiving requires 7-Zip; no archive was created.'}
 $zipExe=if($zipTool.Source){$zipTool.Source}else{$zipTool.FullName}
 & $zipExe a -tzip -mx=1 ($target+'.zip') (Join-Path $target '*')
 if($LASTEXITCODE -ne 0){throw 'ZIP64 creation failed'}
}
Write-Output "Created optional model-inclusive package: $target. Runtime acceptance is still required."
