[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$ServiceName = 'ReleaseManager',
    [string]$InstallDirectory = "$env:ProgramData\ReleaseManager",
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Please run this script as Administrator.' }
if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Stop and remove Windows service')) { return }
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) { if ($service.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }; & sc.exe delete $ServiceName | Out-Null }
if (Test-Path $InstallDirectory) {
    Get-ChildItem -LiteralPath $InstallDirectory -Force | Where-Object { $RemoveData -or $_.Name -ne 'data' } | Remove-Item -Recurse -Force
    if ($RemoveData) { Remove-Item -LiteralPath $InstallDirectory -Force -ErrorAction SilentlyContinue }
}
Write-Host ($(if ($RemoveData) { 'Service and data removed.' } else { "Service removed; data retained at $InstallDirectory\data" })) -ForegroundColor Yellow
