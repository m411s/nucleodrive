# Performance review

Measured 2026-11-17 on this machine (x86-64, real Drive, cli-drive 0.9.0).

## Measurements

| What | Result | Meaning |
|---|---|---|
| `proton-drive --version` (no network) | 105–260 ms | Starting the 96 MB CLI |
| `proton-drive filesystem list` (one folder) | **1.2–1.8 s** | Almost all network and sign-in; **every CLI call costs ~1.5 s** |
| Remote crawl, 36 folders, 4 in parallel | 28 s | ≈ folders × 1.5 s ÷ 4 |
| Local snapshot, 50,000 files / 1,050 folders | **~80 ms** | Local scanning is negligible |
| `pdrivectl sync status` | ~110 ms | Startup + one IPC round trip |
| Daemon memory | 13 MB idle → ~100 MB RSS after syncing | Mostly shared libraries and GC headroom |
| Tray app memory | 165 MB RSS at start (40 MB own) → **325 MB** once the window has been opened | The hidden window keeps its rendering resources |

**The rule:** the number of CLI calls decides everything else. Local work is nearly free.

## Where CLI calls are spent (after the 2026-11-17 optimisations)

| Operation | Before | Now |
|---|---|---|
| Check one folder on Drive | 1 per folder (crawl) | same, hourly by default; checks back off while idle (1, 1, 2, 2, 4 h) |
| Local edits: check Drive before uploading | **1 `info` per changed file**, one after another | 1 listing per changed folder, up to 4 at once |
| Upload N changed files in one folder | 1 upload + 1 listing | same |
| New local folder | create-folder + info | 1 (`create-folder --json` returns the ID) |
| New local folder tree (K folders, e.g. copied in) | **3K** (create, upload, listing per folder) | 1 upload + 1 info + a crawl of K listings, 4 at once |
| Download N changed files | **N** | 1 per Drive folder (up to 50 files), up to 4 folders at once |
| Trash N items | 1 | 1 |
| Rename/move | 1–2 each | same |
| First sync of a folder | 1 download + 1 crawl | same |

Examples at ~1.5 s per CLI run:

| You do | Before | Now |
|---|---|---|
| Save 20 files in one folder | ~30 s of checks, then upload | ~1.5 s check, then upload |
| Copy a folder with 20 subfolders in | ~90 s | ~10 s |
| 100 new phone photos in 5 dated folders | ~150 s | ~3 s (all five folders at once) |
| Nothing changes for a day | 96 checks of Drive (every 15 min) | ~9 (hourly, backing off to 4 h) |

## Optimisations

| # | Change | Status |
|---|---|---|
| 1 | Batch downloads per folder (`filesystem download a b c … staging/`), placed one by one with the usual re-check | done |
| 2 | Upload new local folder trees in one call (`upload -d merge folder parent`), then one crawl to record them. Only when a fresh look at the folder finds exactly what the plan uploads (no excluded, settling or linked items; the CLI refuses links and fails the whole upload, checked); otherwise folder by folder | done |
| 3 | Adaptive check interval: hourly by default, up to 4 h while idle (`sync.remote_interval_minutes` 60, `sync.remote_interval_max_minutes` 240); Proton asks third-party apps not to traverse the file tree frequently | done |
| 4 | Tray memory: the window is built on first open and released when closed to the tray | done |
| 5 | Files page cache: last listing shown instantly, refreshed in the background | done; prefetching the parent is open |
| 6 | `create-folder --json` instead of a separate `info` (checked with cli-drive 0.9.0) | done |
| 7 | Local passes list each changed folder once, in parallel, instead of `info` per file | done |
| 8 | Parallel downloads across folders (up to `parallel_listings`) | done |
| 9 | **ReadyToRun** publish for the app and daemon (`-p:PublishReadyToRun=true`) | open: faster first start, ~30% larger packages; measure first |
| 10 | Parallel uploads across folders | open: same pattern as downloads; uploads are usually one folder at a time |
| 11 | Local passes snapshot only the folders the watcher reported | open, small: local scans take ~80 ms per 50k files |

Reading files: pdrive reads file contents only to compare SHA-1 when a file changed on both sides
with the same size (and on a first sync into a folder that already has files). Scans use `statx`
metadata only; the CLI reads what it uploads.

Not worth doing now: the local scanner, SQLite (batched transactions already) and IPC (sub-millisecond).
