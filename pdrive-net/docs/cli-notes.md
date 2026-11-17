# proton-drive CLI: checked behaviour

Checked on 2026-11-17 against **cli-drive 0.9.0+75612299 (SDK js 0.22.2)**, the build at
`pdrive-net/cli/proton-drive` (sha256 `4f66f984…a332501`, not committed). It's Proton's Drive SDK CLI
built from their JavaScript client.

How: `--help` for every command used, read-only runs signed out and signed in, and test writes in a
throwaway `/my-files/pdrive-test` folder, which was moved to the Drive trash afterwards.

## Commands and options

| Topic | Behaviour | Code |
|---|---|---|
| Create a folder | `filesystem create-folder parentPath name`. **There is no `mkdir`.** Prints the new node; if it exists: stderr `A file or folder with that name already exists`, exit 1; missing parent: `Node not found: <name>`, exit 1. | `ProtonDriveCli.MkdirAsync`; the engine ignores "exists" and creates parents first |
| Upload conflicts | `-f`: `create-new-revision`, `rename`, `replace` (trashes the remote copy), `skip`. `-d`: `merge`, `rename`, `replace`, `skip`. **Without them the CLI prompts.** | Always passed: files `create-new-revision` (config `conflict_strategy` overrides, validated), folders `merge` |
| Unchanged content | Skipped automatically: `Skipped: 1 items` / `- hello.txt`, exit 0. Re-uploading after a migration is harmless. | |
| Changed content | With `create-new-revision`: uploaded as a new revision; `totalStorageSize` then covers all revisions. | |
| Upload output | No progress when stdout isn't a terminal; only `Transfer summary:` / `Uploaded: N items (size)`. 38 MiB took 8 s. | `UploadAsync(output)` forwards the summary lines |
| Partial upload failure | The good files are uploaded; stdout summary has `Failed: N items` then `  - name: Error: …`; stderr `N item(s) failed to upload`; exit 1. | `CliErrorText` puts the file's error first; the engine retries one by one (good files are then skipped as unchanged) |
| Crash (e.g. missing local file) | Nothing uploaded; stack trace and `ENOENT: …` on **stdout**; stderr is only a `=====` rule; exit 1. | `CliErrorText` picks the `ENOENT:` / `…Error:` line |
| Names starting with `-` | `create-folder` and `rename` read them as options (`Unknown option '-y'`) unless they come after `--`; with `--` they work (checked 2026-11-17). | pdrive always passes `--` before those arguments |
| Several downloads at once | `filesystem download [-f …] [-d …] path... localFolder` | Sync downloads up to 50 files of one folder per run |
| Download conflicts | `-f`: `rename`, `remove` (deletes the local copy), `skip`; `-d`: `merge`, `rename`, `remove`, `skip`. Prompts without them. A second download of `hello.txt` produced `hello (1).txt`. Missing remote: `Node not found: nope.txt`, exit 1. Proton Docs/Sheets are skipped. | Always `-f rename -d merge`; the app hides Download for Docs/Sheets |
| Backslash in a local name | `back\slash.txt` uploads and is listed with that exact name. | `RemotePath.EscapeName` escapes only `/` |
| `/` in a node name | Escaped as `\/` in paths (per help). Node UIDs work where a name can't be decrypted or clashes. | Listing paths are escaped; undecryptable names use the uid |
| JSON | `-j/--json` is a global option. Without it, create-folder prints a JS object dump. | `filesystem list PATH --json` |
| Rename / move / trash | `rename path newName` (in place), `move src... targetParent`, `trash path...` (recoverable; prints `✅ name`). | Two-way sync: renames, moves, deletions |
| Info | `info path --json`: one node object, same shape as a listing item. | Sync: checking changed paths without a crawl |
| Other commands | `size`, `copy`, `restore`, `delete`, `empty-trash`, sharing, invitations, albums, photos, takeout. | Not used |
| Speed | `list` takes ~2 s per folder (median 1.9 s, worst 30 s over 150 folders); each call starts fresh. Folder `modificationTime` does **not** change when contents change, so remote changes need a full crawl. | Sync: parallel crawl, interval-based remote checks |

## Two-way sync, end to end (2026-11-17, installed daemon, real Drive)

Test folder `/my-files/pdrive-sync-test` ⇄ `~/proton-drive/pdrive-sync-test`, trashed on both sides afterwards.

| Step | Result |
|---|---|
| First sync (4 files on Drive) | all downloaded in 15 s, with Drive's modification times |
| Local: new file, edit, rename, delete, new folder with a file | all on Drive within ~34 s; the rename kept its original revision (no re-upload); the deleted file went to the Drive trash |
| Drive (via CLI): new file, edit, rename, trash | all applied locally within 14 s of `pdrivectl sync now`; the trashed file is in the desktop Trash |
| Edited on both sides | newer local edit → uploaded; newer Drive edit → downloaded, the older local copy moved to the desktop Trash |
| Daemon log | no warnings or errors |

## `filesystem list --json` schema

A bare JSON array of nodes. Fixture: `tests/Pdrive.Cli.Tests/Fixtures/list-folder.json` (from the
test folder; uids and emails replaced).

| Field | Meaning |
|---|---|
| `uid`, `parentUid` | node ids (`volume~node`), usable in paths |
| `name` | `{"ok": true, "value": "file.txt"}`; `ok: false` when it can't be decrypted |
| `type` | `file` or `folder` (top-level `/` also lists sections) |
| `mediaType` | files only, e.g. `video/mp4`, `application/vnd.proton.doc` / `.sheet` |
| `activeRevision.claimedSize` | **content size**, the one to show |
| `activeRevision.storageSize` | encrypted size of the current revision |
| `totalStorageSize` | all revisions + overhead (190 for a 21-byte file with 2 revisions) |
| `creationTime`, `modificationTime`, `activeRevision.claimedModificationTime` | ISO 8601 |
| also | `keyAuthor`, `nameAuthor`, `ownedBy.email`, `directRole`, `isShared`, `isSharedByUrl`, `folder.claimedModificationTime`, `treeEventScopeId`, `activeRevision.claimedDigests.sha1` |

## Session and errors

| Topic | Behaviour | Code |
|---|---|---|
| Signed out | stderr `You need to login first`, exit 1, nothing on stdout (also with `--json`). | `AuthHeuristics` ("login") |
| Session storage | System keyring, entry `ch.proton.drive/drive-sdk-cli/auth-session`. | |
| From a systemd user service | **Works**: `systemd-run --user --wait --pipe proton-drive filesystem list /my-files --json` listed the root. | `pdrive.service` |
| Sign-in | `auth login` opens a browser (or use another device); keep the process running until done. | `LoginAsync` (300 s) |
| Verbose | `-v` prints coloured `DEBUG/INFO` logs to stderr. | not used |

## Still open

- **Network-error wording**: not tested (it needs the network turned off). Anything that doesn't
  look like a sign-in problem is shown as an error, which is the safe default.
- **`auth login` started from the app** (no terminal): should work (it only opens a browser), but untried.
- **Name with `/` from Drive**: the escaping follows the help text; no such node was available to test.
