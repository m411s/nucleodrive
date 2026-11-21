#!/usr/bin/env bash
# Builds distributable packages into dist/:
#   dist/nucleodrive_<version>_<arch>.deb          system-wide install (sudo apt install ./nucleodrive_….deb)
#   dist/nucleodrive-<version>-<arch>.AppImage     single file, no install
#
#   ./package.sh            both
#   ./package.sh deb        only the .deb
#   ./package.sh appimage   only the AppImage
#
# Environment: VERSION (default: 0.1.0~git<date>.<commit>), MAINTAINER (for the .deb).
# Needs the .NET 10 SDK, dpkg-deb, ImageMagick; the AppImage step downloads appimagetool 1.9.1 once
# (checked against its pinned SHA-256) into .tools/.
#
# The packages use NucleoDrive's own icon. Proton's Drive badge is never included (it's Proton's
# trademark); install.sh can still use it on your own machine.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
DIR="$PWD"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

targets=("$@")
[ ${#targets[@]} -eq 0 ] && targets=(deb appimage)

case "$(uname -m)" in
  x86_64) RID=linux-x64 DEB_ARCH=amd64 AI_ARCH=x86_64 ;;
  aarch64|arm64) RID=linux-arm64 DEB_ARCH=arm64 AI_ARCH=aarch64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

commit=$(git rev-parse --short HEAD 2>/dev/null || echo unknown)
VERSION="${VERSION:-0.1.0~git$(date +%Y%m%d).$commit}"
FILE_VERSION="${VERSION//\~/-}"
MAINTAINER="${MAINTAINER:-NucleoDrive (unofficial Proton Drive client) <nucleodrive@localhost>}"

BUILD="$DIR/dist/build"
PUB="$BUILD/publish"
rm -rf "$BUILD"
mkdir -p "$PUB" "$DIR/dist"

echo "==> Publishing NucleoDrive $VERSION ($RID)"
for proj in NucleoDrive.Daemon NucleoDrive.Ctl NucleoDrive.App; do
  dotnet publish "src/$proj/$proj.csproj" -c Release -r "$RID" --self-contained \
    -p:DebugType=none -p:Version="${FILE_VERSION%%[-+]*}" -o "$PUB" --nologo -v quiet
done

echo "==> Icons"
ICONS="$BUILD/icons"
mkdir -p "$ICONS"
"$DIR/assets/make-tray-icons.sh" "$DIR/assets/icons/nucleodrive-tray-base.png" "$ICONS"
cp "$DIR/assets/icons/nucleodrive-256.png" "$ICONS/app-256.png"

