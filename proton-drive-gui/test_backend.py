import os, tempfile, unittest
from protondrive import AuthError, DriveCLI, DriveError, parse_listing, transfer_timeout

HERE = os.path.dirname(os.path.abspath(__file__))
cli = DriveCLI(os.path.join(HERE, "fake-proton-drive"))

class T(unittest.TestCase):
    def test_list_sorted_folders_first(self):
        es = cli.list("/my-files")
        self.assertEqual([e.name for e in es], ["Documents", "b.txt"])
        self.assertTrue(es[0].is_dir)
        self.assertEqual(es[1].size, 2048)
        self.assertEqual(es[0].path, "/my-files/Documents")
    def test_wrapped_object(self):
        self.assertEqual(cli.list("/my-files/Documents"), [])
    def test_error_surfaces(self):
        with self.assertRaises(DriveError) as c: cli.list("/nope")
        self.assertIn("not logged in", str(c.exception))
    def test_upload_download_login(self):
        self.assertIn("uploaded", cli.upload(["/tmp/x"], "/my-files"))
        self.assertIn("downloaded", cli.download("/my-files/b.txt", "/tmp"))
        self.assertEqual(cli.login().strip(), "ok")
    def test_bad_json(self):
        with self.assertRaises(DriveError): parse_listing("not json", "/")
    def test_auth_error_vs_other_failures(self):
        with self.assertRaises(AuthError): cli.list("/nope")
        with self.assertRaises(DriveError) as c: cli.list("/offline")
        self.assertNotIsInstance(c.exception, AuthError)
    def test_is_logged_in_only_false_when_signed_out(self):
        self.assertTrue(cli.is_logged_in())
        offline = DriveCLI(cli.binary)
        offline.list = lambda path: cli.list("/offline")
        with self.assertRaises(DriveError): offline.is_logged_in()  # not reported as "sign in"
        signed_out = DriveCLI(cli.binary)
        signed_out.list = lambda path: cli.list("/nope")
        self.assertFalse(signed_out.is_logged_in())
    def test_transfer_timeout_scales_with_size(self):
        self.assertEqual(transfer_timeout(0), 600)
        self.assertGreater(transfer_timeout(10 * 1024**3), 40000)  # 10 GB: ~11 h at 256 KB/s
        seen = {}
        c = DriveCLI(cli.binary)
        c._run = lambda *a, timeout=None: seen.setdefault("t", timeout)
        with tempfile.NamedTemporaryFile() as f:
            f.truncate(100 * 1024**2)
            c.upload([f.name], "/my-files")
        self.assertEqual(seen["t"], transfer_timeout(100 * 1024**2))
        seen.clear(); c.download("/my-files/folder", "/tmp")
        self.assertIsNone(seen["t"])  # unknown size: no timeout

unittest.main()
