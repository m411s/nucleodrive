# Changelog

All notable changes to NucleoDrive (called pdrive up to 0.2.1). Versions follow [Semantic Versioning](https://semver.org); before 1.0,
minor versions may change behaviour.

## Unreleased

- Packages: AppStream metainfo (for software centres), a copyright file, a changelog and a `nucleodrivectl`
  man page in the .deb, and update information plus a `.zsync` file for the AppImage, so AppImageUpdate
  and Gear Lever can update it. The AppImage's `.DirIcon` link pointed at a file name with the wrong case.

## 0.3.1 — 2026-11-23

- **Fix:** the app and tray crashed on startup when a `proton-drive` found on the way (for example
  `~/.local/bin/proton-drive`) was a symlink to a file that no longer exists. Such a link is now skipped.
- The app spells its name NucleoDrive everywhere: the setup wizard, window titles, the tray menu and
  Settings still said "nucleodrive" in places.

## 0.3.0 — 2026-11-21

- **New name: NucleoDrive** (was pdrive). Another Proton Drive tool for Linux already goes by "PDrive", so
  this one is now NucleoDrive: commands `nucleodrive` and `nucleodrivectl`, service `nucleodrive.service`,
  app ID `io.github.m411s.nucleodrive`, package `nucleodrive` (it replaces the `pdrive` package).
- **Upgrading keeps everything:** on first start, settings, sync state and the CLI installed from Settings
  move from the `pdrive` folders to the `nucleodrive` ones. `install.sh` and the AppImage stop and remove
  the old service and login entry; "start at login" stays off if you had switched it off.
- Environment variables are now `NUCLEODRIVE_*` (`NUCLEODRIVE_CONFIG_DIR` and so on).
- **Fix:** `install.sh` stopped with "Unit NucleoDrive.service does not exist" before starting the
  service and tray, and the .deb, AppImage and `uninstall.sh` didn't enable or disable the service
  (systemd unit names are case-sensitive; the unit is `nucleodrive.service`).
- The setup wizard now confirms clearly when it has installed the CLI (green dot, version, "Click Next
  to sign in") instead of a plain sentence above the Install button.
- **Tray icons without ImageMagick:** the tray state icons ship ready-made, so every install gets them.
  Before, `install.sh` made them with ImageMagick and fell back to plain icons without it.
- Building no longer fails with NU1507 when your NuGet config has more than one package source: the
  repository's `nuget.config` uses only nuget.org.

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
