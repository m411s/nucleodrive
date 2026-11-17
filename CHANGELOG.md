# Changelog

All notable changes to pdrive. Versions follow [Semantic Versioning](https://semver.org); before 1.0,
minor versions may change behaviour.

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
