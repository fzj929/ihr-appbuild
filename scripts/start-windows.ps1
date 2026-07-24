[CmdletBinding()]
param([string]$ServiceName = 'ReleaseManager', [string]$InstallDirectory = "$env:ProgramData\ReleaseManager")
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name $ServiceName -ErrorAction Stop
if ($service.Status -ne 'Running') {
    try { Start-Service -Name $ServiceName; $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }
    catch {
        $startupLog = Join-Path $InstallDirectory 'data\logs\service-startup.log'
        if (Test-Path $startupLog) { Write-Host "Startup log: $startupLog" -ForegroundColor Yellow; Get-Content -LiteralPath $startupLog -Encoding UTF8 -Tail 80 }
        & sc.exe queryex $ServiceName
        throw
    }
}
Write-Host "$ServiceName is running." -ForegroundColor Green
