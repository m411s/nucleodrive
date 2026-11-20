# Privacy policy

*Last updated: 17 November 2026*

NucleoDrive is an open-source, unofficial Linux client for Proton Drive. It's a volunteer project, not a company,
and it has no servers of its own.

## In short

- **NucleoDrive collects nothing.** No telemetry, analytics, crash reports, advertising, tracking or accounts.
- **Your files go only to Proton**, through Proton's own `proton-drive` command-line tool, which encrypts
  them on your computer before they're uploaded.
- **NucleoDrive never sees your Proton password.** You sign in on Proton's own page in your browser.
- **Everything NucleoDrive stores stays on your computer**, readable only by your user account.

## What NucleoDrive connects to

NucleoDrive itself only connects to Proton:

| Where | When | What is sent |
|---|---|---|
| Proton Drive, through the `proton-drive` CLI | Browsing, syncing, uploading, downloading, signing in and out | What Proton needs to run Drive for you: your session and your encrypted files and metadata. File and folder names are encrypted too. |
| `proton.me/download/drive/cli/` | When you open Settings or run setup (to check for a newer CLI), and when you install or update the CLI | A normal web request with the user agent `nucleodrive`. Like any website, Proton sees your IP address. |

What the `proton-drive` CLI sends is up to Proton and covered by
[Proton's privacy policy](https://proton.me/legal/privacy). NucleoDrive runs it with your files and folder
names as arguments, and nothing else.

Building packages with `package.sh` downloads `appimagetool` from GitHub; that doesn't happen while NucleoDrive
is running.

## What NucleoDrive stores on your computer

| Location | Contents |
|---|---|
| `~/.config/nucleodrive/` | Your settings: which folders sync or back up, where to, and how often |
| `~/.local/state/nucleodrive/state.db` | What was last synced: paths, sizes, change times, file IDs on Drive and checksums. This lets NucleoDrive tell what changed. |
| `$XDG_RUNTIME_DIR/nucleodrive/` | Control sockets between the app and the background service, removed at logout |
| `.nucleodrive-staging/` in your sync folder | Downloads in progress, moved into place when complete |
| The system journal | The service's log: folder and file names it synced, and error messages. View it with `journalctl --user -u nucleodrive`. It's kept as long as your system's journal settings say. |
| The system keyring (GNOME Keyring, KWallet, …) | Your Proton session, stored there by the `proton-drive` CLI, not by NucleoDrive |

NucleoDrive's folders and files are created readable only by you (`0700` and `0600`).

When sync removes a file locally because it was deleted on Drive (or lost a conflict), the file goes to your
desktop Trash, not straight to deletion. Files removed on Drive go to Drive's trash.

## Removing your data

- **Sign out:** Settings › Sign out, or `proton-drive auth logout`. This removes the session from your keyring.
- **Uninstall:** `./uninstall.sh --purge` removes NucleoDrive with its settings and sync state, and the
  `proton-drive` CLI if NucleoDrive installed it. Without `--purge`, your settings and the CLI are kept. With the
  .deb, `sudo apt purge nucleodrive` removes the program; your settings and state are in your home folder (above).
  Your synced files are never removed.
- **Your files on Drive** are managed by Proton; see [Proton's privacy policy](https://proton.me/legal/privacy).

## Changes

Changes to this policy are made in this file. Its history is public in the repository.

## Contact

Questions: open an issue at <https://github.com/m411s/nucleodrive/issues>. NucleoDrive isn't affiliated with Proton AG,
so questions about your Proton account go to [Proton support](https://proton.me/support).
