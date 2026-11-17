# Changelog

All notable changes to pdrive. Versions follow [Semantic Versioning](https://semver.org); before 1.0,
minor versions may change behaviour.

## Unreleased

- **New app icon** (a white cloud with a sync loop on a purple tile, in the app's colours); the app is
  called "pdrive" (searchable as Proton Drive client).
  Proton's Drive badge is no longer used as the icon unless you ask for it (`PROTON_BADGE=1 ./install.sh`).
- **Sync page: folder tree.** Open any Drive folder to choose subfolders to sync, not only top-level
  ones. Subfolders are listed only when you open a folder; folders that sync only partly say so.
- **Light, dark or automatic** (follows the system) in Settings › Appearance; remembered in `~/.config/pdrive/theme`.
- **Fewer CLI runs everywhere:** saving files checks Drive with one listing per folder instead of one
  call per file; a folder tree copied into the sync folder goes up in one upload; new folders take one
  call instead of two; downloads for several folders run at once; checks of Drive back off while
  nothing changes (`sync.remote_interval_max_minutes`, 60).
- **Faster downloads:** a folder's changed files come down in one CLI run instead of one per file
  (100 new photos: about 2.5 minutes → a few seconds).
- Files and folders whose names start with `-` now sync (passed to the CLI after `--`).
- The service ignores a folder listed inside another synced folder (it was synced twice).
- Removed the Python prototype from the repository.

## 0.1.0 — 2026-11-17

First release.

- **Two-way sync** of chosen Drive folders with `~/proton-drive`: local changes upload within seconds
  (inotify), Drive changes arrive at the next check; renames and moves kept as renames; newest wins on
  conflicts, the other version kept in the Trash or Drive's version history; deletions go to the Trash on
  the other side; large deletions wait for confirmation.
- **One-way backup** of other local folders (`pdrivectl add`; hidden in the window for now).
- **App**: browse Drive (breadcrumbs, upload by button or drag and drop, download), Sync page with a
  toggle per folder, Settings (sign in/out, install or update the `proton-drive` CLI verified against
  Proton's checksums, start at login), first-run setup wizard, light and dark themes.
- **Tray icon** with sync state (up to date, syncing, paused, needs you, problem), Sync now, pause,
  "Review deletions…", Settings.
- **Background service** (systemd user unit, hardened) and `pdrivectl` command line.
- **Security**: never writes through symlinks or outside the sync folder, refuses option-like names,
  private config and state, control socket limited to your user. See SECURITY.md and PRIVACY.md.
- **Packages**: `.deb` and AppImage (x86-64), self-contained (no .NET runtime needed).
