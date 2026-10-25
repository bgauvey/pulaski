<#
.SYNOPSIS
    Installs Pulaski Edge as a Windows service (#25).

.DESCRIPTION
    Registers the published Pulaski.Edge.exe as a service that starts with Windows (delayed, after the network), runs as
    LocalService, keeps its buffer under ProgramData and restarts itself after a failure: 10 s, then 30 s, then every 60 s,
    the count resetting after a day. Run from an elevated PowerShell. Configure the edge in appsettings.json next to the
    program (or with Edge__* environment variables) before starting it.

.EXAMPLE
    .\install-service.ps1 -Path "C:\Program Files\Pulaski Edge\Pulaski.Edge.exe"
    Start-Service PulaskiEdge
#>
param(
    [Parameter(Mandatory = $true)][string] $Path,
    [string] $ServiceName = "PulaskiEdge",
    [string] $DataDirectory = "$env:ProgramData\Pulaski Edge"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) { throw "No program at $Path. Publish it first: dotnet publish src/Pulaski.Edge -c Release -r win-x64 --self-contained" }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "A service named $ServiceName exists already; run uninstall-service.ps1 first." }

# The buffer must survive restarts and be writable by the service account; Program Files isn't.
New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
$acl = Get-Acl $DataDirectory
$acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\LocalService", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $DataDirectory $acl

New-Service -Name $ServiceName -BinaryPathName "`"$Path`"" -DisplayName "Pulaski Edge" `
    -Description "Reads machines, buffers what it reads and forwards it to Pulaski." `
    -StartupType AutomaticDelayedStart | Out-Null
sc.exe config $ServiceName obj= "NT AUTHORITY\LocalService" password= "" | Out-Null

# Environment for the service only: where the buffer and the OPC UA certificates live.
$key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $key -Name Environment -PropertyType MultiString -Force `
    -Value @("Edge__BufferPath=$DataDirectory\pulaski-edge.db", "Edge__PkiPath=$DataDirectory\pki", "DOTNET_ENVIRONMENT=Production") | Out-Null

# Recovery: restart after a crash or a non-zero exit (Pulaski Edge exits 1 when a worker fails).
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Write-Host "Installed $ServiceName. Edit appsettings.json next to $Path, then: Start-Service $ServiceName"
Write-Host "It logs to the Windows Event Log (Application, source $ServiceName)."
