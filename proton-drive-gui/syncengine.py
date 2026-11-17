"""One-way backup engine: local folders -> Proton Drive, via the proton-drive CLI.

Only uploads new/changed files. It never deletes anything on either side, and
remembers what it uploaded in a small state file (not by inspecting Drive).
"""
from __future__ import annotations

import copy
import fcntl
import fnmatch
import json
import os
import posixpath
import time
from pathlib import Path

from protondrive import DriveCLI, DriveError

CONF_DIR = Path(os.environ.get("PDG_CONFIG_DIR") or
                Path(os.environ.get("XDG_CONFIG_HOME", "~/.config")).expanduser() / "proton-drive-gui")
STATE_DIR = Path(os.environ.get("PDG_STATE_DIR") or
                 Path(os.environ.get("XDG_STATE_HOME", "~/.local/state")).expanduser() / "proton-drive-gui")
CONFIG = CONF_DIR / "config.json"
STATE = STATE_DIR / "state.json"
STATUS = STATE_DIR / "status.json"
PAUSED = STATE_DIR / "paused"
LOCK = STATE_DIR / "daemon.lock"

DEFAULT_CONFIG = {
    "interval_minutes": 15,
    "min_age_seconds": 5,  # skip files still being written
    "exclude": [".*", "*~", "*.tmp", "*.part", "*.crdownload"],
    "folders": [],  # [{"local": "~/Documents", "remote": "/my-files/Documents"}]
}
BATCH = 50  # files per CLI call, keeps argv short
BATCH_BYTES = 256 * 1024 * 1024  # bigger files go in a call of their own
ROOT_PREFIX = "/my-files"


class ConfigError(Exception):
    pass


def _atomic_write(path: Path, data: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, indent=2))
    os.replace(tmp, path)


def _is_number(v) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _validate(cfg: dict):
    def bad(msg):
        raise ConfigError(f"{CONFIG}: {msg}")
    if not _is_number(cfg["interval_minutes"]) or cfg["interval_minutes"] <= 0:
        bad("'interval_minutes' must be a positive number")
    if not _is_number(cfg["min_age_seconds"]) or cfg["min_age_seconds"] < 0:
        bad("'min_age_seconds' must be a number >= 0")
    if not isinstance(cfg["exclude"], list) or not all(isinstance(p, str) for p in cfg["exclude"]):
        bad("'exclude' must be a list of patterns")
    if not isinstance(cfg["folders"], list):
        bad("'folders' must be a list")
    for f in cfg["folders"]:
        if not (isinstance(f, dict) and isinstance(f.get("local"), str) and isinstance(f.get("remote"), str)):
            bad(f"each folder needs string 'local' and 'remote' (got {f!r})")


def load_config() -> dict:
    """Defaults merged with config.json. Raises ConfigError if the file is unreadable or malformed."""
    cfg = copy.deepcopy(DEFAULT_CONFIG)
    try:
        user = json.loads(CONFIG.read_text()) if CONFIG.exists() else {}
    except (OSError, ValueError) as e:
        raise ConfigError(f"Can't read {CONFIG}: {e}") from e
    if not isinstance(user, dict):
        raise ConfigError(f"{CONFIG}: expected a JSON object")
    cfg.update(user)
    _validate(cfg)
    return cfg


def save_config(cfg: dict):
    _atomic_write(CONFIG, cfg)


def load_state() -> dict:
    try:
        return json.loads(STATE.read_text())
    except (OSError, json.JSONDecodeError):
        return {}


def write_status(state: str, message: str, **extra):
    cur = read_status()
    cur.update(state=state, message=message, **extra)
    _atomic_write(STATUS, cur)


def read_status() -> dict:
    try:
        return json.loads(STATUS.read_text())
    except (OSError, json.JSONDecodeError):
        return {}


def is_paused() -> bool:
    return PAUSED.exists()


def acquire_daemon_lock(attempts: int = 10):
    """Take the single-instance lock and record our PID in it.

    Returns the open lock file (keep it open for the daemon's lifetime), or None
    if another daemon holds it. Retries briefly because daemon_pid() takes a
    shared lock for an instant when it checks.
    """
    LOCK.parent.mkdir(parents=True, exist_ok=True)
    f = open(LOCK, "a+")  # not inherited by the CLI subprocesses (PEP 446)
    for i in range(attempts):
        try:
            fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            break
        except BlockingIOError:
            if i == attempts - 1:
                f.close()
                return None
            time.sleep(0.1)
    f.seek(0)
    f.truncate()
    f.write(str(os.getpid()))
    f.flush()
    return f


