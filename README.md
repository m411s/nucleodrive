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

> [!WARNING]
> **Use at your own risk.** pdrive is an unofficial, third-party app, not made, endorsed or supported by
> Proton AG. It is meant for **personal, non-commercial use**: Proton only allows third-party Drive apps
> for personal, non-commercial projects. pdrive is provided "as is", without warranty of any kind, and its
> authors accept **no responsibility for any data loss** or other damage (see [LICENSE](LICENSE)). Keep
> backups of anything you can't afford to lose.

## Get it

```bash
cd pdrive-net
./install.sh
```

That builds and installs for your user (needs the .NET 10 SDK). To build a `.deb` and an AppImage
instead, run `./package.sh`. Then choose folders on the app's **Sync** page, or run
`pdrivectl sync add Documents`.

Everything else (how sync behaves, safety rules, configuration, development) is in
**[pdrive-net/README.md](pdrive-net/README.md)**.

## Limits of the Proton Drive CLI

pdrive does everything through Proton's official `proton-drive` command-line tool. That keeps your
password and encryption in Proton's hands, but the CLI has limits pdrive can't work around:

- **Changes made elsewhere aren't instant.** The CLI can't report what changed on Drive, so pdrive checks
  every hour (backing off to every 4 hours while nothing changes). **Sync now** checks straight away.
  Changes on this computer upload within seconds.
- **Checking Drive takes a while.** Each CLI run takes about 1.5 s and lists one folder, so a check of
  hundreds of folders takes a minute or more (4 run at once).
- **No online-only files.** Synced folders are fully downloaded; there are no placeholders that download
  when opened. Folders you don't sync stay browsable on the Files page.
- **Proton Docs and Sheets can't be downloaded.** They stay online-only; open them on the web.
- **Only My files.** "Shared with me", Photos and Devices aren't synced.
- **No progress within a single file.** Big uploads and downloads show progress per file, not per byte.
- **Names Drive can't decrypt** stay online-only.
- **Signing in needs a browser and a system keyring** (GNOME Keyring, KWallet, …), where the CLI keeps the
  session.

## Privacy and security

pdrive collects nothing: no telemetry, analytics or crash reports. Your files go only to Proton, encrypted
by Proton's CLI, and pdrive never sees your password. Details in **[PRIVACY.md](PRIVACY.md)**.

Found a security problem? Please report it privately, as described in **[SECURITY.md](SECURITY.md)**.

## What's in this repository

| Folder | What it is |
|---|---|
| [`pdrive-net/`](pdrive-net) | **The app**: .NET 10 + Avalonia. Sync/backup daemon (systemd user service), `pdrivectl`, desktop app with tray, tests, install and packaging scripts. |

Plans: [pdrive-net/docs/ROADMAP.md](pdrive-net/docs/ROADMAP.md). What was checked against the real CLI:
[pdrive-net/docs/cli-notes.md](pdrive-net/docs/cli-notes.md).

## Licence

[MIT](LICENSE) for pdrive's own code. "Proton" and "Proton Drive" are trademarks of Proton AG.
