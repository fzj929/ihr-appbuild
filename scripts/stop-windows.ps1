[CmdletBinding()]
param([string]$ServiceName = 'ReleaseManager')
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
Write-Host "$ServiceName is stopped." -ForegroundColor Yellow
