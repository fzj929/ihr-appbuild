#!/usr/bin/env bash
set -euo pipefail

[[ "${EUID}" -eq 0 ]] || { echo 'Please run as root (sudo).'; exit 1; }
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALL_DIR="${INSTALL_DIR:-/opt/release-manager}"
DATA_DIR="${DATA_DIR:-/var/lib/release-manager}"
SERVICE_NAME="${SERVICE_NAME:-release-manager}"
PORT="${PORT:-5088}"

if [[ "${SKIP_BUILD:-0}" != '1' ]]; then sudo -u "${SUDO_USER:-root}" "$SCRIPT_DIR/build.sh"; fi
[[ -f "$ROOT/artifacts/latest-build.txt" ]] || { echo 'Latest build pointer not found. Run build.sh first.'; exit 1; }
ARTIFACT="$(tr -d '\r\n' < "$ROOT/artifacts/latest-build.txt")"
[[ -f "$ARTIFACT/ReleaseManager.Api.dll" ]] || { echo "Build artifact not found: $ARTIFACT"; exit 1; }
systemctl stop "$SERVICE_NAME" 2>/dev/null || true
id release-manager &>/dev/null || useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin release-manager
install -d -o release-manager -g release-manager -m 0750 "$DATA_DIR"
rm -rf -- "$INSTALL_DIR"
install -d -o root -g root -m 0755 "$INSTALL_DIR"
cp -a "$ARTIFACT/." "$INSTALL_DIR/"
sed -e "s|/opt/release-manager|$INSTALL_DIR|g" -e "s|/var/lib/release-manager|$DATA_DIR|g" -e "s|:5088|:$PORT|g" "$SCRIPT_DIR/nginx-manager.service" > "/etc/systemd/system/$SERVICE_NAME.service"
systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"
systemctl --no-pager --full status "$SERVICE_NAME"
echo "Deployment completed. Open http://localhost:$PORT"
