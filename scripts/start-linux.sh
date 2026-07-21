#!/usr/bin/env bash
set -euo pipefail
SERVICE_NAME="${SERVICE_NAME:-release-manager}"
[[ "${EUID}" -eq 0 ]] || { echo 'Please run as root (sudo).'; exit 1; }
systemctl start "$SERVICE_NAME"
systemctl --no-pager --full status "$SERVICE_NAME"
