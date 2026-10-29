"""活动日志的行为测试：真实文件/进程、并发、断言失败、取消和脱敏。"""
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT.parent / "Revolution.RevCLI")]
from revmcp.activity import Activity
from revmcp.backends import Hub
from revmcp.workflow import run
from revmcp.schema import object_schema
from revmcp.server import Server
from revautomation.client import RevCancelled, RevError
from test_revmcp import FakeHub


def rows(folder):
    return [json.loads(line) for file in Path(folder).glob("*.jsonl") for line in file.read_text(encoding="utf-8").splitlines()]


class ActivityTests(unittest.TestCase):
    def test_start_is_visible_before_completion_and_updates_same_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            activity = Activity(folder)
            ticket = activity.start("gm", "测试/加金币", {"arg0": 3})
            self.assertEqual(rows(folder)[0]["state"], "Running")
            activity.finish(ticket, dict(success=True, data={"count": 3}))
            records = rows(folder)
            self.assertEqual(records[0]["id"], records[1]["id"])
            self.assertEqual(records[1]["state"], "Succeeded")
            self.assertEqual(records[1]["action"], "测试/加金币")

    def test_nested_json_credentials_and_image_payload_are_hidden(self):
        with tempfile.TemporaryDirectory() as folder:
            activity = Activity(folder)
            ticket = activity.start("game", "login", {"argumentsJson": json.dumps({"token": "secret-value", "password": "pw-value", "count": 2})})
            activity.finish(ticket, dict(success=True, data={"base64": "large-image-value", "authorization": "credential-value"}))
            raw = "\n".join(p.read_text(encoding="utf-8") for p in Path(folder).glob("*.jsonl"))
            for secret in ("secret-value", "pw-value", "large-image-value", "credential-value"):
                self.assertNotIn(secret, raw)
            self.assertIn("count", raw)

    def test_writing_failure_does_not_change_business_result(self):
        with tempfile.TemporaryDirectory() as folder:
            blocker = Path(folder) / "not-a-folder"
            blocker.write_text("fixture")
            activity = Activity(blocker)
            ticket = activity.start("cli", "echo")
            activity.finish(ticket, dict(success=True))
            self.assertTrue(activity.last_error)

    def test_concurrent_agents_use_independent_complete_json_lines(self):
        with tempfile.TemporaryDirectory() as folder:
            agents = [Activity(folder) for _ in range(4)]
            def work(activity):
                for _ in range(20):
                    activity.finish(activity.start("cli", "echo"), dict(success=True))
            threads = [threading.Thread(target=work, args=(agent,)) for agent in agents]
            for thread in threads: thread.start()
            for thread in threads: thread.join()
            self.assertEqual(len(rows(folder)), 160)
            self.assertEqual(len(list(Path(folder).glob("*.jsonl"))), 4)

    def test_plan_records_assertion_failure_and_finally_with_context(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = FakeHub()
            hub.activity = Activity(folder)
            report = run(hub, {"name": "失败后清理", "steps": [dict(target="game", action="add", expect=[dict(path="/data/count", equals=99)])],
                               "finally": [dict(target="game", action="reset")]})
            self.assertFalse(report["success"])
            self.assertEqual(hub.count, 0)
            completed = [r for r in rows(folder) if r["state"] != "Running"]
            self.assertEqual(completed[0]["error"], "AssertionFailed")
            self.assertEqual(completed[0]["plan"], "失败后清理")
            self.assertEqual(completed[-1]["phase"], "finally")
            self.assertEqual(completed[-1]["state"], "Succeeded")

    def test_hub_records_rejection_and_cancellation_without_starting_business(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = Hub(cli_root=ROOT.parent / "Revolution.RevCLI", reports=Path(folder) / "reports")
            with self.assertRaises(RevError):
                hub.call("gm", "不存在", {}, catalog=dict(commands=[], actions=[]))
            with patch.object(hub, "_call", side_effect=RevCancelled("cancelled")):
                with self.assertRaises(RevCancelled): hub.call("game", "wait")
            completed = [r for r in rows(Path(folder) / "activity") if r["state"] != "Running"]
            self.assertEqual([r["state"] for r in completed], ["Failed", "Cancelled"])

    def test_real_external_python_call_records_exact_arguments_and_result(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = Hub(cli_root=ROOT.parent / "Revolution.RevCLI", reports=Path(folder) / "reports")
            spec = dict(target="cli", id="echo", command=[sys.executable, "-c", "import json,sys; print(json.dumps(json.load(sys.stdin)))"], cwd=folder,
                        inputSchema=object_schema({"text": {"type": "string"}}))
            result = hub.call("cli", "echo", {"text": "中文调用"}, catalog=dict(commands=[], actions=[spec]))
            self.assertEqual(result["data"]["text"], "中文调用")
            final = rows(Path(folder) / "activity")[-1]
            self.assertEqual(final["state"], "Succeeded")
            self.assertIn("中文调用", final["argumentsJson"])

    def test_mcp_intent_is_logged_separately_from_external_business_arguments(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = Hub(cli_root=ROOT.parent / "Revolution.RevCLI", reports=Path(folder) / "reports")
            spec = dict(target="cli", id="echo", description="回传调用者文本", command=[sys.executable, "-c", "import json,sys; print(json.dumps(json.load(sys.stdin)))"], cwd=folder,
                        inputSchema=object_schema({"text": {"type": "string"}}))
            server = Server(hub)
            with patch.object(hub, "discover", return_value=dict(commands=[], actions=[spec])):
                result = server.call({"name": "rev_call", "arguments": {"target": "cli", "action": "echo", "arguments": {"text": "中文"}, "intent": "确认中文输入不会乱码"}}, None)
            self.assertFalse(result["isError"])
            self.assertEqual(result["structuredContent"]["data"], {"text": "中文"})
            command = next(r for r in rows(Path(folder) / "activity") if r["target"] == "cli" and r["state"] == "Succeeded")
            self.assertEqual(command["intent"], "确认中文输入不会乱码")
            self.assertEqual(command["description"], "回传调用者文本")

    def test_step_intent_is_validated_before_any_mutation(self):
        hub = FakeHub()
        with self.assertRaises(RevError):
            run(hub, {"steps": [dict(target="game", action="add"), dict(target="game", action="state", intent=4)]})
        self.assertEqual(hub.calls, [])


if __name__ == "__main__":
    unittest.main()
