import json, os, tempfile, time, unittest
from pathlib import Path

tmp = tempfile.mkdtemp()
os.environ["PDG_CONFIG_DIR"] = tmp + "/conf"
os.environ["PDG_STATE_DIR"] = tmp + "/state"
os.environ["FAKE_LOG"] = tmp + "/log"
import syncengine as se
from protondrive import DriveCLI

HERE = os.path.dirname(os.path.abspath(__file__))
cli = DriveCLI(os.path.join(HERE, "fake-proton-drive"))

def log():
    p = Path(os.environ["FAKE_LOG"])
    return [json.loads(l) for l in p.read_text().splitlines()] if p.exists() else []

def old(path):
    t = time.time() - 3600
    os.utime(path, (t, t))

class T(unittest.TestCase):
    def setUp(self):
        Path(os.environ["FAKE_LOG"]).unlink(missing_ok=True)
        os.environ.pop("FAKE_FAIL", None)
        self.src = Path(tempfile.mkdtemp())
        (self.src / "sub").mkdir()
        (self.src / ".hidden").write_text("x")
        for n in ("a.txt", "sub/b.txt", "draft.tmp"):
            (self.src / n).write_text(n); old(self.src / n)
        old(self.src / ".hidden")
        self.cfg = {**se.DEFAULT_CONFIG, "folders": [{"local": str(self.src), "remote": "/my-files/Backup"}]}
        self.state = {}

    def uploads(self):
        return [e for e in log() if e[0] == "upload"]

    def test_first_run_uploads_and_excludes(self):
        r = se.run_once(self.cfg, cli, self.state)
        self.assertEqual((r["uploaded"], r["errors"]), (2, []))
        names = sorted(os.path.basename(f) for u in self.uploads() for f in u[1:-1])
        self.assertEqual(names, ["a.txt", "b.txt"])
        dests = {u[-1] for u in self.uploads()}
        self.assertEqual(dests, {"/my-files/Backup", "/my-files/Backup/sub"})
        self.assertIn(["mkdir", "/my-files/Backup/sub"], log())

    def test_second_run_is_noop_then_detects_change(self):
        se.run_once(self.cfg, cli, self.state)
        Path(os.environ["FAKE_LOG"]).unlink()
        self.assertEqual(se.run_once(self.cfg, cli, self.state)["uploaded"], 0)
        (self.src / "a.txt").write_text("changed!"); old(self.src / "a.txt")
        self.assertEqual(se.run_once(self.cfg, cli, self.state)["uploaded"], 1)

    def test_failure_not_recorded_and_retried(self):
        os.environ["FAKE_FAIL"] = "b.txt"
        r = se.run_once(self.cfg, cli, self.state)
        self.assertEqual(r["uploaded"], 1)
        self.assertEqual(len(r["errors"]), 1)
        os.environ.pop("FAKE_FAIL")
        self.assertEqual(se.run_once(self.cfg, cli, self.state)["uploaded"], 1)

    def test_recent_files_skipped(self):
        (self.src / "new.txt").write_text("fresh")
        self.assertEqual(se.run_once(self.cfg, cli, self.state)["uploaded"], 2)

    def test_stop_flag(self):
        r = se.run_once(self.cfg, cli, self.state, should_stop=lambda: True)
        self.assertEqual(r["uploaded"], 0)

    def test_one_bad_file_does_not_block_its_batch(self):
        (self.src / "c.txt").write_text("c"); old(self.src / "c.txt")
        os.environ["FAKE_FAIL"] = "a.txt"  # a.txt and c.txt share a batch
        r = se.run_once(self.cfg, cli, self.state)
        self.assertEqual(r["uploaded"], 2)  # c.txt + sub/b.txt
        self.assertEqual(len(r["errors"]), 1)
        self.assertIn("a.txt", r["errors"][0])
        self.assertIn(f"{self.src}::c.txt", self.state)
        self.assertNotIn(f"{self.src}::a.txt", self.state)

    def test_chunks_split_by_count_and_bytes(self):
        big = se.BATCH_BYTES
        self.assertEqual(list(se.chunks(["a", "B", "c"], {"a": 1, "B": big, "c": 1})), [["a"], ["B"], ["c"]])
        many = [str(i) for i in range(se.BATCH + 1)]
        self.assertEqual([len(c) for c in se.chunks(many, dict.fromkeys(many, 1))], [se.BATCH, 1])


class Config(unittest.TestCase):
    def tearDown(self):
        se.CONFIG.unlink(missing_ok=True)

    def write(self, text):
        se.CONFIG.parent.mkdir(parents=True, exist_ok=True)
        se.CONFIG.write_text(text)

    def test_defaults_when_missing_and_not_shared(self):
        cfg = se.load_config()
        cfg["folders"].append({"local": "/x", "remote": "/my-files/x"})
        self.assertEqual(se.load_config()["folders"], [])

    def test_bad_json_and_bad_types_raise_config_error(self):
        for text in ("{bad", "[]", '{"interval_minutes": "15"}', '{"interval_minutes": 0}',
                     '{"folders": [{"local": "/x"}]}', '{"exclude": "*.tmp"}'):
            self.write(text)
            with self.assertRaises(se.ConfigError, msg=text):
                se.load_config()

unittest.main()
