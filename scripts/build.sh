#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_DIR="${OUTPUT_DIR:-$ROOT/artifacts/packages/$(date +%Y%m%d%H%M%S)}"
WEB="$ROOT/src/release-manager-web"
API="$ROOT/src/ReleaseManager.Api/ReleaseManager.Api.csproj"

echo '[1/4] Restoring .NET dependencies...'
dotnet restore "$API"
echo '[2/4] Installing frontend dependencies...'
cd "$WEB"
if [[ -f package-lock.json ]]; then npm ci; else echo 'WARNING: package-lock.json not found; using npm install.'; npm install; fi
echo '[3/4] Building Vue frontend...'
npm run build
echo '[4/4] Publishing .NET backend...'
mkdir -p "$OUTPUT_DIR"
dotnet publish "$API" -c "$CONFIGURATION" -f net8.0 -o "$OUTPUT_DIR" --no-restore
printf 'BuiltAtUtc=%s\nConfiguration=%s\nFramework=net8.0\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$CONFIGURATION" > "$OUTPUT_DIR/build-info.txt"
mkdir -p "$ROOT/artifacts"
printf '%s\n' "$OUTPUT_DIR" > "$ROOT/artifacts/latest-build.txt"
echo "Build completed: $OUTPUT_DIR"
