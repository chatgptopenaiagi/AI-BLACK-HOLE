param([string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\dev'))
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
if($outputRoot -eq [IO.Path]::GetPathRoot($outputRoot)){throw 'Use a dedicated output directory.'}
$env:DOTNET_CLI_HOME=Join-Path $root '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $root
try {
 if(Get-Command node -ErrorAction SilentlyContinue){ node --check web/app.js; if($LASTEXITCODE -ne 0){throw 'Browser JavaScript syntax check failed'} }
 dotnet restore src\AIBlackHole.csproj --locked-mode --runtime win-x64
 if($LASTEXITCODE -ne 0){throw 'Restore failed'}
 dotnet publish src\AIBlackHole.csproj -c Release --no-restore -o $outputRoot
 if($LASTEXITCODE -ne 0){throw 'Publish failed'}
 foreach($folder in @('web','docs','scripts','runtime','reports')) {
  $source=Join-Path $root $folder
  $target=Join-Path $outputRoot $folder
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  if(-not (Test-Path -LiteralPath $source)){continue}
  if($folder -eq 'runtime') { robocopy $source $target /E /XO /NFL /NDL /NJH /NJS /NP | Out-Null; if($LASTEXITCODE -ge 8){throw 'Runtime copy failed'} }
  else { Get-ChildItem -LiteralPath $source | Copy-Item -Destination $target -Recurse -Force }
 }
 Get-ChildItem -LiteralPath $root -File -Filter '*.md' | Copy-Item -Destination (Join-Path $outputRoot 'docs') -Force
 if(Test-Path LICENSE){Copy-Item LICENSE (Join-Path $outputRoot 'LICENSE.txt') -Force}
 $manifest=@(Get-ChildItem -LiteralPath (Join-Path $outputRoot 'runtime') -Recurse -File | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($outputRoot,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
 $manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outputRoot 'runtime-manifest.json')
 [ordered]@{version='2.0.0-beta.2';timestamp_utc=(Get-Date -AsUTC -Format o);source_commit=(git rev-parse HEAD);source_dirty=([bool](git status --porcelain));executable_sha256=(Get-FileHash (Join-Path $outputRoot 'AI-BLACK-HOLE.exe')).Hash.ToLowerInvariant();sdk=(dotnet --version);self_contained=$true} | ConvertTo-Json | Set-Content (Join-Path $outputRoot 'build-info.json')
}finally{Pop-Location}
Write-Output "Built $outputRoot; execution acceptance remains required."
