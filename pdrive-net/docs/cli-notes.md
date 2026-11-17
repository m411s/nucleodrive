# proton-drive CLI: checked behaviour

Status: **not yet checked against the real CLI.** Everything below marked _assumed_ comes from the
Python prototype's guesses and Proton's blog post. Fill this in once the CLI is in `~/.local/bin`, and save
raw outputs under `tests/Fixtures/cli/` so the parser tests use real data.

| Question | Assumed | Checked | Code that depends on it |
|---|---|---|---|
| `filesystem list <path> --json` schema | bare list or `{items:[…]}`, keys `name`/`type`/`size` | | `ListingParser` |
| Does `filesystem mkdir` exist? | best guess; failure ignored | | `ProtonDriveCli.MkdirAsync`, `SyncEngine` |
| `upload --conflict-strategy` values | `skip` documented; `replace`/revision unknown | | `DriveCliOptions.ConflictStrategy`, config `conflict_strategy` |
| Re-upload of a changed file without a strategy | unknown (replace / revision / duplicate?) | | default for `conflict_strategy` |
| Signed-out / expired-session error wording | contains "not logged in", "sign in", "session expired"… | | `AuthHeuristics` |
| Network error wording | anything else | | `AuthHeuristics` (must NOT match) |
| Does upload print progress? Format? | unknown; lines are forwarded raw | | `IDriveClient.UploadAsync(output)` |
| Exit codes | non-zero = failure | | `ProtonDriveCli.RunAsync` |
| Keyring reachable from `systemd --user` service | unknown | | `pdrive.service`, daemon "Sign in required" |
| `auth login` without a terminal (from GUI) | opens browser, blocks until done | | `LoginAsync` |

## Commands to run

```bash
proton-drive --help
proton-drive filesystem --help
proton-drive filesystem list --help
proton-drive filesystem upload --help
proton-drive filesystem download --help
proton-drive filesystem mkdir --help
proton-drive auth --help
```

```bash
proton-drive filesystem list /my-files --json > tests/Fixtures/cli/list-root.json
```

```bash
proton-drive auth logout && proton-drive filesystem list /my-files --json; echo "exit=$?"
```

```bash
systemd-run --user --wait --pipe proton-drive filesystem list /my-files --json | head
```
