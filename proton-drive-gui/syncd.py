#!/usr/bin/env python3
"""Background backup daemon + small control CLI.

  syncd.py run                       run the daemon (what the systemd service does)
  syncd.py add LOCAL REMOTE          add a folder, e.g. add ~/Documents /my-files/Documents
  syncd.py list | status
  syncd.py now | pause | resume      talk to the running daemon
"""
import argparse
import os
import signal
import sys
import threading
import time
from pathlib import Path

import syncengine as se
from protondrive import DriveCLI, DriveError


def run_daemon():
    wake, stop = threading.Event(), threading.Event()
    # Handlers go in before the lock publishes our PID, so an early SIGUSR1 can't kill us.
    signal.signal(signal.SIGUSR1, lambda *_: wake.set())
    signal.signal(signal.SIGTERM, lambda *_: (stop.set(), wake.set()))
    signal.signal(signal.SIGINT, lambda *_: (stop.set(), wake.set()))
    lock = se.acquire_daemon_lock()
    if lock is None:
        sys.exit(f"Another backup daemon is already running (pid {se.daemon_pid() or '?'}).")
    se.write_status("idle", "Starting…", pid=os.getpid())
    state = se.load_state()
    interval = se.DEFAULT_CONFIG["interval_minutes"]

    while not stop.is_set():
        try:
            cfg = se.load_config()
            interval = cfg["interval_minutes"]
            if se.is_paused():
                se.write_status("paused", "Paused")
            elif not cfg["folders"]:
                se.write_status("idle", "No folders configured (syncd.py add LOCAL REMOTE)")
            else:
                cli = DriveCLI()
                if not cli.is_logged_in():
                    se.write_status("login", "Sign in required (open the Proton Drive app)")
                else:
                    se.write_status("syncing", "Syncing…")
                    res = se.run_once(
                        cfg, cli, state,
                        should_stop=lambda: stop.is_set() or se.is_paused(),
                        progress=lambda m: se.write_status("syncing", m),
                    )
                    now = int(time.time())
                    if res["errors"]:
                        se.write_status("error", f"{len(res['errors'])} error(s): {res['errors'][0]}", last_sync=now)
                    else:
                        msg = f"Up to date ({res['uploaded']} uploaded)" if res["uploaded"] else "Up to date"
                        se.write_status("idle", msg, last_sync=now)
        except se.ConfigError as e:
            # Keep running on the last good interval; the next cycle re-reads the file.
            se.write_status("error", str(e))
        except DriveError as e:
            se.write_status("error", (str(e).splitlines() or ["proton-drive failed"])[0])
        except Exception as e:  # keep the daemon alive
            se.write_status("error", f"Unexpected: {e}")
        wake.wait(max(1, interval) * 60)
        wake.clear()
    se.write_status("idle", "Stopped", pid=0)
    lock.close()


def signal_daemon():
    pid = se.daemon_pid()
    if not pid:
        print("Daemon doesn't appear to be running.", file=sys.stderr)
        return False
    try:
        os.kill(pid, signal.SIGUSR1)
        return True
    except ProcessLookupError:
        print("Daemon doesn't appear to be running.", file=sys.stderr)
        return False


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("run")
    a = sub.add_parser("add")
    a.add_argument("local")
    a.add_argument("remote")
    for c in ("list", "status", "now", "pause", "resume"):
        sub.add_parser(c)
    args = ap.parse_args()

    if args.cmd in (None, "run"):
        run_daemon()
    elif args.cmd == "add":
        try:
            cfg = se.load_config()
        except se.ConfigError as e:
            sys.exit(f"{e}\nFix or remove the file, then try again.")  # never overwrite it
        local = str(Path(args.local).expanduser().resolve())
        if not Path(local).is_dir():
            sys.exit(f"Not a folder: {local}")
        if not args.remote.startswith(se.ROOT_PREFIX):
            sys.exit(f"Remote path should start with {se.ROOT_PREFIX}")
        cfg["folders"].append({"local": local, "remote": args.remote.rstrip("/")})
        se.save_config(cfg)
        print(f"Added {local} -> {args.remote}")
        signal_daemon()
    elif args.cmd == "list":
        try:
            folders = se.load_config()["folders"]
        except se.ConfigError as e:
            sys.exit(str(e))
        for f in folders:
            print(f"{f['local']} -> {f['remote']}")
    elif args.cmd == "status":
        s = se.read_status()
        print(f"{s.get('state', 'unknown')}: {s.get('message', '')}")
    elif args.cmd == "now":
        signal_daemon()
    elif args.cmd == "pause":
        se.PAUSED.parent.mkdir(parents=True, exist_ok=True)
        se.PAUSED.touch()
        signal_daemon()
    elif args.cmd == "resume":
        se.PAUSED.unlink(missing_ok=True)
        signal_daemon()


if __name__ == "__main__":
    main()
