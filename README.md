# pdrive

An unofficial Linux client for [Proton Drive](https://proton.me/drive), built on Proton's official
[`proton-drive` CLI](https://proton.me/support/drive-cli). Not affiliated with or endorsed by Proton AG.

- **Two-way sync**: chosen Drive folders stay identical with `~/proton-drive`. Changes on this computer
  upload within seconds. Changes made elsewhere arrive at the next check. Deletions go to the Trash, never
  erased.
- **Backup**: one-way upload of any other local folders.
- **Browse** your Drive: upload, download, sign in through the browser.
- **Tray icon** showing the state (synced, syncing, paused, needs you, error).
- **Settings**: sign in/out, install or update the CLI (verified against Proton's checksums), start at
  login.

## Get it

```bash
cd pdrive-net
./install.sh
```

That builds and installs for your user (needs the .NET 10 SDK). To build a `.deb` and an AppImage
instead, run `./package.sh`. Then choose folders in the app's **Sync** tab, or run
`pdrivectl sync add Documents`.

Everything else (how sync behaves, safety rules, configuration, development) is in
**[pdrive-net/README.md](pdrive-net/README.md)**.

## What's in this repository

| Folder | What it is |
|---|---|
| [`pdrive-net/`](pdrive-net) | **The app**: .NET 10 + Avalonia. Sync/backup daemon (systemd user service), `pdrivectl`, desktop app with tray, tests, install and packaging scripts. |
| [`proton-drive-gui/`](proton-drive-gui) | The original Python/GTK prototype (file browser + one-way backup). Kept for reference; `pdrive-net` imports its settings. |

Plans: [pdrive-net/docs/ROADMAP.md](pdrive-net/docs/ROADMAP.md). What was checked against the real CLI:
[pdrive-net/docs/cli-notes.md](pdrive-net/docs/cli-notes.md).

## Licence

[MIT](LICENSE) for pdrive's own code. "Proton" and "Proton Drive" are trademarks of Proton AG.
