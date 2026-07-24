[CmdletBinding()]
param(
    [string]$ServiceName = 'ReleaseManager',
    [string]$InstallDirectory = "$env:ProgramData\ReleaseManager",
    [int]$Port = 5088,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Please run this script as Administrator.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1'); if ($LASTEXITCODE -ne 0) { throw 'Build failed.' } }
$latestBuildFile = Join-Path $root 'artifacts\latest-build.txt'
if (-not (Test-Path $latestBuildFile)) { throw 'Latest build pointer not found. Run build.ps1 first.' }
$artifact = (Get-Content -LiteralPath $latestBuildFile -Encoding UTF8 -Raw).Trim()
if (-not (Test-Path (Join-Path $artifact 'ReleaseManager.Api.exe'))) { throw "Build artifact not found: $artifact" }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $InstallDirectory -Force | Where-Object Name -ne 'data' | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $artifact '*') -Destination $InstallDirectory -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $InstallDirectory 'data') -Force | Out-Null

$exe = Join-Path $InstallDirectory 'ReleaseManager.Api.exe'
$dataDirectory = Join-Path $InstallDirectory 'data'
$binaryPath = "`"$exe`" --urls=http://0.0.0.0:$Port `"--DataRoot=$dataDirectory`" `"--contentRoot=$InstallDirectory`" `"--ServiceName=$ServiceName`""
if ($existing) {
    Set-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $binaryPath
    Set-Service -Name $ServiceName -StartupType Automatic
} else {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'Software Release Manager' -Description 'SVN software build and release management platform' -StartupType Automatic | Out-Null
}
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/""/0 | Out-Null
$listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    $ownerName = if ($owner) { $owner.ProcessName } else { 'unknown' }
    throw "Port $Port is already in use by PID $($listener.OwningProcess) ($ownerName). Stop that process or deploy with another -Port."
}
$startedAt = Get-Date
try {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
} catch {
    Write-Host "`nService failed to start. Diagnostic details:" -ForegroundColor Red
    & sc.exe queryex $ServiceName
    & sc.exe qc $ServiceName
    $startupLog = Join-Path $dataDirectory 'logs\service-startup.log'
    if (Test-Path $startupLog) {
        Write-Host "`nApplication startup log ($startupLog):" -ForegroundColor Yellow
        Get-Content -LiteralPath $startupLog -Encoding UTF8 -Tail 80
    }
    Write-Host "`nRecent Windows service/application events:" -ForegroundColor Yellow
    $events = @()
    $events += Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $startedAt.AddMinutes(-1) } -ErrorAction SilentlyContinue
    $events += Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt.AddMinutes(-1) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error') -or $_.Message -match $ServiceName }
    if ($events.Count -gt 0) { $events | Sort-Object TimeCreated -Descending | Select-Object -First 10 TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List | Out-String | Write-Host }
    else { Write-Host 'No related Windows events were found.' }
    throw
}
Write-Host "Deployment completed. Open http://localhost:$Port" -ForegroundColor Green
