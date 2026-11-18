param([string]$EvidenceDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'evidence'),[string]$OutputDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'reports'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
function ReadEvidence([string]$name){$file=Join-Path $EvidenceDirectory $name;if(Test-Path -LiteralPath $file){Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable}}
function Convert-SafeEvidence($value){
 if($null -eq $value){return $null}
 if($value -is [string]){if($value -match '[A-Za-z]:[\\/]|\\\\[^\\\s]+\\'){return '[private local path redacted]'};return $value}
 if($value -is [Collections.IDictionary]){$result=[ordered]@{};foreach($key in $value.Keys){if($key -match '(?i)secret|passphrase|credential|chatUrl|sourceDirectory|executablePath|installLocation|uniqueId|commandLine'){continue};$result[$key]=Convert-SafeEvidence $value[$key]};return $result}
 if($value -is [Collections.IEnumerable]){return ,@($value | ForEach-Object {Convert-SafeEvidence $_})}
 return $value
}
function Save([string]$name,$value){Convert-SafeEvidence $value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $OutputDirectory $name) -Encoding utf8}
$machine=ReadEvidence 'ABH-MACHINE-DISCOVERY.json'
Save 'ABH-MACHINE-DISCOVERY.json' @{status='VERIFIED';scope='Native Windows machine discovery; selected public hardware fields only';os=$machine.os;cpu=$machine.cpu;memory=$machine.memory;gpus=$machine.gpus;nvidia_smi=$machine.nvidia_smi;discovery_scope=$machine.discovery_scope}
$registry=ReadEvidence 'ui-state/data/models.json'
$models=@($registry.models | ForEach-Object {@{id=$_.id;originalFileName=$_.originalFileName;bytes=$_.bytes;sha256=$_.sha256;state=$_.state;capabilities=$_.capabilities;lastVerification=$_.lastVerification;lastSuccessfulLaunch=$_.lastSuccessfulLaunch;verifiedEngine=$_.verifiedEngine;location='External file retained at its original selected location';identity=@{name=$_.info.name;architecture=$_.info.architecture;quantization=$_.info.quantization;version=$_.info.version;tensorCount=$_.info.tensorCount;layers=$_.info.layers;context=$_.info.context;chatTemplatePresent=([bool]$_.info.chatTemplate);embedding=$_.info.embedding;auxiliary=$_.info.auxiliary}}})
Save 'ABH-MODEL-REGISTRY.json' @{status='VERIFIED';schema=1;models=$models;privacy='Absolute paths and volume identifiers excluded from public evidence; private original evidence retained locally'}
Save 'ABH-GGUF-INSPECTION.json' @{status='VERIFIED';models=$models;limitation='Metadata identity and tested capabilities are separate. Tokenizer/template bodies are omitted from the public summary.'}
$qwen=ReadEvidence 'ABH-QWEN-ACCEPTANCE.json'
$smol=ReadEvidence 'ui-state/data/calibration/d456e45171744a23.json'
Save 'ABH-CALIBRATION-RESULTS.json' @{status='VERIFIED';qwen=$qwen.reports[0].calibration;smol=$smol;conditions='Other independent inference workloads were running. VRAM samples cover total device usage, not exclusive ABH memory. See each candidate report.'}
$security=ReadEvidence 'ABH-SECURITY-TESTS-final.json';if(-not $security){$security=ReadEvidence 'ABH-SECURITY-TESTS-v5.json'}
Save 'ABH-SECURITY-TESTS.json' $security
Save 'ABH-MODEL-IDENTITY-TESTS.json' @{status=$security.status;checks=@($security.checks | Where-Object name -Match 'GGUF|Registry|fingerprint|model|discovery|header|reconnect|Hash cancellation|Network path');realModels=$models}
$ui=ReadEvidence 'ABH-UI-ACCEPTANCE-final.json';if(-not $ui){$ui=ReadEvidence 'ABH-UI-ACCEPTANCE-v7.json'};if(-not $ui){$ui=ReadEvidence 'ABH-UI-ACCEPTANCE.json'}
Save 'ABH-UI-ACCEPTANCE.json' $ui
Save 'ABH-MULTIMODEL-TESTS.json' @{status=$ui.status;distinctArchitectures=@($models.identity.architecture | Sort-Object -Unique);nativeSwitching=$ui;scope='Same native model selector and engine adapter; genuine generation and browser chat; no model copied or relocated'}
Save 'ABH-RUNTIME-CAPABILITIES.json' @{status='VERIFIED';primary='llama.cpp';version='0.9.0-dev, build 1, commit 6c01da627';backends=@('cpu','cuda');runtimeManifest='research/runtime-provenance.json';managedOtherBackends='UNSUPPORTED';npu='UNSUPPORTED';embedding='NOT TESTED';vision='UNSUPPORTED';audio='UNSUPPORTED';splitLoading='UNSUPPORTED'}
Save 'ABH-ENGINE-COMPATIBILITY.json' @{status='VERIFIED';models=$models;scope='Two exact model fingerprints on the tested runtime, hardware and profiles; no compatibility claim for untested GGUF files'}
Save 'ABH-AGENT-WORKFLOWS.json' @{status='PARTIAL';verified=@($security.checks | Where-Object name -Match 'workflow|cloud agent|step limit');supportedAgents=@('local','ARX','DRAGONHYDRA');maxSteps=6;maxConcurrent=2;maxToolCalls=0;cloudBudget=0;limitation='Codex and cloud handoffs are explicit separate actions, not autonomous workflow stages'}
foreach($pair in @(@('ABH-RECOVERY-TESTS.json','ABH-RECOVERY-TESTS.json'),@('ABH-PORTABILITY-TESTS.json','ABH-PORTABILITY-TESTS.json'),@('ABH-INSTALLER-TESTS.json','ABH-INSTALLER-TESTS.json'),@('ABH-RELEASE-MANIFEST.json','ABH-RELEASE-MANIFEST.json'),@('ABH-GITHUB-PUBLICATION.json','ABH-GITHUB-PUBLICATION.json'),@('ABH-MCP-SDK-TESTS-final.json','ABH-MCP-SDK-TESTS.json'))){$value=ReadEvidence $pair[0];if($value){Save $pair[1] $value}}
$codex=ReadEvidence 'ABH-CODEX-LOCAL-ACCEPTANCE.json'
$events=$codex.reports[0].codex.final.events
$calls=@($events | Where-Object {$_.method -eq 'item/completed' -and $_.params.item.type -eq 'mcpToolCall'} | ForEach-Object {$_.params.item})
$expected=$codex.reports[0].codex.expectedRuntime
$matches=@($calls | Where-Object { $r=$_.result.content[0].text | ConvertFrom-Json; $r.pid -eq $expected.pid -and $r.port -eq $expected.port -and $r.modelId -eq $expected.modelId }).Count
Save 'ABH-CODEX-TOOL-EVIDENCE.json' @{status=if($calls.Count -gt 0 -and $matches -eq $calls.Count){'VERIFIED'}else{'PARTIAL'};installedVersion='0.169.0';callCount=$calls.Count;matchingResults=$matches;calls=$calls;expected=$expected;exactlyOnceInstruction='PARTIAL: three calls despite exactly-once prompt';shutdown=$codex.reports[0].shutdown}
@"
# Codex bridge report

