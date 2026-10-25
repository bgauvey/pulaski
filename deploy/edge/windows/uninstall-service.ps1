<#
.SYNOPSIS
    Stops and removes the Pulaski Edge Windows service (#25). The buffer under ProgramData is kept: anything in it not yet
    forwarded is sent once an edge with the same buffer runs again.
#>
param([string] $ServiceName = "PulaskiEdge")

$ErrorActionPreference = "Stop"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) { Write-Host "No service named $ServiceName."; return }
if ($service.Status -ne "Stopped") { Stop-Service -Name $ServiceName; $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30)) }
sc.exe delete $ServiceName | Out-Null
Write-Host "Removed $ServiceName."
