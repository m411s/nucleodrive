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
#   NO_BADGE=1     don't download Proton's Drive badge; use pdrive's own icon
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

# App icon (Icon=pdrive): scalable SVG plus PNGs for desktops/panels that want bitmaps.
install -Dm644 "$DIR/assets/pdrive.svg" "$ICONS/scalable/apps/pdrive.svg"
for size in 16 24 32 48 64 128 256; do
  install -Dm644 "$DIR/assets/icons/pdrive-$size.png" "$ICONS/${size}x${size}/apps/pdrive.png"
done

# Proton's Drive badge as the app icon, if wanted and possible. It's Proton's trademark, so it is
# downloaded onto this computer only and never shipped in the repo; pdrive's own icon is the fallback.
ICON_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/pdrive/icons"
BADGE_URL="https://pmecdn.protonweb.com/image-transformation/?s=c&image=image/upload/v1776940263/static/logos/drive/drive-badge.svg"
mkdir -p "$ICON_DATA"
render_svg() { # render_svg IN.svg OUT.png SIZE (librsvg via GdkPixbuf; ImageMagick's own SVG renderer drops gradients)
  python3 -I - "$1" "$2" "$3" 2>/dev/null <<'PY'
import sys, gi
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import GdkPixbuf
GdkPixbuf.Pixbuf.new_from_file_at_size(sys.argv[1], int(sys.argv[3]), int(sys.argv[3])).savev(sys.argv[2], "png", [], [])
PY
}
base="$DIR/assets/icons/pdrive-256.png"
if [ "${NO_BADGE:-0}" != "1" ] && command -v curl >/dev/null; then
  tmp_badge="$ICON_DATA/.drive-badge.download"
  if curl -fsSL --max-time 30 --proto =https -o "$tmp_badge" "$BADGE_URL" \
     && head -c 512 "$tmp_badge" | grep -q "<svg" && ! grep -qiE "<script|javascript:|xlink:href=\"http|href=\"http" "$tmp_badge"; then
    mv "$tmp_badge" "$ICON_DATA/drive-badge.svg"
  else
    rm -f "$tmp_badge"
    [ -f "$ICON_DATA/drive-badge.svg" ] || echo "Couldn't download Proton's Drive badge; using pdrive's own icon."
  fi
fi
# Transparent background: drop the badge's white rounded tile and crop to the folder.
# If the badge's shape ever changes so the tile isn't found, the original is used as is.
badge="$ICON_DATA/drive-badge.svg"
if [ -f "$badge" ] && python3 -I - "$badge" "$ICON_DATA/drive-folder.svg" <<'PY' 2>/dev/null
import re, sys
s = open(sys.argv[1], encoding="utf-8").read()
t = re.sub(r'<path fill="#fff" d="M0 10C[^"]*Z"/>', "", s, count=1)
if t == s or 'viewBox="0 0 36 36"' not in t:
    sys.exit(1)
open(sys.argv[2], "w", encoding="utf-8").write(t.replace('viewBox="0 0 36 36"', 'viewBox="6 6.4 24 24"', 1))
PY
then
  badge="$ICON_DATA/drive-folder.svg"
fi
if [ "${NO_BADGE:-0}" != "1" ] && [ -f "$badge" ] && render_svg "$badge" "$ICON_DATA/app-256.png" 256; then
  base="$ICON_DATA/app-256.png"
  install -Dm644 "$badge" "$ICONS/scalable/apps/pdrive.svg"
  for size in 16 24 32 48 64 128 256; do
    render_svg "$badge" "$ICONS/${size}x${size}/apps/pdrive.png" "$size" || true
  done
else
  rm -f "$ICON_DATA/app-256.png"
fi
# Tray state icons (synced, syncing, paused, error, attention, stopped) from whichever icon is in use.
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
Name=Proton Drive (tray)
Exec="$LIB/pdrive" --tray
Icon=pdrive
X-GNOME-Autostart-enabled=true
NoDisplay=true
$AUTOSTART_HIDDEN
DESK

cat > "$APPS/pdrive.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Proton Drive (unofficial)
Comment=Sync, browse and back up Proton Drive
Exec="$LIB/pdrive"
Icon=pdrive
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
echo "Next:  pdrivectl add ~/Documents /my-files/Documents   (or use the Backup tab in the app)"
echo "On GNOME the tray icon needs the AppIndicator extension (Ubuntu has it enabled by default)."