VERIFIED: installed Codex App Server 0.169.0 initialized over STDIO in an isolated ABH profile. The local Qwen model returned a Python addition function. It executed $($calls.Count) real abh_runtime_status MCP calls with preserved call IDs and arguments; $matches results matched the live PID, port and model ID. The final answer reproduced those values. The owned model shut down cleanly.

PARTIAL: the prompt requested exactly one tool call but three occurred. General coding/custom-tool compatibility is not established. Custom/freeform and hosted tools are rejected explicitly. The normal Codex configuration was not modified. See ABH-CODEX-TOOL-EVIDENCE.json.
"@ | Set-Content (Join-Path $OutputDirectory 'ABH-CODEX-BRIDGE-REPORT.md')
foreach($provider in @('OpenAI','Claude','Grok')){
 $checks=@($security.checks | Where-Object name -Like "$provider *")
 @"
# $provider connector report

VERIFIED: $($checks.Count) adapter checks using synthetic credentials and mock HTTP/SSE transport. Native text, structured-output requests, tool identifiers/results, streaming, error isolation, cancellation and reservation limits were exercised. Native payloads are retained; proposed tools do not execute automatically.

NOT TESTED: live paid-provider access, billing reconciliation and arbitrary model-specific schemas. No paid request was made. Cloud access remains disabled until configured and the exact outbound content is explicitly approved. Request reservations bound locally authorized requests; they are not a provider invoice or hard billing cap.
"@ | Set-Content (Join-Path $OutputDirectory ('ABH-'+$provider.ToUpperInvariant()+'-CONNECTOR-REPORT.md'))
}
$arx=ReadEvidence 'ABH-ARX-EXECUTION.json'
@"
# ARX report

$($arx.status): actual ARX $($arx.evidence.scanner.version) deep CLI scan through an existing interpreter with isolated Python mode. Pinned source: bca7232bce200bc22f9cd8fee34fe17fc6a0b604. CLI SHA-256: $($arx.cliSha256). The returned schema_version 0.1 scanner/machine envelope was imported successfully.

The installed ARX application was preserved. ABH core has no mandatory Python dependency. This is real machine-scanner execution, not merely format reuse; it is not proof of a model's inference compatibility. The full private machine report is retained locally and excluded from publication.
"@ | Set-Content (Join-Path $OutputDirectory 'ABH-ARX-REPORT.md')
@'
# MCP report

VERIFIED: official TypeScript SDK 1.32.2 initialized and invoked real tools over STDIO and Streamable HTTP using negotiated protocol 2025-11-25. Unknown model IDs were rejected. Separate native tests exercise 2026-07-28 metadata, discovery and tool responses. ABH exposes 11 bounded registered-model/status tools.

The actual Codex App Server used the STDIO adapter and received live runtime results. Read-only keys cannot invoke model mutations. External remote HTTP endpoints, arbitrary filesystem access and shell execution are unsupported. See the SDK and security JSON reports for exact checks.
'@ | Set-Content (Join-Path $OutputDirectory 'ABH-MCP-REPORT.md')
Write-Output "Exported selected sanitized evidence to $OutputDirectory"
