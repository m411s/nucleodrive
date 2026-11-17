# Planned

## Settings page: install the CLI and sign in

Requested 2026-11-17. A **Settings** tab (or dialog) in the app that covers first-time setup without
a terminal:

- **proton-drive CLI**
  - Shows whether it's found, where (`BinaryLocator`), and its version (`proton-drive --version`,
    which also says whether it's the latest).
  - **Download / Update** button: fetch the official Linux build from Proton (find the direct
    download URL behind https://proton.me/download/drive/cli; HTTPS only, Proton's domain only), keep
    it in a new empty folder (e.g. `~/.local/lib/pdrive/cli/`), check it's an x86-64/arm64 ELF and that
    `--version` runs, then `chmod +x` and link it as `~/.local/bin/proton-drive`. Check whether Proton
    publishes a checksum or signature to verify it against.
  - Today the CLI lives in `pdrive-net/cli/` with a symlink in `~/.local/bin`; the install should
    replace that cleanly (ask before overwriting an existing link or file).
- **Account**
  - Signed-in state (a cheap `filesystem list /my-files`), with **Sign in** (`auth login`, opens the
    browser; `ProtonDriveCli.LoginAsync` already exists and the Files tab offers it when signed out) and
    **Sign out** (`auth logout`, with a confirmation since sync stops until you sign in again).
- After installing or signing in, poke the daemon (Sync now) so the status updates straight away.
- Nice to have: the other `config.json` settings (sync folder location, check interval, parallel
  listings, deletion threshold, exclude patterns), so the file never needs editing by hand.
