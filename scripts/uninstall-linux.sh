#!/usr/bin/env bash
set -euo pipefail
[[ "${EUID}" -eq 0 ]] || { echo 'Please run as root (sudo).'; exit 1; }
SERVICE_NAME="${SERVICE_NAME:-release-manager}"
INSTALL_DIR="${INSTALL_DIR:-/opt/release-manager}"
DATA_DIR="${DATA_DIR:-/var/lib/release-manager}"
systemctl disable --now "$SERVICE_NAME" 2>/dev/null || true
rm -f -- "/etc/systemd/system/$SERVICE_NAME.service"
systemctl daemon-reload
rm -rf -- "$INSTALL_DIR"
if [[ "${1:-}" == '--purge-data' ]]; then rm -rf -- "$DATA_DIR"; userdel release-manager 2>/dev/null || true; echo 'Service, application and data removed.'; else echo "Service and application removed; data retained at $DATA_DIR"; fi
