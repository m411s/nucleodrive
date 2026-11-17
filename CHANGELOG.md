# Changelog

All notable changes to pdrive. Versions follow [Semantic Versioning](https://semver.org); before 1.0,
minor versions may change behaviour.

## 0.2.1 — 2026-11-17

- **App icon fix:** the dock and app menu could keep showing an old icon (GNOME Shell keeps icons it has
  loaded by name), or a generic gear right after installing. The icon is now named after pdrive's app ID,
  `io.github.m411s.pdrive`, and `install.sh` tells GNOME about new icons straight away.
- The release notes no longer say attestations need a public repository.

## 0.2.0 — 2026-11-17

### Sync

- **Folder tree on the Sync page.** Open any Drive folder to choose subfolders to sync, not only
  top-level ones. Subfolders are listed only when you open a folder; folders that sync only partly say so.
- **Choose how often Drive is checked:** every 30 minutes, every hour (default), every 2 or 4 hours, or
  only on Sync now, with "Last check took 26 s · next check around 15:40". Also `pdrivectl sync interval`.
- **Drive is checked hourly** by default (was every 15 minutes), backing off to every 4 hours while
  nothing changes; Proton asks third-party apps not to traverse the file tree frequently. Local changes
  still upload within seconds, and Sync now checks straight away. Existing configs keep their value.
- Files and folders whose names start with `-` now sync (passed to the CLI after `--`).
- The service ignores a folder listed inside another synced folder (it was synced twice).

### Faster

- Saving files checks Drive with one listing per folder instead of one call per file (20 files: ~30 s of
  checks → ~1.5 s).
- A folder's changed files download in one CLI run, and several folders at once (100 new photos in
  5 folders: ~2.5 minutes → a few seconds).
- A folder tree copied into the sync folder goes up in one upload (20 subfolders: ~90 s → ~10 s).
- New folders on Drive take one CLI call instead of two.

### App

- **New look:** a new app icon (a white cloud with a sync loop on a violet tile) and a violet accent
  (#7C3AED) of its own; the app is called "pdrive" (searchable as a Proton Drive client).
- **Light, dark or automatic** (follows the system) in Settings › Appearance.
- **What the Proton Drive CLI can't do**, explained in Settings and the README.

### Proton's rules for third-party apps

- A **use-at-your-own-risk notice** in the README, About, the setup wizard and the release notes:
  unofficial, for personal non-commercial use, no responsibility for data loss.
- "pdrive is a third-party app, not made or supported by Proton" wherever you sign in.
- Proton's Drive badge is no longer used or downloaded as the icon; a copy left by an earlier install
  is removed.

### Repository

- Public, with a privacy policy (`PRIVACY.md`) and a security policy (`SECURITY.md`).
- Removed the Python prototype.

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
