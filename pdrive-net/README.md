# pdrive: Proton Drive for Linux (unofficial, .NET)

A file browser, two-way folder sync, one-way backup and tray icon for Proton Drive, built on Proton's
official `proton-drive` CLI. Not affiliated with Proton AG.

- **Sync** chosen Drive folders with `~/proton-drive` in both directions (details below).
- **Browse** your Drive: upload (button or drag-and-drop), download, sign in through the browser.
- **Back up** local folders to Drive: new and changed files are uploaded within seconds (inotify),
  with a full scan every `interval_minutes` as a safety net. **Nothing is ever deleted** on either side;
  deleting or renaming a local file leaves the old copy on Drive.
- **Tray icon** shows the state with a dot on the app icon: green ✓ synced, blue ↑ syncing, grey ‖ paused,
  red ! problem, amber ! needs you (sign in, or confirm deletions), faded – service not running.
- **Settings**: sign in/out, install or update the `proton-drive` CLI (verified against Proton's
  published SHA-512), start at login, keep syncing in the background.

## First start

A setup wizard walks through signing in to Proton (installing the CLI first if needed), choosing the
sync folder (`~/proton-drive` by default, created if missing) and which Drive folders to sync, and
whether to start at login. It appears until it's completed, unless folders are already synced; run it
again from **Settings › Run setup again…**. **Settings › About pdrive** (or the tray menu) shows the
version, the licence and the project link.

## Two-way sync (`~/proton-drive`)

Choose Drive folders in the app's **Sync** tab or with `pdrivectl sync add Photos`. Each one is mirrored
at `~/proton-drive/<folder>` (e.g. `/my-files/Documents/Work` ⇄ `~/proton-drive/Documents/Work`).
Unchosen folders stay online-only and remain browsable in the Files page.

- **Changes on this computer** (file manager, editors, …) upload within seconds (inotify).
- **Changes made elsewhere** (web, phone, other computers) arrive at the next check of Drive: every
  `sync.remote_interval_minutes` (15), or on **Sync now**. The CLI can only list one folder at a time
  (about 2 s each), so a check takes a while for big folders; up to `sync.parallel_listings` (4) run at once.
- **Deletions** go to the Trash on the other side (Drive trash, or your desktop Trash), never erased.
- **Renames and moves** are applied as renames, so nothing is re-transferred and version history stays.
- **Edited in both places**: the newer version wins. A losing local file goes to your desktop Trash; a
  losing Drive version stays in Drive's version history.
- **Safety**: more than `sync.max_deletes_without_confirm` (50) deletions at once wait for you to confirm
  (app, or `pdrivectl sync confirm`). A missing or unmounted folder, a folder gone from Drive, or a failed
  listing stop that folder's sync with an error instead of being read as "everything deleted".
  Files still being written are left until they settle.
- **First sync** of an empty local folder downloads it in one go. If the local folder already has files,
  they're merged: identical files (by SHA-1) are linked without transfer.
- Hidden files (`.*`) and other `exclude` patterns are skipped on both sides. Proton Docs/Sheets stay online-only.

## Install

Needs the .NET 10 SDK to build (the installed app is self-contained). The `proton-drive` CLI can be
installed from the app (**Settings › Install the CLI**), or by hand from
https://proton.me/download/drive/cli/index.html into `~/.local/bin` (or set `PROTON_DRIVE_BIN`).

`install.sh` downloads Proton's Drive badge to use as the app icon (onto this computer only; it's
Proton's trademark and isn't part of this repo). `NO_BADGE=1` keeps pdrive's own icon. Tray state
icons need ImageMagick; rendering the badge needs librsvg (`python3-gi` with GdkPixbuf).

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
Cinnamon show them out of the box. Without a tray, use the app's Backup page.

## Packages (.deb, AppImage)

```bash
./package.sh
```

builds `dist/pdrive_<version>_amd64.deb` and `dist/pdrive-<version>-x86_64.AppImage`
(`./package.sh deb` or `./package.sh appimage` for one). Both use pdrive's own icon; Proton's badge is
never included.

- **.deb**: `sudo apt install ./dist/pdrive_*.deb`. Installs to `/opt/pdrive` (`pdrive`, `pdrivectl` in
  `/usr/bin`), the `pdrive` systemd **user** service (enabled for everyone; starts at login, or now with
  `systemctl --user start pdrive`), the tray at login (`/etc/xdg/autostart`), and the launcher. Settings
  can switch the tray and the service off per user. Don't combine it with `install.sh`; run
  `./uninstall.sh` first.
