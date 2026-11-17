# pdrive: Proton Drive for Linux (unofficial, .NET)

A file browser, one-way folder backup and tray icon for Proton Drive, built on Proton's official
`proton-drive` CLI. It replaces the Python prototype in `../proton-drive-gui`. Not affiliated with Proton AG.

- **Browse** your Drive: upload (button or drag-and-drop), download, sign in through the browser.
- **Back up** local folders to Drive: new and changed files are uploaded within seconds (inotify),
  with a full scan every `interval_minutes` as a safety net. **Nothing is ever deleted** on either side;
  deleting or renaming a local file leaves the old copy on Drive.
- **Tray icon** shows the backup state: green = up to date, blue = syncing, light grey = paused,
  red = problem, amber = sign in needed, dark grey = daemon not running.

## Install

Needs the .NET 10 SDK to build (the installed app is self-contained) and the `proton-drive` CLI:
download it from https://proton.me/download/drive/cli, `chmod +x` it, and put it in `~/.local/bin`
(or set `PROTON_DRIVE_BIN`).

```bash
./install.sh
```

```bash
pdrivectl add ~/Documents /my-files/Documents
```

`install.sh` publishes to `~/.local/lib/pdrive`, links `~/.local/bin/pdrivectl`, and installs and starts
the `pdrive` systemd user service, the tray autostart entry and an app launcher. If the Python
prototype's service is running, it offers to stop it: two daemons on the same folders would upload
twice. `./uninstall.sh` removes everything except your settings (`--purge` removes those too).

On GNOME, tray icons need the "AppIndicator and KStatusNotifierItem Support" extension. KDE, XFCE and
Cinnamon show them out of the box. Without a tray, use the app's Backup tab.

## Using it

| | |
|---|---|
| App | `pdrive`: Files and Backup tabs. `pdrive --tray` starts hidden (used at login). Launching again shows the running window. |
| CLI | `pdrivectl status \| list \| add LOCAL REMOTE \| remove LOCAL \| now \| pause \| resume` |
| Daemon | `systemctl --user status pdrive`, logs with `journalctl --user -u pdrive` |
| Config | `~/.config/pdrive/config.json`: `interval_minutes` (15), `min_age_seconds` (5), `exclude` (`.*`, `*~`, `*.tmp`, `*.part`, `*.crdownload`), `folders`, `conflict_strategy` (passed to `upload --conflict-strategy`), `watch` (true) |
| State | `~/.local/state/pdrive/state.db` (SQLite: what was uploaded where, pause, last sync) |

A broken `config.json` shows as an error in the status; the daemon keeps running on the last good
settings. `pdrivectl add/remove/pause/resume` also work while the daemon is stopped.

### Moving from the Python prototype

The first start imports `~/.config/proton-drive-gui/config.json` and `~/.local/state/proton-drive-gui/state.json`.
Unchanged files are **not** uploaded again (mtimes are compared at .NET's 100 ns resolution).
The old files are left alone.

## Layout

```
src/Pdrive.Cli      wrapper around the proton-drive CLI (CliWrap; SIGINT-then-kill cancellation,
                    size-scaled timeouts, AuthException vs DriveException, tolerant listing parser)
src/Pdrive.Sync     config, SQLite state, scanner, batching, SyncEngine, ChangeWatcher (inotify)
src/Pdrive.Ipc      JSON-RPC contract over a Unix socket, DaemonClient, auto-reconnecting DaemonMonitor
src/Pdrive.Daemon   Worker Service (systemd notify), single-instance flock, sync loop, RPC server
src/Pdrive.Ctl      pdrivectl
src/Pdrive.App      Avalonia app: browser, backup page, tray, single instance
tests/              xUnit v3; Pdrive.FakeCli stands in for proton-drive
docs/cli-notes.md   what is (not yet) verified against the real CLI
```

The daemon socket is `$XDG_RUNTIME_DIR/pdrive/daemon.sock` (mode 0600). Override paths with
`PDRIVE_CONFIG_DIR`, `PDRIVE_STATE_DIR` and `PDRIVE_RUNTIME_DIR`.

## Develop

```bash
dotnet build
```

```bash
dotnet test
```

Tests use `tests/Pdrive.FakeCli` instead of the real CLI, plus temp directories, so they never touch
your Drive or settings.

## Known gaps

- **Not yet checked against the real CLI**: the listing JSON schema, whether `mkdir` exists, which
  `--conflict-strategy` values exist and what re-uploading does without one, the wording of sign-in errors,
  progress output, and keyring access from the systemd service. See `docs/cli-notes.md`.
- No delete, rename, mkdir or share in the browser yet. No two-way sync.
- Files that change continuously are uploaded at most once per 60 s of activity (the watcher's cap).
