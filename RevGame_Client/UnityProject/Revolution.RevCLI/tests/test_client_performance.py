"""Heartbeat read races and domain-aware ready barrier regression."""
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.client import RevClient, RevCancelled, _RELEASED_JOB, atomic_json


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
        """发布前取消时 Unity 不会读取请求，可以直接清理准备文件。"""
        token = threading.Event()
        endpoint = dict(sessionId="same", batchMode=False)
        def write(path, value):
            """模拟落盘以后、发布以前收到取消。"""
            atomic_json(path, value)
            if path.name == "request.json":
                token.set()
        with patch.object(self.client, "ready_endpoint", return_value=endpoint):
            with patch("revautomation.client.atomic_json", side_effect=write):
                with self.assertRaises(RevCancelled):
                    self.client.call("editor.play", cancel=token)
        self.assertFalse((self.client.root / "inbox").exists())
        self.assertEqual(list((self.client.root / "jobs").iterdir()), [])

    def test_terminal_file_bridge_cleans_consumed_files_but_keeps_requested_output(self):
        """旧桥的响应一经消费就删除控制文件，但截图等实际产物仍可读取。"""
        endpoint = dict(sessionId="same")
        outputs = []
        def write(path, value):
            """模拟 Unity 从 inbox 领到请求后返回一个可读输出。"""
            atomic_json(path, value)
            if path.parent.name == "inbox":
                path.unlink()
                work = self.client.root / "jobs" / value["id"]
                screenshot = work / "scene-capture.png"
                screenshot.write_bytes(b"image")
                outputs.append(screenshot)
                atomic_json(work / "progress.json", {"completed": 1})
                atomic_json(work / "response.json", dict(protocol=1, id=value["id"], success=True))
        with patch.object(self.client, "ready_endpoint", return_value=endpoint), patch("revautomation.client.atomic_json", side_effect=write):
            response = self.client.call("scene.capture")
        self.assertTrue(response["success"])
        self.assertEqual(list(Path(response["jobDirectory"]).iterdir()), outputs)

    def test_scenario_only_exports_when_requested_and_removes_scratch(self):
        """两种用法都读到实际轮数，明示的 HTML 在原始逐轮文件删除前生成。"""
        endpoint = dict(sessionId="same", memoryRequired=True)
        destination = self.client.project / "Docs/性能"
        def run(client, request, *_args, **_kwargs):
            """预备旧版 Unity scenario 仍会写入的三类中间文件。"""
            work = client.root / "jobs" / request["id"]
            result = work / "scenario-result.json"
            log = work / "unity-errors.jsonl"
            data = dict(success=True, completed=1, resultPath=str(result), unityLogPath=str(log))
            atomic_json(result, data)
            log.touch()
            (work / "iterations.jsonl").write_text('{"iteration":1}\n', encoding="utf-8")
            return dict(success=True, id=request["id"], data=data, jobDirectory=str(work))
        with patch.object(self.client, "ready_endpoint", return_value=endpoint), patch("revautomation.memory_transport.call", side_effect=run):
            default = self.client.call("scenario.run")
            self.assertEqual(default["data"]["completed"], 1)
            self.assertNotIn("reports", default)
            self.assertFalse(destination.exists())
            explicit = self.client.call("scenario.run", report_dir=destination)
        self.assertEqual(list((self.client.root / "jobs").iterdir()), [])
        self.assertNotIn("resultPath", explicit["data"])
        self.assertTrue(Path(explicit["reports"]["html"]).is_file())
        self.assertEqual(Path(explicit["reports"]["csv"]).parent, destination)

    def test_file_bridge_job_query_leaves_result_for_the_active_owner(self):
        """旁路查询不能抢先删除同步调用的回执，调用方放弃等待后才接手清理。"""
        ident = "a" * 32
        work = self.client.root / "jobs" / ident
        response = work / "response.json"
        atomic_json(work / "request.json", {"id": ident})
        atomic_json(response, dict(protocol=1, id=ident, success=True))
        (work / "cancel").touch()
        self.assertTrue(self.client.job(ident)["response"]["success"])
        self.assertTrue(response.is_file())
        (work / "cancel").write_text(_RELEASED_JOB, encoding="ascii")
        self.assertTrue(self.client.job(ident)["response"]["success"])
        self.assertFalse(work.exists())

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
