#!/usr/bin/env bash
# Removes what install.sh installed. Config and state are kept unless you pass --purge.
# Nothing on Proton Drive is touched.
set -euo pipefail

LIB="$HOME/.local/lib/pdrive"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}"
STATE="${XDG_STATE_HOME:-$HOME/.local/state}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

if [ "${NO_SYSTEMD:-0}" != "1" ] && command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; then
  systemctl --user disable --now pdrive.service 2>/dev/null || true
fi
rm -f "$CONF/systemd/user/pdrive.service" "$CONF/autostart/pdrive-tray.desktop" "$APPS/pdrive.desktop"
[ -L "$HOME/.local/bin/pdrivectl" ] && rm -f "$HOME/.local/bin/pdrivectl"
rm -rf "$LIB"
if [ "${NO_SYSTEMD:-0}" != "1" ] && command -v systemctl >/dev/null; then
  systemctl --user daemon-reload 2>/dev/null || true
fi

if [ "${1:-}" = "--purge" ]; then
  rm -rf "$CONF/pdrive" "$STATE/pdrive"
  echo "Removed pdrive, its config and its state."
else
  echo "Removed pdrive. Kept your settings: $CONF/pdrive and $STATE/pdrive (use --purge to delete them)."
fi