# Puts hicolor icons + tray icons under a share/ directory.
install_icons() {
  local share="$1"
  install -Dm644 "$DIR/assets/nucleodrive.svg" "$share/icons/hicolor/scalable/apps/io.github.m411s.nucleodrive.svg"
  for size in 16 24 32 48 64 128 256; do
    install -Dm644 "$DIR/assets/icons/nucleodrive-$size.png" "$share/icons/hicolor/${size}x${size}/apps/io.github.m411s.nucleodrive.png"
  done
  mkdir -p "$share/nucleodrive/icons"
  cp "$ICONS"/*.png "$share/nucleodrive/icons/"
}

# Files 644 (755 if executable), folders 755, whatever the build machine's umask was.
normalize_modes() {
  find "$1" -type d -exec chmod 755 {} +
  find "$1" -type f -perm /111 -exec chmod 755 {} +
  find "$1" -type f ! -perm /111 -exec chmod 644 {} +
}

build_deb() {
  echo "==> .deb"
  local root="$BUILD/deb"
  mkdir -p "$root/opt/nucleodrive" "$root/usr/bin" "$root/DEBIAN"
  cp -a "$PUB/." "$root/opt/nucleodrive/"
  ln -s /opt/nucleodrive/nucleodrive "$root/usr/bin/nucleodrive"
  ln -s /opt/nucleodrive/nucleodrivectl "$root/usr/bin/nucleodrivectl"
  install_icons "$root/usr/share"

  install -Dm644 /dev/stdin "$root/usr/lib/systemd/user/nucleodrive.service" <<'UNIT'
[Unit]
Description=Proton Drive sync and backup (unofficial)
Documentation=https://proton.me/support/drive-cli

[Service]
Type=notify
ExecStart=/opt/nucleodrive/nucleodrive-daemon
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

  install -Dm644 /dev/stdin "$root/usr/share/applications/nucleodrive.desktop" <<'DESK'
[Desktop Entry]
Type=Application
Name=NucleoDrive
Comment=Sync, browse and back up Proton Drive (unofficial client)
GenericName=Proton Drive client
Keywords=Proton;Drive;sync;cloud;backup;
Exec=nucleodrive
Icon=io.github.m411s.nucleodrive
Categories=Network;FileTransfer;
StartupWMClass=nucleodrive
DESK

  install -Dm644 /dev/stdin "$root/etc/xdg/autostart/nucleodrive-tray.desktop" <<'DESK'
[Desktop Entry]
Type=Application
Name=NucleoDrive (tray)
Exec=nucleodrive --tray
Icon=io.github.m411s.nucleodrive
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESK

  local size
  size=$(du -sk "$root" | cut -f1)
  # pdrive was this package's name up to 0.2.1: Conflicts/Replaces make installing NucleoDrive remove it.
  cat > "$root/DEBIAN/control" <<CTRL
Package: nucleodrive
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: $MAINTAINER
Installed-Size: $size
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6
Conflicts: pdrive
Replaces: pdrive
Recommends: python3-gi
Suggests: gnome-shell-extension-appindicator
Section: net
Priority: optional
Homepage: https://proton.me/drive
Description: Unofficial Proton Drive client: two-way sync, backup, browser, tray
 Keeps chosen Proton Drive folders in sync with ~/proton-drive, backs up other
 folders one way, and lets you browse Drive. Built on Proton's official
 proton-drive CLI, which can be installed from the app's Settings page.
 Not affiliated with Proton AG.
CTRL

  # Enable the user service for everyone (it starts at their next login); stop/disable on removal.
  cat > "$root/DEBIAN/postinst" <<'SH'
#!/bin/sh
set -e
if [ "$1" = configure ] && command -v systemctl >/dev/null 2>&1; then
  systemctl --global enable nucleodrive.service >/dev/null 2>&1 || true
  echo "nucleodrive: the sync service starts at your next login (or now: systemctl --user start nucleodrive)."
fi
exit 0
SH
  cat > "$root/DEBIAN/prerm" <<'SH'
#!/bin/sh
set -e
if [ "$1" = remove ] && command -v systemctl >/dev/null 2>&1; then
  systemctl --global disable nucleodrive.service >/dev/null 2>&1 || true
fi
exit 0
SH
  chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm"
  normalize_modes "$root"

  local out="$DIR/dist/nucleodrive_${VERSION}_${DEB_ARCH}.deb"
  dpkg-deb --build --root-owner-group -Zxz "$root" "$out" >/dev/null
  echo "    $out ($(du -h "$out" | cut -f1))"
}

appimagetool_path() {
  local tool="$DIR/.tools/appimagetool-1.9.1-$AI_ARCH.AppImage"
  local sha
  case "$AI_ARCH" in
    x86_64) sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 ;;
    *) command -v appimagetool && return; echo "No pinned appimagetool for $AI_ARCH; install appimagetool." >&2; exit 1 ;;
  esac
  if command -v appimagetool >/dev/null; then command -v appimagetool; return; fi
  if [ ! -x "$tool" ]; then
    mkdir -p "$DIR/.tools"
    curl -fsSL --proto =https -o "$tool.part" \
      "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$AI_ARCH.AppImage"
    echo "$sha  $tool.part" | sha256sum -c --quiet - || { rm -f "$tool.part"; echo "appimagetool checksum mismatch" >&2; exit 1; }
    chmod +x "$tool.part"
    mv "$tool.part" "$tool"
  fi
  echo "$tool"
}

build_appimage() {
  echo "==> AppImage"
  local app="$BUILD/AppDir"
  mkdir -p "$app/usr/lib/nucleodrive"
  cp -a "$PUB/." "$app/usr/lib/nucleodrive/"
  install_icons "$app/usr/share"
  cp "$DIR/assets/icons/nucleodrive-256.png" "$app/nucleodrive.png"
  ln -s NucleoDrive.png "$app/.DirIcon"
  install -Dm755 "$DIR/packaging/appimage/AppRun" "$app/AppRun"
  normalize_modes "$app"
  cat > "$app/nucleodrive.desktop" <<'DESK'
[Desktop Entry]
Type=Application
Name=NucleoDrive
Comment=Sync, browse and back up Proton Drive (unofficial client)
GenericName=Proton Drive client
Keywords=Proton;Drive;sync;cloud;backup;
Exec=nucleodrive
Icon=nucleodrive
Categories=Network;FileTransfer;
StartupWMClass=nucleodrive
DESK

  local tool out="$DIR/dist/nucleodrive-${FILE_VERSION}-${AI_ARCH}.AppImage"
  tool=$(appimagetool_path)
  # Run the tool from its extracted form, so building needs no FUSE.
  ARCH="$AI_ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$app" "$out" >/dev/null 2>"$BUILD/appimagetool.log" \
    || { cat "$BUILD/appimagetool.log" >&2; exit 1; }
  echo "    $out ($(du -h "$out" | cut -f1))"
}

for t in "${targets[@]}"; do
  case "$t" in
    deb) build_deb ;;
    appimage) build_appimage ;;
    *) echo "Unknown target: $t (use deb and/or appimage)" >&2; exit 1 ;;
  esac
done
