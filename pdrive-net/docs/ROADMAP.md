# Planned

Ideas:

- Edit the rest of `config.json` in Settings (sync folder location, check interval, parallel listings,
  deletion threshold, exclude patterns), so the file never needs editing by hand.
- **Exclude a subfolder inside a synced folder** (keep it online-only). Choosing subfolders to sync is done
  (folder tree on the Sync page); switching off a subfolder *inside* a synced parent is not possible yet.
  It needs per-path exclusions in the config and the planner (today's exclude patterns match names
  anywhere, not paths), and a rule for the local copy: move it to the Trash, or keep it and stop syncing
  it (ask, like the deletion guard).
- Bring the Backup page back in the window once it's polished (it's hidden for now; one-way backups still
  work through `pdrivectl add` and the daemon).
- Rename, delete, new folder and sharing in the Files page (the CLI has `rename`, `trash`,
  `create-folder` and `sharing` commands).

## Windows and macOS clients

Proton already ships official Drive apps for Windows and macOS, so check first what pdrive would add
there (the CLI-based design, two-way sync of chosen folders, backups, open source). The app (Avalonia),
the sync planner and executor, the CLI wrapper and the IPC contracts are cross-platform .NET already.
These parts are Linux-specific and need a counterpart:

| Today (Linux) | Windows | macOS |
|---|---|---|
| systemd user service (`Type=notify`) | Task Scheduler logon task, or the daemon hosted in the tray process | launchd LaunchAgent (`~/Library/LaunchAgents`) |
| Unix socket in `$XDG_RUNTIME_DIR` + `SO_PEERCRED` | Named pipe with a per-user ACL (or AF_UNIX on Windows 10 1803+) | Unix socket + `getpeereid`/`LOCAL_PEERCRED` |
| `flock` single instance | Named mutex | `flock` works as is |
| `statx` P/Invoke (inode, mtime) | File ID + `GetFileInformationByHandleEx` | `stat` (different struct layout) |
| Freedesktop Trash | Recycle Bin (`IFileOperation`) | `NSFileManager.trashItem` |
| XDG paths, `.desktop` autostart | `%LOCALAPPDATA%`, `HKCU\…\Run` | `~/Library/Application Support`, `SMAppService` login item |
| `xdg-open` | `explorer.exe` | `open` |
| .deb / AppImage | MSIX or Inno Setup, winget; code signing certificate | Signed and notarised `.app` in a `.dmg` (Apple Developer ID), Homebrew cask |

Sync rules that matter more there:

- **Case-insensitive file systems** (NTFS, APFS by default): `Report.pdf` and `report.pdf` on Drive map to one
  local file. Detect and keep one online-only with a warning (same work as the NFC/NFD item in SECURITY.md).
