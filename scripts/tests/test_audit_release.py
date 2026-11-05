import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("audit_release", Path(__file__).resolve().parents[1] / "audit_release.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)
REPO = Path(__file__).resolve().parents[2]


class ReleaseBoundaryTests(unittest.TestCase):
    def test_conversation_database_is_rejected(self):
        with self.assertRaises(audit.AuditError):
            audit.verify_inventory({"BitChat.exe": "binary", "state.dpapi": "private"}, {"BitChat.exe"}, {"BitChat.exe"})

    def test_missing_binary_is_rejected(self):
        with self.assertRaises(audit.AuditError):
            audit.verify_inventory({}, {"BitChat.exe"}, {"BitChat.exe"})

    def test_symlink_to_user_profile_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "profile-link").symlink_to(root / "outside")
            with self.assertRaises(audit.AuditError):
                audit.files(root)

    def test_dependency_substitution_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("BitChat.exe", "native/win-x64/bitchat_zlib.dll", "media/ffmpeg.exe", "tor/tor.exe"):
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"substituted dependency")
            with self.assertRaisesRegex(audit.AuditError, "dependency was modified"):
                audit.verify_windows(root, "win-x64", REPO)

    def test_tracked_private_profile_is_rejected(self):
        self.check_tracked_rejection("state.dpapi", b"synthetic encrypted profile", "user data")

    def test_credential_is_rejected_without_printing_it(self):
        token = b"ghp_" + b"x" * 36
        self.check_tracked_rejection("config.txt", token, "content withheld")

    def check_tracked_rejection(self, name, contents, expected):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            (root / name).write_bytes(contents)
            subprocess.run(["git", "-C", str(root), "add", name], check=True)
            with self.assertRaisesRegex(audit.AuditError, expected) as result:
                audit.verify_source(root)
            self.assertNotIn(contents.decode(), str(result.exception))


if __name__ == "__main__":
    unittest.main()
