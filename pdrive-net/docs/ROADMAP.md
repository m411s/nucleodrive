# Planned

Nothing scheduled right now. Ideas:

- Edit the rest of `config.json` in Settings (sync folder location, check interval, parallel listings,
  deletion threshold, exclude patterns), so the file never needs editing by hand.
- Rename, delete, new folder and sharing in the Files tab (the CLI has `rename`, `trash`,
  `create-folder` and `sharing` commands).

## Done

- 2026-11-17: **Settings tab**: Proton account (sign in/out), the proton-drive CLI (install/update
  from Proton's release index with SHA-512 verification, into `~/.local/share/pdrive/cli`, linked as
  `~/.local/bin/proton-drive`), start the tray at login, keep the background service running.
- 2026-11-17: **Tray state icons** (synced, syncing, paused, error, needs you, not running) made by
  `assets/make-tray-icons.sh` from the app icon; `install.sh` uses Proton's Drive badge as the app
  icon when it can download it (local only, never committed: it's Proton's trademark).