- **Windows names**: `< > : " | ? *`, trailing dots or spaces and reserved names (`CON`, `NUL`, `COM1`…) can't
  exist locally; keep such items online-only. Long paths need `\\?\` or the long-path setting.
- **Symlinks/junctions and reparse points** (OneDrive placeholders, etc.) must be ignored like symlinks today.

First step: check whether Proton publishes the proton-drive CLI for `windows-x64`/`windows-arm64` and
`darwin-arm64`/`darwin-x64` (its release index lists the platforms). Without it there's nothing to build on.
Then: abstract the platform services behind interfaces (service manager, IPC transport, trash, file IDs,
autostart, open folder), add CI runners for both systems, and start with Windows (no notarisation needed for a first test build).

## Distribution: Flatpak, Snap, APT repository, stores

Today: `package.sh` builds a .deb and an AppImage locally (see README). Getting pdrive to other people
needs the groundwork below first, then one item per channel.

### Groundwork (needed by every channel)

- **Licence.** Done: MIT (`LICENSE`). Packages still need it shipped as `/usr/share/doc/pdrive/copyright`
  (.deb) and in the AppStream metainfo (`<project_license>MIT</project_license>`).
- **Name and identity.** Stores reject anything that looks official. Keep "pdrive — unofficial client for
  Proton Drive"; never use Proton's logo or a `me.proton.*` / `ch.proton.*` ID. App ID e.g.
  `io.github.<owner>.pdrive` (Flathub requires that you control that domain, i.e. the GitHub account).
  Check Proton's brand guidelines for what the description may say.
- **The proton-drive CLI.** Find out whether Proton's licence allows redistributing the binary. Until
  then, don't bundle it: keep downloading it from proton.me, either with Settings › Install the CLI, which
  works inside sandboxes because it writes to `$XDG_DATA_HOME`, or with Flatpak `extra-data` (URL + SHA-256
  from Proton's release index).
- **AppStream metainfo** (`<app-id>.metainfo.xml`): summary, description, screenshots, releases,
  content rating, `<developer>`, launchable. GNOME Software, KDE Discover and Flathub all show it. The
  .deb and AppImage should ship it too.
- **Versioned releases.** Real versions instead of `0.1.0~git…`, git tags, a changelog.
- **CI** (GitHub Actions). Run `test.sh` on every push. On a tag, build the .deb and AppImage for
  amd64 and arm64 and attach them to a GitHub Release. Add zsync update info to the AppImage, so
  AppImageUpdate and Gear Lever can update it.
- **Screenshots** of the Files, Sync and Settings pages, for every store page (`PDRIVE_SCREENSHOTS=dir` renders them).

### Flatpak / Flathub

- Manifest: `org.freedesktop.Platform` 25.08 with the `org.freedesktop.Sdk.Extension.dotnet10`
  extension. Flathub builds offline, so generate the NuGet sources with `flatpak-dotnet-generator.py`
  from the solution and commit the result.
- Permissions:
  - `--share=network`; `--socket=wayland` and `--socket=fallback-x11`; `--talk-name=org.freedesktop.secrets` (the CLI keeps its session in the keyring);
  - `--talk-name=org.kde.StatusNotifierWatcher` (tray icon);
  - `--filesystem=~/proton-drive:create` plus the backup folders the user picks (keep the list short:
    Flathub reviewers push back on `--filesystem=home`).
- **No systemd inside the sandbox.** The daemon has to run differently:
  - either ask the Background portal (`org.freedesktop.portal.Background`, `RequestBackground` with
    autostart) to start `pdrive --tray` at login, and let the tray process host the sync/backup workers
    (`DaemonHost` in-process instead of a separate unit), or
  - start `flatpak run --command=pdrive-daemon` from the autostart entry the portal writes.
  Settings' "background service" toggle then talks to the portal instead of `systemctl`.
- Desktop Trash: the sandbox can't reach `~/.local/share/Trash`; use the Trash portal
  (`org.freedesktop.portal.Trash`) for local deletions. `xdg-open` already goes through the OpenURI portal.
- Submit as a PR to `flathub/flathub` and keep the manifest in a `flathub/<app-id>` repo afterwards.

### Snap / Snap Store

- `snapcraft.yaml`, `base: core24`, `confinement: strict`. Build with the `dotnet` plugin, or `dump`
  the self-contained `package.sh` output.
- Plugs: `network`, `home`, `desktop`, `wayland`, `x11`, `password-manager-service` (keyring; needs
  store approval), plus whatever the tray needs (StatusNotifierItem over D-Bus: check the `unity7`
  plug or a `dbus` plug).
- Background service: a snap **user daemon** (`daemon: simple`, `daemon-scope: user`) runs
  `pdrive-daemon`. Settings' service toggle uses `snapctl` / `snap start|stop --user` instead of `systemctl`.
- Desktop Trash: same problem as Flatpak (the sandbox can't reach `~/.local/share/Trash`); check
  what's allowed.
- Avoid `classic` confinement: it needs a manual review with a justification and is unlikely to be granted.
- Register the name `pdrive`, then publish to `edge` → `beta` → `stable`.

### APT repository (own, signed)

- Our own repo, not a Launchpad PPA: PPAs build from source on Launchpad without network access,
  which makes the .NET restore painful.
- Build in CI on each tag for amd64 and arm64. Index with `reprepro` or `aptly` and sign with a
  dedicated GPG key (kept only in CI secrets). Host on GitHub Pages or object storage.
- Users add a deb822 source with `Signed-By:`:
  `/etc/apt/sources.list.d/pdrive.sources` plus the key in `/etc/apt/keyrings/pdrive.gpg`. Document it in
  the README and offer a small `pdrive-archive-keyring` package.
- Run `lintian` on the .deb in CI and fix what it reports (e.g. copyright file, changelog, man page for
  `pdrivectl`).
- Later, an RPM (Fedora/openSUSE) from the same publish output, and a COPR or OBS repo.

### Other places

- **GitHub Releases** with the AppImage, .deb, checksums and a signature. That's the simplest
  channel and the one the others link to.
- **AppImageHub** (appimage.github.io): submit once the AppImage has update info and AppStream metadata.
- **AUR** (`pdrive-bin`) from the GitHub Release; usually maintained by the community once the project exists.

## Done

- 2026-11-17: **Packages**: `package.sh` builds a .deb (system-wide; user service enabled for everyone)
  and an AppImage (sets up its own service and login entry on first launch).
- 2026-11-17: **Redesign**: sidebar navigation with a live status, breadcrumbs, cached folder listings, folder toggles that apply immediately, cards with line icons, light/dark themes, cross-fades; the window is released when closed to the tray. **Security review** (see docs/SECURITY.md).
- 2026-11-17: **Settings page**: Proton account (sign in/out), the proton-drive CLI (install/update
  from Proton's release index with SHA-512 verification, into `~/.local/share/pdrive/cli`, linked as
  `~/.local/bin/proton-drive`), start the tray at login, keep the background service running.
- 2026-11-17: **Tray state icons** (synced, syncing, paused, error, needs you, not running) in Proton
  VPN's style, made by `assets/make-tray-icons.sh` from the app icon; `install.sh` uses Proton's Drive
  badge (transparent, local only, never committed: it's Proton's trademark).
