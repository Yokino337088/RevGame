"""内存通道回归：真实 HTTP 服务、无工单落盘、单次 POST、Ack、取消与重载断线只重读。"""
import datetime as dt
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import sys
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.client import RevClient, RevError, RevCancelled, atomic_json
from revautomation import memory_transport
from revautomation.storage import DEFAULT, prune_reports


class Handler(BaseHTTPRequestHandler):
    posts = 0
    acknowledgements = 0
    mode = "normal"
    gets = 0
    deletes = 0
    def log_message(self, *_): pass
    def send(self, result):
        raw = json.dumps(result).encode()
        self.send_response(200); self.send_header("Content-Length", str(len(raw))); self.end_headers(); self.wfile.write(raw)
    def do_POST(self):
        if self.path.endswith("/ack"):
            Handler.acknowledgements += 1; self.send({"success": True}); return
        Handler.posts += 1
        self.body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        Handler.ident = self.body["id"]
        self.send({"success": True, "id": Handler.ident})
    def do_GET(self):
        if self.path == "/health":
            self.send({"success": True, "transport": "http-memory"}); return
        Handler.gets += 1
        if Handler.mode == "drop" and Handler.gets == 1:
            self.connection.close(); return
        if Handler.mode == "pending":
            self.send({"pending": True}); return
        self.send({"protocol": 1, "id": Handler.ident, "success": True, "dataJson": '{"count":3}'})
    def do_DELETE(self):
        Handler.deletes += 1; self.send({"success": True})


class MemoryTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.project = Path(self.temp.name)
        (self.project / "Assets").mkdir(); (self.project / "ProjectSettings").mkdir()
        self.client = RevClient(self.project, "live", timeout=3)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True); self.thread.start()
        self.info = dict(protocol=1, url=f"http://127.0.0.1:{self.server.server_port}", token="private", sessionId="session", project=str(self.project))
        atomic_json(self.client.root / "editor-http.json", self.info)
        Handler.posts = Handler.acknowledgements = Handler.gets = Handler.deletes = 0; Handler.mode = "normal"
    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join(); self.temp.cleanup()
    def payload(self):
        return dict(id="a" * 32, action="editor.status", protocol=1, payloadJson="{}", confirm=False, sessionId="session",
                    deadlineUtc=(dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=10)).isoformat(), timeoutSeconds=10)
    def test_auto_memory_call_does_not_create_job_directory(self):
        with patch.object(self.client, "ready_endpoint", return_value=dict(memoryUrl=self.info["url"], sessionId="session")):
            result = self.client.call("editor.status")
        self.assertEqual(result["data"]["count"], 3)
        self.assertEqual(result["transport"], "http-memory")
        self.assertFalse((self.client.root / "jobs").exists())
        self.assertEqual((Handler.posts, Handler.acknowledgements), (1, 1))
    def test_get_connection_loss_never_reposts_mutation(self):
        Handler.mode = "drop"
        result = memory_transport.call(self.client, self.payload())
        self.assertTrue(result["success"])
        self.assertEqual(Handler.posts, 1)
        self.assertGreaterEqual(Handler.gets, 2)
    def test_reload_heartbeat_without_url_waits_for_http_instead_of_writing_jobs(self):
        endpoint = dict(memoryRequired=True, sessionId="session")
        with patch.object(self.client, "ready_endpoint", return_value=endpoint), patch.object(
            memory_transport, "credentials", side_effect=[FileNotFoundError("reload"), self.info, self.info]
        ):
            result = self.client.call("editor.status")
        self.assertEqual(result["transport"], "http-memory")
        self.assertEqual(Handler.posts, 1)
        self.assertFalse((self.client.root / "jobs").exists())
    def test_unavailable_required_http_times_out_without_file_fallback(self):
        self.client.timeout = .3
        with patch.object(self.client, "ready_endpoint", return_value=dict(memoryRequired=True, sessionId="session")), patch.object(
            memory_transport, "credentials", side_effect=FileNotFoundError("reload")
        ):
            with self.assertRaises(TimeoutError):
                self.client.call("editor.status")
        self.assertEqual(Handler.posts, 0)
        self.assertFalse((self.client.root / "jobs").exists())
    def test_timeout_cancels_same_identity_without_creating_files(self):
        self.client.timeout = .3
        Handler.mode = "pending"
        with self.assertRaises(TimeoutError): memory_transport.call(self.client, self.payload())
        self.assertEqual(Handler.posts, 1)
        self.assertEqual(Handler.deletes, 1)
        self.assertFalse((self.client.root / "jobs").exists())
    def test_post_unknown_result_does_not_fall_back_to_file_transport(self):
        with patch.object(memory_transport, "request", side_effect=[{"success": True}, ConnectionError("lost")]):
            with self.assertRaises(RevError): memory_transport.call(self.client, self.payload())
        self.assertFalse((self.client.root / "jobs").exists())
    def test_reports_keep_active_manual_and_marked_files(self):
        reports = self.project / "reports"; reports.mkdir()
        old = reports / "20260101-000000-aabbccdd.json"; old.write_text("{}")
        keep = reports / "20260101-000000-bbaaccdd.json"; keep.write_text("{}"); keep.with_name(keep.name + ".keep").touch()
        active = reports / "20260101-000000-ccaabbdd"; active.mkdir(); (active / "image.png").write_text("fixture")
        import os, time
        for path in (old, keep, active): os.utime(path, (time.time() - 40 * 86400,) * 2)
        preview = prune_reports(reports, DEFAULT, True)
        self.assertEqual(preview["candidates"], 1)
        self.assertTrue(old.exists())
        result = prune_reports(reports, DEFAULT, False)
        self.assertEqual(result["removed"], 1)
        self.assertTrue(keep.exists() and active.exists())
