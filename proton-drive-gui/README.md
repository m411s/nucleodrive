# Proton Drive GUI (unofficial)

A small GTK4/libadwaita file browser on top of Proton's official `proton-drive` CLI.
Browse folders, upload (button or drag & drop), download, sign in via browser.
Not affiliated with Proton AG. It's a browser/transfer tool, not a sync client.

## Setup

1. Get the CLI from https://proton.me/download/drive/cli, then:
   `chmod +x proton-drive && mv proton-drive ~/.local/bin/`
   (or set `PROTON_DRIVE_BIN=/path/to/proton-drive`)
2. Install GTK bindings (Debian/Ubuntu): `sudo apt install python3-gi gir1.2-gtk-4.0 gir1.2-adw-1`
   (Fedora: `python3-gobject gtk4 libadwaita`)
3. Run: `python3 app.py` and click "Sign in" if prompted.

The CLI keeps its session in your OS keyring (libsecret / GNOME Keyring / KWallet).

## Files

- `protondrive.py` – CLI wrapper (no GUI deps)
- `app.py` – the GTK app
- `test_backend.py` + `fake-proton-drive` – tests for the wrapper (`python3 test_backend.py`)

## Known gaps

- The listing parser is tolerant because the CLI's `--json` schema isn't documented; if
  names/folders look wrong, run `proton-drive filesystem list /my-files --json` and adjust `parse_listing`.
- No delete/rename/mkdir/share yet (the CLI has more commands; add them in `protondrive.py`).
- Transfers show a spinner and toast, not per-file progress.

## Background backup + tray (one-way, local → Drive)

```
./install.sh                                   # venv + systemd user service + tray autostart + launcher
python3 syncd.py add ~/Documents /my-files/Documents
python3 syncd.py status | pause | resume | now
python3 tray.py &                              # or log out/in; tray autostarts
```

- Uploads new/changed files every `interval_minutes` (default 15, `~/.config/proton-drive-gui/config.json`).
- Never deletes anything locally or on Drive. Deleted/renamed local files stay in Drive.
- Skips dotfiles, `*.tmp`, `*.part`, files modified in the last 5 s (edit `exclude` in the config).
- The tray needs the AppIndicator/StatusNotifier support of your desktop (GNOME: "AppIndicator and
  KStatusNotifierItem Support" extension; KDE/XFCE/Cinnamon work out of the box).
- Only one daemon runs at a time (lock: `~/.local/state/proton-drive-gui/daemon.lock`).
  A broken `config.json` shows as an error in the tray/status instead of stopping the daemon.
- Upload timeouts scale with file size (10 min + time at 256 KB/s). Files over 256 MB go in a
  CLI call of their own; if a batch fails, its files are retried one by one.
- Run `python3 test_sync.py` (engine) and `python3 test_daemon.py` (daemon process) for tests.

### Unverified against the real CLI
- Whether `filesystem mkdir` exists (failure is ignored; upload then reports the real error).
- Whether re-uploading a changed file replaces it, adds a revision, or creates a duplicate.
- Whether the CLI session is reachable from the systemd user service (needs the session D-Bus/keyring).
  If the tray shows "Sign in required" after you signed in, that's the first suspect.
