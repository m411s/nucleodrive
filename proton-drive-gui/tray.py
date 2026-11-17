#!/usr/bin/env python3
"""System tray indicator for the Proton Drive backup daemon (pystray).

Shows status, "Sync now", Pause/Resume, "Open Proton Drive", Quit.
Needs: pystray + pillow. On GNOME you also need the AppIndicator/KStatusNotifier
extension for tray icons to show up at all.
"""
import os
import signal
import subprocess
import sys
import threading
import time

import pystray
from PIL import Image, ImageDraw

import syncengine as se

HERE = os.path.dirname(os.path.abspath(__file__))
COLORS = {"idle": "#3ba55d", "syncing": "#4a7fe0", "paused": "#9aa0a6", "error": "#d9534f", "login": "#e0a030",
          "stopped": "#5f6368"}


def make_icon(color: str) -> Image.Image:
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((4, 4, 60, 60), fill=color)
    d.rectangle((24, 18, 31, 46), fill="white")                 # "P" stem
    d.pieslice((24, 18, 46, 38), -90, 90, fill="white")          # "P" bowl
    d.pieslice((29, 23, 40, 33), -90, 90, fill=color)
    return img


def status() -> dict:
    return se.effective_status()


def ago(ts):
    if not ts:
        return "never"
    secs = int(time.time() - ts)
    if secs < 90:
        return "just now"
    if secs < 5400:
        return f"{secs // 60} min ago"
    return f"{secs // 3600} h ago"


def daemon_cmd(cmd):
    subprocess.Popen([sys.executable, os.path.join(HERE, "syncd.py"), cmd])


def main():
    def status_text(_item):
        s = status()
        return f"{s.get('message', 'Daemon not running')}"

    def last_text(_item):
        return f"Last sync: {ago(status().get('last_sync'))}"

    def toggle_pause(_icon, _item):
        daemon_cmd("resume" if se.is_paused() else "pause")

    menu = pystray.Menu(
        pystray.MenuItem(status_text, None, enabled=False),
        pystray.MenuItem(last_text, None, enabled=False),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Sync now", lambda *_: daemon_cmd("now"),
                         enabled=lambda _i: se.daemon_pid() is not None and not se.is_paused()),
        pystray.MenuItem(lambda _i: "Resume" if se.is_paused() else "Pause", toggle_pause),
        pystray.MenuItem("Open Proton Drive", lambda *_: subprocess.Popen([sys.executable, os.path.join(HERE, "app.py")])),
        pystray.Menu.SEPARATOR,
        pystray.MenuItem("Quit tray", lambda icon, _i: icon.stop()),
    )
    icon = pystray.Icon("proton-drive-gui", make_icon(COLORS["idle"]), "Proton Drive", menu)

    def refresh_loop():
        last = None
        while icon.visible or last is None:
            s = status()
            state = s.get("state", "error")
            if state != last:
                icon.icon = make_icon(COLORS.get(state, COLORS["error"]))
                last = state
            icon.title = f"Proton Drive – {s.get('message', 'not running')}"[:120]
            try:
                icon.update_menu()
            except Exception:
                pass
            time.sleep(3)

    def setup(ic):
        ic.visible = True
        threading.Thread(target=refresh_loop, daemon=True).start()

    signal.signal(signal.SIGTERM, lambda *_: icon.stop())
    icon.run(setup)


if __name__ == "__main__":
    main()