- **AppImage**: `chmod +x pdrive-*.AppImage`, then run it. `… daemon` runs the service, `… ctl ARGS` is
  `pdrivectl`. The first launch sets up the background service, the login entry and a menu entry
  pointing at the AppImage file (updated if you move it; never touching an install.sh/.deb setup).
  `PDRIVE_NO_INTEGRATION=1` skips that.
- Building the AppImage downloads appimagetool 1.9.1 once into `.tools/` (checked against its pinned SHA-256).

## Using it

| | |
|---|---|
| App | `pdrive`: Files, Sync, Backup and Settings pages. `pdrive --tray` starts hidden (used at login). Launching again shows the running window. |
| CLI | `pdrivectl sync status \| list \| add FOLDER \| remove FOLDER \| now \| confirm` (two-way sync) |
| | `pdrivectl status \| list \| add LOCAL REMOTE \| remove LOCAL \| now \| pause \| resume` (backups; pause/resume cover sync too) |
| Daemon | `systemctl --user status pdrive`, logs with `journalctl --user -u pdrive` |
| Config | `~/.config/pdrive/config.json`: `interval_minutes` (15), `min_age_seconds` (5), `exclude` (`.*`, `*~`, `*.tmp`, `*.part`, `*.crdownload`), `folders`, `conflict_strategy` for files that already exist on Drive (`create-new-revision` default, `rename`, `replace`, `skip`), `watch` (true), and `sync`: `local` (`~/proton-drive`), `folders`, `remote_interval_minutes` (15), `parallel_listings` (4), `max_deletes_without_confirm` (50) |
| State | `~/.local/state/pdrive/state.db` (SQLite: what was uploaded where, the sync baseline, pause, last sync) |

A broken `config.json` shows as an error in the status; the daemon keeps running on the last good
settings. `pdrivectl add/remove/pause/resume` also work while the daemon is stopped.

### Moving from the Python prototype

The prototype was removed from this repository (it is in the git history before 0.1.1).

The first start imports `~/.config/proton-drive-gui/config.json` and `~/.local/state/proton-drive-gui/state.json`.
Unchanged files are **not** uploaded again (mtimes are compared at .NET's 100 ns resolution).
The old files are left alone.

## Layout

```
src/Pdrive.Cli      wrapper around the proton-drive CLI (CliWrap; SIGINT-then-kill cancellation,
                    size-scaled timeouts, AuthException vs DriveException, tolerant listing parser)
src/Pdrive.Sync     config, SQLite state, scanner, batching, SyncEngine (backup), ChangeWatcher (inotify),
                    TwoWay/ (sync planner, snapshots, executor, engine), FreedesktopTrash
src/Pdrive.Ipc      JSON-RPC contract over a Unix socket, DaemonClient, auto-reconnecting DaemonMonitor
src/Pdrive.Daemon   Worker Service (systemd notify), single-instance flock, sync loop, RPC server
src/Pdrive.Ctl      pdrivectl
src/Pdrive.App      Avalonia app: browser, sync and backup pages, tray, single instance
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
./test.sh
```

`test.sh` runs the xUnit v3 test programs directly (`dotnet test` works too, but its driver crashed
intermittently on SDK 10.0.115 before running any tests). Tests use `tests/Pdrive.FakeCli` instead of the real CLI, plus temp directories, so they never touch
your Drive or settings.

## Known gaps

Planned work is in `docs/ROADMAP.md` (next up: a Settings page to download the CLI and sign in).

- Built and checked against cli-drive 0.9.0 (`docs/cli-notes.md`). The CLI's JSON and messages are
  undocumented, so a newer CLI may need small adjustments there. Changed files become new revisions
  (`conflict_strategy`, default `create-new-revision`), so old versions stay in Drive's version history.
- The CLI prints no progress when it isn't attached to a terminal, so progress is per file and
  per batch, not within one large file.
- No delete, rename, mkdir or share in the browser yet.
- Sync has no "online-only" placeholder files (that would need a FUSE filesystem): synced folders are
  fully downloaded. Remote changes aren't instant (no event feed in the CLI).
- Files that change continuously are uploaded at most once per 60 s of activity (the watcher's cap).

## Licence

MIT, see [LICENSE](../LICENSE). This covers pdrive's own code and its own icon. It does not cover
Proton's proton-drive CLI, which pdrive downloads from proton.me and doesn't redistribute, or Proton's
logo, which `install.sh` may download for your own machine. "Proton" and "Proton Drive" are trademarks
of Proton AG; pdrive isn't affiliated with or endorsed by Proton.
