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
#   NO_TRAY=1      don't start the tray now (it still starts at login)
#
# Config and state (kept across reinstalls): ~/.config/pdrive, ~/.local/state/pdrive.
# On first start the daemon imports the Python prototype's config and state (proton-drive-gui).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIB="$HOME/.local/lib/pdrive"
BIN="$HOME/.local/bin"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"

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
# These paths go into a systemd unit and desktop entries, where ", %, \ and line breaks change meaning.
case "$LIB" in
  *[\"%\\]*|*"
"*) echo "Unsupported characters in $LIB (\", %, \\ or a line break); install somewhere else." >&2; exit 1 ;;
esac
mkdir -p "$BIN" "$CONF/systemd/user" "$CONF/autostart" "$APPS"
ln -sf "$LIB/pdrivectl" "$BIN/pdrivectl"

# App icon (Icon=io.github.m411s.pdrive): scalable SVG plus PNGs for desktops/panels that want bitmaps.
# The icon's name is pdrive's app ID: a name GNOME Shell hasn't cached an older icon under.
rm -f "$ICONS/scalable/apps/pdrive.svg" "$ICONS"/*/apps/pdrive.png # the earlier name
install -Dm644 "$DIR/assets/pdrive.svg" "$ICONS/scalable/apps/io.github.m411s.pdrive.svg"
for size in 16 24 32 48 64 128 256; do
  install -Dm644 "$DIR/assets/icons/pdrive-$size.png" "$ICONS/${size}x${size}/apps/io.github.m411s.pdrive.png"
done

ICON_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/pdrive/icons"
mkdir -p "$ICON_DATA"
base="$DIR/assets/icons/pdrive-tray-base.png" # the plain cloud: the sync loop is a smudge at tray size
# Earlier versions could use Proton's Drive badge as the icon; Proton's rules for third-party apps
# don't allow its logos, so remove any copy left from then.
rm -f "$ICON_DATA/app-256.png" "$ICON_DATA/drive-badge.svg" "$ICON_DATA/drive-folder.svg" "$ICON_DATA/.drive-badge.download"
# Tray state icons (synced, syncing, paused, error, attention, stopped) from pdrive's icon.
if command -v magick >/dev/null || command -v convert >/dev/null; then
  "$DIR/assets/make-tray-icons.sh" "$base" "$ICON_DATA"
else
  echo "ImageMagick not found: the tray uses simple built-in state icons."
fi

# Only refresh an existing cache: creating one here would hide icons other apps add later.
if [ -f "$ICONS/icon-theme.cache" ] && command -v gtk-update-icon-cache >/dev/null; then
  gtk-update-icon-cache -q -t -f "$ICONS" || true
fi

cat > "$CONF/systemd/user/pdrive.service" <<UNIT
[Unit]
Description=Proton Drive sync and backup (unofficial)

[Service]
Type=notify
ExecStart="$LIB/pdrive-daemon"
Restart=on-failure
RestartSec=30
TimeoutStopSec=60
# Hardening that works for user services and doesn't get in the way of syncing your files.
NoNewPrivileges=yes
LockPersonality=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
SystemCallArchitectures=native

[Install]
WantedBy=default.target
UNIT

# Keep "start at login" off if it was switched off in Settings (Hidden=true per the XDG autostart spec).
AUTOSTART_HIDDEN=""
grep -qx "Hidden=true" "$CONF/autostart/pdrive-tray.desktop" 2>/dev/null && AUTOSTART_HIDDEN="Hidden=true"
cat > "$CONF/autostart/pdrive-tray.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=pdrive (tray)
Exec="$LIB/pdrive" --tray
Icon=io.github.m411s.pdrive
X-GNOME-Autostart-enabled=true
NoDisplay=true
$AUTOSTART_HIDDEN
DESK

cat > "$APPS/pdrive.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=pdrive
Comment=Sync, browse and back up Proton Drive (unofficial client)
GenericName=Proton Drive client
Keywords=Proton;Drive;sync;cloud;backup;
Exec="$LIB/pdrive"
Icon=io.github.m411s.pdrive
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

# Start (or restart, after an update) the tray now instead of waiting for the next login.
if [ "${NO_TRAY:-0}" != "1" ] && { [ -n "${WAYLAND_DISPLAY:-}" ] || [ -n "${DISPLAY:-}" ]; }; then
  pkill -u "$(id -u)" -f "^$LIB/pdrive --tray\$" 2>/dev/null && sleep 1 || true
  setsid -f "$LIB/pdrive" --tray >/dev/null 2>&1 < /dev/null && echo "Tray started (it also starts at login)."
fi

case ":$PATH:" in *":$BIN:"*) ;; *) echo "Note: $BIN is not on your PATH." ;; esac
echo "Next:  pdrivectl add ~/Documents /my-files/Documents   (or use the Backup page in the app)"
echo "On GNOME the tray icon needs the AppIndicator extension (Ubuntu has it enabled by default)."
