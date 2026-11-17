# Security review

To report a vulnerability, see the [security policy](../../SECURITY.md). What pdrive stores and sends is in
the [privacy policy](../../PRIVACY.md).

Reviewed 2026-11-17 (commit a7bbc7f and later). Scope: the daemon, the sync engine, the app, the CLI
wrapper and installer, `install.sh`, the .deb/AppImage packaging and the GitHub workflows.

## Threat model

- **Your Drive content is untrusted input.** Names and folder structures can come from shared
  folders, other devices or a compromised account. They must never make pdrive write, move or delete
  outside the sync folder, or run anything.
- **Other users on the same computer** must not be able to control the daemon or read your config.
- **Downloaded programs** (the proton-drive CLI, appimagetool) must be verified before use.
- **Out of scope:** malware already running as you (it can do anything you can), and Proton's own
  servers and CLI.

Dependency scan (`dotnet list package --vulnerable --include-transitive`): **no known vulnerabilities**.

## Fixed

| Severity | Issue | Fix |
|---|---|---|
| **High** | **Writing through a local symlink.** If you had a symlink inside the sync folder pointing elsewhere (e.g. `Docs/keys → ~/.ssh`), a Drive folder with the same name could have its files downloaded *through* the link, e.g. into `~/.ssh/authorized_keys`. Reproduced in a test before the fix. | The planner leaves everything under any ignored item alone (symlinks are ignored). The executor refuses any local write, move, folder creation or trashing whose path passes through a symlink below the sync folder, checked again right before a download is moved into place. `SecurityTests` |
| Medium | **CLI option injection.** A file or folder named like `--help` or `-rf` was passed as a bare argument to `rename` / `create-folder`, so the CLI could read it as an option. | Names are passed after `--` (end of options), which cli-drive 0.9.0 supports for `create-folder` and `rename`, so such files sync normally; paths are always safe because they start with `/`. `OptionInjectionTests` |
| Medium | **AppImage login service from a shared location.** Started from `/tmp` or another world-writable folder, the AppImage registered itself as a systemd service and autostart entry. Anyone able to replace that file would run code as you at every login. | Integration is skipped, with an explanation, if the AppImage or its folder is writable by others or not owned by you. Paths with `"`, `%`, `\` or line breaks (which change the meaning of unit and desktop files) are refused, also in `install.sh`. |
| Medium | **Readable config and state.** `~/.config/pdrive` and `~/.local/state/pdrive` were 0775/0664 under the default umask; they list your folders and what is synced where. | Folders 0700, files 0600 (config, state database), existing ones tightened at daemon start. |
| Low | **Control socket peers.** The daemon trusted anyone who could connect. | It checks `SO_PEERCRED` and refuses other users, as defence in depth behind the 0700 runtime folder and 0600 socket. |
| Low | **Undecryptable Drive names** would sync as placeholders ("(name unavailable) …") and be uploaded back as new files. | Kept online-only. |
| Hardening | Service units | `NoNewPrivileges`, `LockPersonality`, `RestrictRealtime`, `RestrictSUIDSGID`, `SystemCallArchitectures=native` (install.sh, .deb, AppImage). |
| Hardening | GitHub Actions referenced by movable tags | Pinned to commit SHAs (Dependabot keeps them current). Workflows already use least-privilege tokens and pass tag names through environment variables, not `${{ }}` in scripts. |

## Already in place

- The CLI is always run with an argument list, never through a shell. Remote paths always start with `/my-files`.
- CLI installer: downloads only from `https://proton.me/download/drive/cli/`, verifies Proton's published
  SHA-512, checks it's an ELF executable and that `--version` runs, then installs atomically. Never
  replaces an existing `~/.local/bin/proton-drive` without asking.
- Sync: deletions go to the Drive trash or the desktop Trash, never erased. Mass deletions wait for
  confirmation. A failed listing aborts the pass instead of looking like deletions. Missing or unmounted folders are errors.
  Downloads go through staging and are re-checked before replacing anything. Symlinks are never followed or uploaded.
- Single instance via `flock`. The control socket is in `$XDG_RUNTIME_DIR` (0700), and the socket itself is 0600.
- No credentials are handled by pdrive; the CLI keeps its session in the system keyring.
- appimagetool is pinned by SHA-256. Nothing else is downloaded at install time (Proton's badge icon was dropped).

## Proposed (not done yet)

| Priority | Proposal | Why |
|---|---|---|
| 1 | **Sign releases.** Add GitHub build-provenance attestations (`actions/attest-build-provenance`) and a minisign/GPG signature on `SHA256SUMS`. | Users can verify downloads came from this repo's CI. |
| 2 | Name collisions: two Drive files whose names differ only in Unicode normalisation (NFC/NFD, e.g. from macOS) map to different local files. Detect them and keep them online-only with a warning. | Avoids confusing duplicates and re-upload loops. |
| 2 | Stricter sandbox for the service when user namespaces are available: `ProtectSystem=strict`, `ProtectHome=read-only` plus `ReadWritePaths=` for the sync folder, backup sources, state and runtime. Needs the backup folders listed, so generate the unit from config. | Limits damage from a bug in the daemon or the CLI. |
| 3 | Deletion guard as a percentage as well (e.g. more than 20% of a folder), not only a count. | Catches mass deletions in small folders. |
| 3 | If Proton starts signing CLI releases, verify the signature as well as the checksum. | Today the checksum comes from the same server as the binary. |
