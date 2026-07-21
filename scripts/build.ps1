[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ("artifacts\packages\{0}" -f (Get-Date -Format 'yyyyMMddHHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$web = Join-Path $root 'src\release-manager-web'
$api = Join-Path $root 'src\ReleaseManager.Api\ReleaseManager.Api.csproj'

Write-Host '[1/4] Restoring .NET dependencies...' -ForegroundColor Cyan
dotnet restore $api
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

Write-Host '[2/4] Installing frontend dependencies...' -ForegroundColor Cyan
Push-Location $web
try {
    if (Test-Path 'package-lock.json') { & npm.cmd ci } else { Write-Warning 'package-lock.json not found; using npm install.'; & npm.cmd install }
    if ($LASTEXITCODE -ne 0) { throw 'npm dependency installation failed.' }
    Write-Host '[3/4] Building Vue frontend...' -ForegroundColor Cyan
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'Vue build failed.' }
} finally { Pop-Location }

Write-Host '[4/4] Publishing .NET backend...' -ForegroundColor Cyan
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
dotnet publish $api -c $Configuration -f net8.0 -o $OutputDirectory --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed. Stop the installed service or running ReleaseManager process, then retry.' }

Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-info.txt') -Encoding UTF8 -Value @(
    "BuiltAtUtc=$([DateTime]::UtcNow.ToString('O'))"
    "Configuration=$Configuration"
    "Framework=net8.0"
)
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'artifacts\latest-build.txt') -Encoding UTF8 -Value $OutputDirectory
Write-Host "Build completed: $OutputDirectory" -ForegroundColor Green
