"""Daemon process tests: runs syncd.py as a real subprocess against temp config/state dirs."""
import json, os, signal, subprocess, sys, tempfile, time, unittest
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
SYNCD = os.path.join(HERE, "syncd.py")


class T(unittest.TestCase):
    def setUp(self):
        tmp = Path(tempfile.mkdtemp())
        self.conf, self.state = tmp / "conf", tmp / "state"
        self.conf.mkdir(); self.state.mkdir()
        self.env = {**os.environ, "PDG_CONFIG_DIR": str(self.conf), "PDG_STATE_DIR": str(self.state),
                    "PROTON_DRIVE_BIN": os.path.join(HERE, "fake-proton-drive")}
        self.procs = []

    def tearDown(self):
        for p in self.procs:
            if p.poll() is None:
                p.kill()
            p.communicate()  # reaps and closes pipes

    def spawn(self, *args):
        p = subprocess.Popen([sys.executable, SYNCD, *args], env=self.env,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.procs.append(p)
        return p

    def ctl(self, *args):
        return subprocess.run([sys.executable, SYNCD, *args], env=self.env, capture_output=True, text=True, timeout=10)

    def status(self):
        try:
            return json.loads((self.state / "status.json").read_text())
        except (OSError, ValueError):
            return {}

    def wait_for(self, pred, timeout=5):
        end = time.time() + timeout
        while time.time() < end:
            if pred():
                return True
            time.sleep(0.05)
        return False

    def test_corrupt_config_reports_error_and_keeps_running(self):
        (self.conf / "config.json").write_text("{bad")
        d = self.spawn("run")
        self.assertTrue(self.wait_for(lambda: "Can't read" in self.status().get("message", "")), self.status())
        self.assertEqual(self.status()["state"], "error")
        self.assertIsNone(d.poll())  # still alive
        d.send_signal(signal.SIGTERM)
        self.assertEqual(d.wait(5), 0)
        self.assertEqual(self.status()["message"], "Stopped")

    def test_add_refuses_to_overwrite_corrupt_config(self):
        (self.conf / "config.json").write_text("{bad")
        r = self.ctl("add", HERE, "/my-files/x")
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual((self.conf / "config.json").read_text(), "{bad")

    def test_single_instance_and_now_reaches_running_daemon(self):
        d = self.spawn("run")
        self.assertTrue(self.wait_for(lambda: "No folders" in self.status().get("message", "")))
        second = self.spawn("run")
        _out, err = second.communicate(timeout=10)
        self.assertNotEqual(second.returncode, 0)
        self.assertIn("already running", err)
        self.assertEqual(self.ctl("now").returncode, 0)
        self.assertIsNone(d.poll())  # SIGUSR1 handled, not fatal

    def tray_status(self):
        code = "import json, syncengine as se; print(json.dumps(se.effective_status()))"
        r = subprocess.run([sys.executable, "-c", code], env=self.env, cwd=HERE,
                           capture_output=True, text=True, timeout=10)
        return json.loads(r.stdout)

    def test_tray_status_tracks_daemon_liveness(self):
        # Leftover "syncing" status from a daemon that died: must not be shown as live.
        (self.state / "status.json").write_text(json.dumps({"state": "syncing", "message": "Syncing…"}))
        self.assertEqual(self.tray_status()["state"], "stopped")
        d = self.spawn("run")
        self.assertTrue(self.wait_for(lambda: "No folders" in self.status().get("message", "")))
        self.assertEqual(self.tray_status()["state"], "idle")
        self.assertEqual(self.ctl("pause").returncode, 0)
        self.assertEqual(self.tray_status()["state"], "paused")
        d.kill(); d.wait()  # crash: no clean "Stopped" write
        self.assertEqual(self.tray_status()["state"], "stopped")

    def test_stale_pid_is_never_signalled(self):
        # Simulate a crashed daemon whose PID now belongs to an unrelated process.
        victim = subprocess.Popen(["sleep", "30"])
        self.procs.append(victim)
        (self.state / "status.json").write_text(json.dumps({"state": "syncing", "pid": victim.pid}))
        (self.state / "daemon.lock").write_text(str(victim.pid))
        for cmd in ("now", "pause", "resume"):
            self.ctl(cmd)
        time.sleep(0.2)
        self.assertIsNone(victim.poll(), "unrelated process was signalled")


unittest.main()
