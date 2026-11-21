#!/usr/bin/env bash
# Removes what install.sh installed. Config and state are kept unless you pass --purge.
# Nothing on Proton Drive is touched.
set -euo pipefail

LIB="$HOME/.local/lib/nucleodrive"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}"
STATE="${XDG_STATE_HOME:-$HOME/.local/state}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"

if [ "${NO_SYSTEMD:-0}" != "1" ] && command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; then
  systemctl --user disable --now nucleodrive.service 2>/dev/null || true
fi
rm -f "$CONF/systemd/user/nucleodrive.service" "$CONF/autostart/nucleodrive-tray.desktop" "$APPS/nucleodrive.desktop"
[ -L "$HOME/.local/bin/nucleodrivectl" ] && rm -f "$HOME/.local/bin/nucleodrivectl"
rm -rf "$LIB"
rm -f "$ICONS/scalable/apps/io.github.m411s.nucleodrive.svg" "$ICONS"/*/apps/io.github.m411s.nucleodrive.png
DATA="${XDG_DATA_HOME:-$HOME/.local/share}/nucleodrive"
rm -rf "$DATA/icons"
# The CLI installed from Settings stays (it's useful on its own) unless --purge.
if [ "${1:-}" = "--purge" ]; then
  [ "$(readlink "$HOME/.local/bin/proton-drive" 2>/dev/null)" = "$DATA/cli/proton-drive" ] && rm -f "$HOME/.local/bin/proton-drive"
  rm -rf "$DATA"
fi
if [ "${NO_SYSTEMD:-0}" != "1" ] && command -v systemctl >/dev/null; then
  systemctl --user daemon-reload 2>/dev/null || true
fi

if [ "${1:-}" = "--purge" ]; then
  rm -rf "$CONF/nucleodrive" "$STATE/nucleodrive"
  echo "Removed NucleoDrive, its config and its state."
else
  echo "Removed NucleoDrive. Kept your settings: $CONF/nucleodrive and $STATE/nucleodrive (use --purge to delete them)."
fi
