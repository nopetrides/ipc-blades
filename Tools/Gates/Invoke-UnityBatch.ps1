# Runs the pinned Unity editor in batch mode against this project and waits for the Unity
# process itself (Start-Process -Wait would also wait for helper processes Unity spawns).
# Example: .\Tools\Gates\Invoke-UnityBatch.ps1 -Method IPCBlades.Gates.RigidIPCGateProbe.LogStatusMenu -LogName gate_run
param(
    [Parameter(Mandatory)] [string] $Method,
    [Parameter(Mandatory)] [string] $LogName,
    [string] $Unity = "K:\Unity\6000.4.4f1\Editor\Unity.exe"
)
$project = (Resolve-Path "$PSScriptRoot\..\..").Path
$log = Join-Path $project "Logs\$LogName.log"
$args = @('-batchmode', '-quit', '-projectPath', "`"$project`"", '-executeMethod', $Method, '-logFile', "`"$log`"")
$p = Start-Process -FilePath $Unity -ArgumentList $args -PassThru
$p.WaitForExit()
"unity exit code: $($p.ExitCode)  log: $log"
