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

## Where CLI calls are spent today

| Operation | Calls |
|---|---|
| Check one folder on Drive | 1 per folder (crawl) |
| Upload N changed files in one folder | 1 upload + 1 listing (batched) ✓ |
| Download N changed files | 1 per Drive folder (up to 50 files per run) ✓ |
| New local folder with K subfolders | **2K** (create-folder + info each) + uploads |
| Trash N items | 1 (batched) ✓ |
| Rename/move | 1–2 each |
| First sync of a folder | 1 download + 1 crawl ✓ |

## Proposed optimisations (largest benefit first)

| # | Change | Expected effect |
|---|---|---|
| 1 | **Batch downloads per folder**: `filesystem download a b c … staging/` in one call, then move each file into place with the usual re-check. | 100 new files from your phone: ~150 s → ~2–5 s per folder *(done)* |
| 2 | **Upload new local folder trees in one call** (`upload -d merge` takes folders), then one crawl of that folder to record it. | Copying a folder with 20 subfolders: ~60 s of create-folder/info → one upload + crawl |
| 3 | **Adaptive check interval**: back off when checks find nothing (15 → 30 → 60 min), go back to 15 after any change or Sync now. | Fewer CLI runs and less network use when idle; same response when busy |
| 4 | **Tray memory**: create the main window only when first opened, release it when closed in tray mode, then compact the heap. | The window's ~150 MB of rendering memory is only used while it's open *(done: the window is built on first open and released when closed to the tray)* |
| 5 | **Files page cache**: show the last listing of a folder instantly, refresh in the background. | Going back to a folder feels instant instead of ~1.5 s per click *(done)*; prefetching the parent is still open |
| 6 | Parse `create-folder --json` output (if the CLI prints the node) instead of a separate `info` call. | Halves the calls per new folder (needs checking with the real CLI) |
| 7 | **ReadyToRun** publish for the app and daemon (`-p:PublishReadyToRun=true`). | Faster first window open and first sync after login, at ~30% larger packages |
| 8 | Parallel downloads (up to `parallel_listings`) for large initial merges. | Faster when many separate folders change at once |
| 9 | Local passes snapshot only the folders the watcher reported. | Small; local scans are already ~80 ms per 50k files |

Not worth doing now: the local scanner, SQLite (batched transactions already) and IPC (sub-millisecond).