def daemon_pid() -> int | None:
    """PID of the running daemon, or None.

    Trusts the PID only while its lock is held, so a PID left behind by a crashed
    daemon (and possibly reused by another process) is never signalled.
    """
    try:
        f = open(LOCK)
    except OSError:
        return None
    with f:
        try:
            fcntl.flock(f, fcntl.LOCK_SH | fcntl.LOCK_NB)
        except BlockingIOError:
            try:
                return int(f.read().strip())
            except ValueError:
                return None  # daemon is starting and hasn't written its PID yet
        fcntl.flock(f, fcntl.LOCK_UN)
        return None


def effective_status() -> dict:
    """What the tray should show: status.json, overridden when the daemon isn't
    running (its last status would be stale) or when paused."""
    s = read_status()
    if daemon_pid() is None:
        s["state"], s["message"] = "stopped", "Backup daemon not running"
    elif is_paused():
        s["state"], s["message"] = "paused", "Paused"
    return s


def scan(local: Path, exclude: list[str], min_age: float) -> dict[str, list]:
    """Return {relative_path: [mtime_ns, size]} for regular files, skipping excluded/too-new ones."""
    out = {}
    now = time.time()
    for root, dirs, files in os.walk(local, followlinks=False):
        dirs[:] = [d for d in dirs if not any(fnmatch.fnmatch(d, pat) for pat in exclude)]
        for name in files:
            if any(fnmatch.fnmatch(name, pat) for pat in exclude):
                continue
            full = Path(root) / name
            try:
                if full.is_symlink():
                    continue
                st = full.stat()
            except OSError:
                continue
            if now - st.st_mtime < min_age:
                continue
            out[str(full.relative_to(local))] = [st.st_mtime_ns, st.st_size]
    return out


def chunks(rels: list[str], sizes: dict[str, int]):
    """Group files into CLI calls of at most BATCH files and (unless a single file is
    bigger) BATCH_BYTES bytes, so one large file can't drag a whole batch into a timeout."""
    chunk, total = [], 0
    for r in rels:
        size = sizes[r]
        if chunk and (len(chunk) >= BATCH or total + size > BATCH_BYTES):
            yield chunk
            chunk, total = [], 0
        chunk.append(r)
        total += size
    if chunk:
        yield chunk


def _first_line(e: Exception) -> str:
    return (str(e).splitlines() or [""])[0]


def run_once(cfg: dict, cli: DriveCLI, state: dict, should_stop=lambda: False, progress=lambda msg: None) -> dict:
    """Upload everything that changed. Returns {'uploaded': n, 'errors': [str]}."""
    uploaded, errors = 0, []
    made_dirs: set[str] = set()

    def record(local: Path, done: list[str], files: dict):
        nonlocal uploaded
        for r in done:
            state[f"{local}::{r}"] = files[r]
        uploaded += len(done)
        if done:
            _atomic_write(STATE, state)

    for folder in cfg["folders"]:
        local = Path(folder["local"]).expanduser()
        remote = folder["remote"].rstrip("/")
        if not local.is_dir():
            errors.append(f"Missing local folder: {local}")
            continue
        files = scan(local, cfg["exclude"], cfg["min_age_seconds"])
        changed = [r for r, sig in files.items() if state.get(f"{local}::{r}") != sig]
        by_dir: dict[str, list[str]] = {}
        for rel in sorted(changed):
            by_dir.setdefault(posixpath.dirname(rel.replace(os.sep, "/")), []).append(rel)
        for reldir, rels in by_dir.items():
            remote_dir = posixpath.join(remote, reldir) if reldir else remote
            if remote_dir not in made_dirs:
                # Also create parents (shortest first). Failure is fine: dir may exist already.
                parts = reldir.split("/") if reldir else []
                for i in range(len(parts) + 1):
                    d = posixpath.join(remote, *parts[:i])
                    if d not in made_dirs:
                        try:
                            cli.mkdir(d)
                        except DriveError:
                            pass
                        made_dirs.add(d)
            for chunk in chunks(rels, {r: files[r][1] for r in rels}):
                if should_stop():
                    return {"uploaded": uploaded, "errors": errors}
                progress(f"Uploading {len(chunk)} file(s) to {remote_dir}")
                try:
                    cli.upload([str(local / r) for r in chunk], remote_dir)
                    record(local, chunk, files)
                    continue
                except DriveError as e:
                    if len(chunk) == 1:
                        errors.append(f"{remote_dir}/{posixpath.basename(chunk[0])}: {_first_line(e)}")
                        continue
                # The batch failed: retry one by one so a single bad file doesn't hold back the rest.
                done = []
                for r in chunk:
                    if should_stop():
                        break
                    try:
                        cli.upload([str(local / r)], remote_dir)
                        done.append(r)
                    except DriveError as e:
                        errors.append(f"{remote_dir}/{posixpath.basename(r)}: {_first_line(e)}")
                record(local, done, files)
    return {"uploaded": uploaded, "errors": errors}
