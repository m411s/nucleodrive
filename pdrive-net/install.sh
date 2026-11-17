#!/usr/bin/env bash
# Builds and installs pdrive for the current user (no root needed):
#   ~/.local/lib/pdrive/                      daemon, pdrivectl and app (self-contained .NET, one shared folder)
#   ~/.local/bin/pdrivectl                    symlink
#   ~/.config/systemd/user/pdrive.service     backup daemon (Type=notify)
#   ~/.config/autostart/pdrive-tray.desktop   tray at login (pdrive --tray)
#   ~/.local/share/applications/pdrive.desktop
#
# Options (environment):
#   NO_SYSTEMD=1   don't touch systemd (e.g. no user session, or testing with a fake HOME)
#   NO_BUILD=1     reuse an existing publish in $LIB (only rewrite the unit/desktop files)
#
# Config and state (kept across reinstalls): ~/.config/pdrive, ~/.local/state/pdrive.
# On first start the daemon imports the Python prototype's config and state (proton-drive-gui).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIB="$HOME/.local/lib/pdrive"
BIN="$HOME/.local/bin"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

case "$(uname -m)" in
  x86_64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

have_systemd() {
  [ "${NO_SYSTEMD:-0}" != "1" ] && command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1
}

ask() { # ask "question" -> 0 for yes; defaults to no when not interactive
  local reply
  [ -t 0 ] || return 1
  read -r -p "$1 [y/N] " reply
  [[ "$reply" =~ ^[Yy] ]]
}

# --- the Python prototype -------------------------------------------------------
# Two daemons uploading the same folders would create duplicate work (and possibly duplicates on
# Drive), so offer to stop the old one. Its config/state stay where they are and get imported.
if have_systemd && systemctl --user cat proton-drive-sync.service >/dev/null 2>&1; then
  if ask "The Python backup service (proton-drive-sync) is installed. Stop and disable it?"; then
    systemctl --user disable --now proton-drive-sync.service
    echo "Stopped proton-drive-sync. (Unit file left in place; remove it when you're happy.)"
  else
    echo "WARNING: proton-drive-sync is still enabled. Don't run both daemons on the same folders." >&2
  fi
fi
OLD_TRAY="$CONF/autostart/proton-drive-tray.desktop"
if [ -f "$OLD_TRAY" ] && ask "Disable the Python tray's autostart entry ($OLD_TRAY)?"; then
  mv "$OLD_TRAY" "$OLD_TRAY.disabled"
  echo "Renamed to $OLD_TRAY.disabled"
fi

# --- build -------------------------------------------------------------------------
if [ "${NO_BUILD:-0}" != "1" ]; then
  command -v dotnet >/dev/null || { echo "dotnet SDK (10.0) not found; install it first." >&2; exit 1; }
  STAGE="$(mktemp -d)"
  trap 'rm -rf "$STAGE"' EXIT
  for proj in Pdrive.Daemon Pdrive.Ctl Pdrive.App; do
    echo "Publishing $proj ($RID)…"
    dotnet publish "$DIR/src/$proj/$proj.csproj" -c Release -r "$RID" --self-contained \
      -p:DebugType=none -p:GenerateDocumentationFile=false -o "$STAGE" --nologo -v quiet
  done
  if have_systemd; then systemctl --user stop pdrive.service 2>/dev/null || true; fi
  rm -rf "$LIB.old"
  [ -d "$LIB" ] && mv "$LIB" "$LIB.old"
  mkdir -p "$(dirname "$LIB")"
  mv "$STAGE" "$LIB"
  chmod 755 "$LIB"
  trap - EXIT
  rm -rf "$LIB.old"
fi
[ -x "$LIB/pdrive-daemon" ] || { echo "Nothing published in $LIB" >&2; exit 1; }

# --- files -------------------------------------------------------------------------
mkdir -p "$BIN" "$CONF/systemd/user" "$CONF/autostart" "$APPS"
ln -sf "$LIB/pdrivectl" "$BIN/pdrivectl"

cat > "$CONF/systemd/user/pdrive.service" <<UNIT
[Unit]
Description=Proton Drive one-way backup (unofficial)

[Service]
Type=notify
ExecStart="$LIB/pdrive-daemon"
Restart=on-failure
RestartSec=30
TimeoutStopSec=60

[Install]
WantedBy=default.target
UNIT

cat > "$CONF/autostart/pdrive-tray.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Proton Drive Backup (tray)
Exec="$LIB/pdrive" --tray
Icon=folder-remote
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESK

cat > "$APPS/pdrive.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Proton Drive (unofficial)
Comment=Browse Proton Drive and manage folder backups
Exec="$LIB/pdrive"
Icon=folder-remote
Categories=Network;FileTransfer;
StartupWMClass=pdrive
DESK

if have_systemd; then
  systemctl --user daemon-reload
  systemctl --user enable --now pdrive.service
  echo "Daemon started:  systemctl --user status pdrive   (logs: journalctl --user -u pdrive)"
else
  echo "systemd not used; start the daemon yourself: \"$LIB/pdrive-daemon\""
fi

case ":$PATH:" in *":$BIN:"*) ;; *) echo "Note: $BIN is not on your PATH." ;; esac
echo "Next:  pdrivectl add ~/Documents /my-files/Documents   (or use the Backup tab in the app)"
echo "Tray starts at next login, or now: \"$LIB/pdrive\" --tray &"
