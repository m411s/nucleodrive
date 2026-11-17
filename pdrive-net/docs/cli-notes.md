# proton-drive CLI: checked behaviour

Checked against **cli-drive 0.9.0+75612299 (SDK js 0.22.2)**, the build at `pdrive-net/cli/proton-drive`
(sha256 `4f66f984…a332501`, not committed). It's Proton's Drive SDK CLI built from their JavaScript client.

Source: `--help` for every command used, plus read-only runs while signed out. Everything that
needs a signed-in session is still open (bottom table).

## Confirmed

| Question | Answer | Code |
|---|---|---|
| Create a folder | **`filesystem create-folder parentPath name`**. There is no `mkdir`; the prototype's guess always failed silently. | `ProtonDriveCli.MkdirAsync` |
| Upload conflicts | `-f/--file-conflict-strategy`: `create-new-revision`, `rename`, `replace` (trashes the remote copy), `skip`. `-d/--folder-conflict-strategy`: `merge`, `rename`, `replace`, `skip`. **Without them the CLI prompts**, which would stall a non-interactive call until its timeout. | Always passed. Files default to `create-new-revision` (old content stays in version history; config `conflict_strategy` overrides, validated against these values); folders always `merge`. |
| Same content | "Files with the same content are automatically skipped." Re-uploading unchanged files is harmless. | (safety net for migration) |
| Download conflicts | `-f`: `rename`, `remove` (deletes the local copy), `skip`; `-d`: `merge`, `rename`, `remove`, `skip`. Also prompts without them. | Always `-f rename -d merge`: never deletes local files. |
| JSON output | `-j/--json` is a global option. | `filesystem list PATH --json` |
| Paths | POSIX paths always; `/` lists top-level sections; root is `/my-files`. **A `/` inside a node name is escaped as `\/`.** Node UIDs work where names can't be decrypted or clash. | `RemotePath.EscapeName/UnescapeName`; listing paths and remote folders are escaped. |
| Signed out | stderr `You need to login first`, exit code 1, nothing on stdout (also with `--json`). | Matched by `AuthHeuristics` ("login"). |
| Session storage | Loaded "from secrets" (system keyring), entry `ch.proton.drive/drive-sdk-cli/auth-session`. | |
| Sign-in | `auth login` opens a browser (sign-in can happen on another device). "Keep the terminal open until authentication completes." | `LoginAsync` (300 s timeout) |
| Version check | `--version` prints the CLI and SDK versions and whether it's the latest. | |
| Other commands | `info`, `size`, `rename`, `copy`, `move`, `trash`, `restore`, `delete`, `empty-trash`, sharing, invitations, albums, photos, takeout. | Not used yet: future browser features (rename, trash, share). |

## Still open (needs a signed-in session)

| Question | How to check | Code that depends on it |
|---|---|---|
| `list --json` schema (keys for name, type, size, path) | `proton-drive filesystem list /my-files --json > tests/Fixtures/cli/list-root.json` | `ListingParser` (tolerant today) |
| Network-error wording (must NOT look like sign-in) | run a list with the network off | `AuthHeuristics` |
| Upload progress output | upload a large file with `-v` and see what's printed | `UploadAsync(output)` |
| `create-folder` when the folder exists | create the same folder twice in a test folder | engine ignores the failure; confirm it's harmless |
| Backslash in a local name, e.g. `a\b.txt` | upload one into a test folder | `RemotePath.EscapeName` escapes only `/` |
| Keyring reachable from the systemd user service | `systemd-run --user --wait --pipe proton-drive filesystem list /my-files --json` | daemon "Sign in required" |
| `auth login` without a terminal (from the app) | Sign in from the app's Files tab | `LoginAsync` |
