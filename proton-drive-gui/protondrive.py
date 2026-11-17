"""Thin wrapper around Proton's official `proton-drive` CLI.

No GUI imports here, so it can be tested without a display.
Commands used (from Proton's docs):
    proton-drive auth login
    proton-drive filesystem list <path> --json
    proton-drive filesystem upload <local...> <remote_dir>
    proton-drive filesystem download <remote_path> <local_dir>
"""
from __future__ import annotations

import json
import os
import posixpath
import shutil
import subprocess
from dataclasses import dataclass

ROOT = "/my-files"
MIN_RATE = 256 * 1024  # bytes/s; a transfer slower than this is treated as stuck


class DriveError(Exception):
    pass


class AuthError(DriveError):
    """The CLI says we're not signed in (or the session expired)."""


# The CLI's error wording isn't documented, so this is a heuristic, kept in one place.
_AUTH_HINTS = ("not logged in", "not signed in", "log in", "login", "sign in", "unauthorized",
               "unauthenticated", "authentication", "session expired", "session has expired",
               "no session", "invalid session")


def looks_like_auth_error(msg: str) -> bool:
    m = msg.lower()
    return any(h in m for h in _AUTH_HINTS)


def transfer_timeout(nbytes: int) -> int:
    """Seconds to allow a transfer of nbytes: 10 min of slack plus time at MIN_RATE."""
    return int(600 + nbytes / MIN_RATE)


def local_size(paths: list[str]) -> int:
    total = 0
    for p in paths:
        try:
            if os.path.isdir(p):
                for root, _dirs, files in os.walk(p):
                    for name in files:
                        try:
                            total += os.lstat(os.path.join(root, name)).st_size
                        except OSError:
                            pass
            else:
                total += os.stat(p).st_size
        except OSError:
            pass
    return total


@dataclass
class Entry:
    name: str
    is_dir: bool
    size: int | None
    path: str


def find_binary() -> str | None:
    env = os.environ.get("PROTON_DRIVE_BIN")
    if env and os.access(env, os.X_OK):
        return env
    found = shutil.which("proton-drive")
    if found:
        return found
    for cand in ("~/.local/bin/proton-drive", "~/bin/proton-drive", "~/Downloads/proton-drive"):
        p = os.path.expanduser(cand)
        if os.access(p, os.X_OK):
            return p
    return None


def _first(d: dict, *keys, default=None):
    for k in keys:
        if k in d and d[k] is not None:
            return d[k]
    return default


def parse_listing(raw: str, parent: str) -> list[Entry]:
    """Tolerant parser: the exact JSON schema of the CLI isn't documented,
    so accept a bare list or an object wrapping one."""
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as e:
        raise DriveError(f"Could not parse CLI output as JSON: {e}\n{raw[:300]}")
    if isinstance(data, dict):
        for k in ("items", "entries", "children", "nodes", "files", "data", "result"):
            if isinstance(data.get(k), list):
                data = data[k]
                break
        else:
            data = []
    entries = []
    for item in data:
        if isinstance(item, str):
            item = {"name": item}
        name = _first(item, "name", "Name", "title")
        if not name:
            continue
        kind = str(_first(item, "type", "kind", "nodeType", "node_type", default="")).lower()
        is_dir = bool(_first(item, "isFolder", "is_folder", "isDirectory", "is_dir", default=False)) \
            or "folder" in kind or "dir" in kind
        size = _first(item, "size", "Size", "claimedSize", "totalStorageSize")
        path = _first(item, "path", "fullPath") or posixpath.join(parent, name)
        entries.append(Entry(name, is_dir, size if isinstance(size, int) else None, path))
    entries.sort(key=lambda e: (not e.is_dir, e.name.lower()))
    return entries


class DriveCLI:
    def __init__(self, binary: str | None = None):
        self.binary = binary or find_binary()
        if not self.binary:
            raise DriveError(
                "proton-drive CLI not found. Download it from "
                "https://proton.me/download/drive/cli, chmod +x it, and put it on your PATH "
                "(or set PROTON_DRIVE_BIN)."
            )

    def _run(self, *args: str, timeout: int | None = 600) -> str:
        try:
            p = subprocess.run([self.binary, *args], capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            raise DriveError(f"The proton-drive command timed out after {timeout} s.")
        except OSError as e:
            raise DriveError(str(e))
        if p.returncode != 0:
            msg = (p.stderr or p.stdout).strip() or f"exit code {p.returncode}"
            raise (AuthError if looks_like_auth_error(msg) else DriveError)(msg)
        return p.stdout

    def login(self) -> str:
        """Opens the browser for sign-in; blocks until the CLI finishes."""
        return self._run("auth", "login", timeout=300)

    def list(self, path: str = ROOT) -> list[Entry]:
        return parse_listing(self._run("filesystem", "list", path, "--json", timeout=60), path)

    def is_logged_in(self) -> bool:
        """False only if the CLI reports we're signed out. Other failures (offline,
        CLI crash, timeout) raise DriveError so callers can show the real cause."""
        try:
            self.list(ROOT)
            return True
        except AuthError:
            return False

    def mkdir(self, remote_path: str) -> str:
        # Command name is a best guess; callers treat failure as "may already exist".
        return self._run("filesystem", "mkdir", remote_path, timeout=60)

    def upload(self, local_paths: list[str], remote_dir: str) -> str:
        timeout = transfer_timeout(local_size(local_paths))
        return self._run("filesystem", "upload", *local_paths, remote_dir, timeout=timeout)

    def download(self, remote_path: str, local_dir: str, size: int | None = None) -> str:
        """size: expected bytes if known; without it (e.g. folders) there's no timeout."""
        timeout = transfer_timeout(size) if size is not None else None
        return self._run("filesystem", "download", remote_path, local_dir, timeout=timeout)
