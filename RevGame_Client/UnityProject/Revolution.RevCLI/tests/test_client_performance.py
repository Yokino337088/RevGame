"""Heartbeat read races and domain-aware ready barrier regression."""
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.client import RevClient, RevCancelled, atomic_json


class ClientPerformanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        project = Path(self.temp.name)
        (project / "Assets").mkdir()
        (project / "ProjectSettings").mkdir()
        self.client = RevClient(project, mode="live", timeout=10)

    def test_pre_cancelled_call_never_creates_job_or_inbox(self):
        token = threading.Event()
        token.set()
        with patch.object(self.client, "ready_endpoint") as ready:
            with self.assertRaises(RevCancelled):
                self.client.call("editor.play", cancel=token)
            ready.assert_not_called()
        self.assertFalse(self.client.root.exists())

    def test_cancel_during_job_preparation_does_not_publish_inbox(self):
        token = threading.Event()
        endpoint = dict(sessionId="same", batchMode=False)
        def write(path, value):
            atomic_json(path, value)
            if path.name == "request.json":
                token.set()
        with patch.object(self.client, "ready_endpoint", return_value=endpoint):
            with patch("revautomation.client.atomic_json", side_effect=write):
                with self.assertRaises(RevCancelled):
                    self.client.call("editor.play", cancel=token)
        self.assertFalse((self.client.root / "inbox").exists())
        self.assertEqual(len(list((self.client.root / "jobs").glob("*/cancel"))), 1)

    def test_transient_heartbeat_replace_retries_read_without_offline_delay(self):
        endpoint = dict(protocol=1, project=str(self.client.project), heartbeatUnix=time.time(), sessionId="same")
        with patch("revautomation.client.read_json", side_effect=[PermissionError("replacing"), endpoint]) as read:
            with patch("revautomation.client.time.sleep") as sleep:
                self.assertEqual(self.client.endpoint(), endpoint)
                self.assertEqual(read.call_count, 2)
                sleep.assert_called_once_with(.002)

    def test_non_object_endpoint_is_rejected_without_attribute_error(self):
        with patch("revautomation.client.read_json", return_value=[]):
            self.assertIsNone(self.client.endpoint())

    def test_ready_barrier_restarts_for_new_domain_with_same_session(self):
        clock, reads, first_new = [0.0], [0], []
        def endpoint():
            reads[0] += 1
            domain = "old" if reads[0] <= 4 else "new"
            if domain == "new" and not first_new:
                first_new.append(clock[0])
            return dict(sessionId="same", domainId=domain, heartbeatUnix=100 + clock[0])
        def sleep(seconds):
            clock[0] += seconds
        with patch.object(self.client, "endpoint", side_effect=endpoint):
            with patch("revautomation.client.time.monotonic", side_effect=lambda: clock[0]):
                with patch("revautomation.client.time.sleep", side_effect=sleep):
                    result = self.client.wait_ready()
        self.assertEqual(result["endpoint"]["domainId"], "new")
        self.assertGreaterEqual(clock[0] - first_new[0], 2)


if __name__ == "__main__":
    unittest.main()
