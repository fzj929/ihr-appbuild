[CmdletBinding()]
param([string]$ServiceName = 'ReleaseManager')
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Running') { Start-Service -Name $ServiceName; $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
Write-Host "$ServiceName is running." -ForegroundColor Green
