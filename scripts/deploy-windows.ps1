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
$binaryPath = "`"$exe`" --urls http://0.0.0.0:$Port --DataRoot `"$(Join-Path $InstallDirectory 'data')`""
if ($existing) {
    Set-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $binaryPath
    Set-Service -Name $ServiceName -StartupType Automatic
} else {
    New-Service -Name $ServiceName -BinaryPathName $binaryPath -DisplayName 'Software Release Manager' -Description 'SVN software build and release management platform' -StartupType Automatic | Out-Null
}
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/""/0 | Out-Null
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Deployment completed. Open http://localhost:$Port" -ForegroundColor Green
