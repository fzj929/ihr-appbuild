#!/usr/bin/env bash
set -euo pipefail
SERVICE_NAME="${SERVICE_NAME:-release-manager}"
[[ "${EUID}" -eq 0 ]] || { echo 'Please run as root (sudo).'; exit 1; }
systemctl stop "$SERVICE_NAME"
echo "$SERVICE_NAME is stopped."
