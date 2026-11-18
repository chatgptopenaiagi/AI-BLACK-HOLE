param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$os=Get-CimInstance Win32_OperatingSystem
$cpu=Get-CimInstance Win32_Processor
$gpu=Get-CimInstance Win32_VideoController
$applications=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -Match 'Ollama|LM Studio|ARX|Python|Miniforge|CUDA|.NET' | Select-Object DisplayName,DisplayVersion,InstallLocation
$smi=Get-Command nvidia-smi -ErrorAction SilentlyContinue
$nvidia=if($smi){ & $smi.Source --query-gpu=name,memory.total,memory.free,driver_version --format=csv,noheader,nounits }else{@()}
$report=[ordered]@{
 schema='abh-machine/1';timestamp_utc=(Get-Date -AsUTC -Format o)
 os=($os | Select-Object Caption,Version,OSArchitecture)
 cpu=($cpu | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
 memory=[ordered]@{total_bytes=[long]$os.TotalVisibleMemorySize*1024;available_bytes=[long]$os.FreePhysicalMemory*1024;pressure_percent=[math]::Round(100*(1-$os.FreePhysicalMemory/$os.TotalVisibleMemorySize),2)}
 gpus=($gpu | Select-Object Name,DriverVersion,AdapterRAM);nvidia_smi=$nvidia
 disks=@(Get-CimInstance Win32_DiskDrive | Select-Object Index,Model,InterfaceType,MediaType,Size)
 volumes=@(Get-Volume | Select-Object DriveLetter,DriveType,FileSystem,Size,SizeRemaining,UniqueId)
 installed_runtimes=@($applications)
 python_commands=@(Get-Command python,python3,py -All -ErrorAction SilentlyContinue | Select-Object Name,Source)
 inference_processes=@(Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -Match 'llama|ollama|kobold|lm-studio|AI-BLACK-HOLE' | Select-Object Id,ProcessName,WorkingSet64)
 acceleration='CPU always candidate; CUDA driver probe is evidence, actual inference required; Vulkan/NPU not verified'
 discovery_scope='CIM, installed application registry, PATH tools, active process names; no unrelated disk scan'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8
