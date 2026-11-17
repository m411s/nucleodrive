#!/usr/bin/env bash
# Installs: systemd user service (backup daemon), tray autostart entry, app launcher.
# Set SKIP_PIP=1 to skip creating the venv (then PYTHON must have pystray+pillow+gi).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV="${XDG_DATA_HOME:-$HOME/.local/share}/proton-drive-gui/venv"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}"

if [ "${SKIP_PIP:-0}" = "1" ]; then
  PY="${PYTHON:-python3}"
else
  # --system-site-packages keeps access to the distro's python3-gi for the GTK app
  python3 -m venv --system-site-packages "$VENV"
  "$VENV/bin/pip" install --quiet pystray pillow
  PY="$VENV/bin/python"
fi

mkdir -p "$CONF/systemd/user" "$CONF/autostart" "$HOME/.local/share/applications"

cat > "$CONF/systemd/user/proton-drive-sync.service" <<UNIT
[Unit]
Description=Proton Drive one-way backup (unofficial)
After=network-online.target

[Service]
ExecStart=$PY $DIR/syncd.py run
WorkingDirectory=$DIR
Restart=on-failure
RestartSec=30

[Install]
WantedBy=default.target
UNIT

cat > "$CONF/autostart/proton-drive-tray.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Proton Drive Backup (tray)
Exec=$PY $DIR/tray.py
Path=$DIR
X-GNOME-Autostart-enabled=true
DESK

cat > "$HOME/.local/share/applications/proton-drive-gui.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Proton Drive (unofficial)
Exec=$PY $DIR/app.py
Path=$DIR
Icon=folder-remote
Categories=Network;FileTransfer;
DESK

if command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1; then
  systemctl --user daemon-reload
  systemctl --user enable --now proton-drive-sync.service
  echo "Service started. Status: systemctl --user status proton-drive-sync"
else
  echo "systemd user session not found; start manually: $PY $DIR/syncd.py run"
fi
echo "Next: $PY $DIR/syncd.py add ~/Documents /my-files/Documents"
echo "Tray starts at next login, or now: $PY $DIR/tray.py &"
