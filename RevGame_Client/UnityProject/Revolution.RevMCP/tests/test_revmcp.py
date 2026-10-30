"""Behavioral regression tests for registration, real processes and MCP transport."""
import io
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT.parent / "Revolution.RevCLI")]
from revmcp.backends import Hub, call_external, external_specs, gm_line
from revmcp.schema import object_schema, validate
from revmcp.server import action_schema
from revmcp.workflow import run
from revautomation.client import RevError, RevCancelled


class FakeHub:
    timeout = .1
    def __init__(self):
        self.count = 0
        self.calls = []
    def discover(self, cancel=None):
        return dict(commands=[], actions=[dict(target="game", id=action, readOnly=readonly, inputSchema=object_schema()) for action, readonly in (("state", True), ("add", False), ("reset", False))])
    def validate_call(self, target, action, arguments, confirm, catalog):
        validate(arguments, object_schema())
    def call(self, target, action, arguments, confirm=False, cancel=None, catalog=None):
        self.calls.append(action)
        if action == "add":
            self.count += 1
        elif action == "reset":
            self.count = 0
        return dict(success=True, data={"count": self.count})


class RevMCPTests(unittest.TestCase):
    def test_schema_distinguishes_bool_and_number(self):
        with self.assertRaises(ValueError):
            validate(True, {"type": "integer"})
        with self.assertRaises(ValueError):
            validate({"typo": 2}, object_schema())

    def test_gm_parameters_preserve_unicode_and_argument_boundaries(self):
        command = dict(name="测试/文本", inputSchema=object_schema({"arg0": {"type": "string"}}, ["arg0"]))
        self.assertEqual(gm_line(command, {"arg0": "中文 空格"}), '测试/文本 "中文 空格"')
        with self.assertRaises(RevError):
            gm_line(command, {"arg0": 'has " quote'})

    def test_gm_positional_holes_are_rejected(self):
        command = dict(name="test", inputSchema=object_schema({"arg0": {"type": "integer"}, "arg1": {"type": "integer"}}))
        with self.assertRaises(RevError):
            gm_line(command, {"arg1": 4})

    def test_plan_checks_entire_contract_before_mutation(self):
        hub = FakeHub()
        with self.assertRaises(ValueError):
            run(hub, {"steps": [dict(target="game", action="add"), dict(target="game", action="state", arguments={"typo": 1})]})
        self.assertEqual(hub.calls, [])

    def test_plan_asserts_business_state_and_cleans_up(self):
        hub = FakeHub()
        report = run(hub, {"steps": [dict(target="game", action="add"), dict(target="game", action="state", expect=[dict(path="/data/count", equals=1)])], "finally": [dict(target="game", action="reset")]})
        self.assertTrue(report["success"])
        self.assertEqual(hub.count, 0)

    def test_failed_assertion_still_cleans_up(self):
        hub = FakeHub()
        report = run(hub, {"steps": [dict(target="game", action="add", expect=[dict(path="/data/count", equals=9)])], "finally": [dict(target="game", action="reset")]})
        self.assertFalse(report["success"])
        self.assertEqual(hub.count, 0)

    def test_mutation_cannot_be_retried_by_until(self):
        hub = FakeHub()
        with self.assertRaises(RevError):
            run(hub, {"steps": [dict(target="game", action="add", until=dict(path="/data/count", equals=2))]})
        self.assertEqual(hub.calls, [])

    def test_cancellation_runs_cleanup_without_cancelled_token(self):
        class CancelHub(FakeHub):
            def call(self, target, action, arguments, confirm=False, cancel=None, catalog=None):
                if action == "add":
                    raise RevCancelled("cancelled")
                if cancel is not None:
                    raise AssertionError("Cleanup received cancelled token")
                return super().call(target, action, arguments, confirm, cancel, catalog)
        hub = CancelHub()
        report = run(hub, {"steps": [dict(target="game", action="add")], "finally": [dict(target="game", action="reset")]})
        self.assertFalse(report["success"])
        self.assertTrue(report["cancelled"])
        self.assertEqual(hub.calls, ["reset"])

    def test_dynamic_editor_schema_keeps_arrays_as_arrays(self):
        schema = action_schema(dict(keys=["changes", "steps", "repeat"]), {"parameterTypes": {"changes": "array"}})
        validate({"changes": [], "steps": [], "repeat": 3}, schema)

    def test_actual_python_tool_receives_json_stdin_not_shell(self):
        with tempfile.TemporaryDirectory() as folder:
            script = Path(folder) / "echo.py"
            script.write_text("import json,sys\nprint(json.dumps(json.load(sys.stdin),ensure_ascii=False))", encoding="utf-8")
            spec = dict(name="echo", command=[sys.executable, str(script)], cwd=folder, inputSchema=object_schema({"text": {"type": "string"}}, ["text"]))
            manifest = Path(folder) / "tools.json"
            manifest.write_text(json.dumps(dict(version=1, tools=[spec])), encoding="utf-8")
            specs = external_specs(manifest)
            result = call_external(specs[0], {"text": "中文 $(secret) & | >"}, False, None)
            self.assertTrue(result["success"])
            self.assertEqual(result["data"]["text"], "中文 $(secret) & | >")

    def test_external_nonzero_exit_and_explicit_failure_are_failures(self):
        with tempfile.TemporaryDirectory() as folder:
            spec = dict(command=[sys.executable, "-c", "print('{\"success\":false}')"], cwd=folder, inputSchema=object_schema())
            self.assertFalse(call_external(spec, {}, False, None)["success"])
            spec["command"] = [sys.executable, "-c", "import sys; print('{}');sys.exit(3)"]
            self.assertEqual(call_external(spec, {}, False, None)["exitCode"], 3)

    def test_external_confirmation_happens_before_starting_process(self):
        spec = dict(command=["missing-executable"], inputSchema=object_schema(), confirm=True)
        with self.assertRaises(RevError):
            call_external(spec, {}, False, None)

    def test_mcp_stdio_real_initialize_discovery_call_and_cancellation(self):
        with tempfile.TemporaryDirectory() as folder:
            script = Path(folder) / "tool.py"
            script.write_text("import sys,json,time\na=json.load(sys.stdin)\ntime.sleep(a.get('seconds',0))\nprint(json.dumps(a))", encoding="utf-8")
            manifest = Path(folder) / "tools.json"
            manifest.write_text(json.dumps(dict(version=1, tools=[dict(name="echo", description="Echo test fixture", command=[sys.executable, str(script)], cwd=folder, inputSchema=object_schema({"seconds": {"type": "number"}}))])), encoding="utf-8")
            process = subprocess.Popen([sys.executable, str(ROOT / "rev_mcp.py"), "--external-manifest", str(manifest), "--reports", str(Path(folder) / "reports"), "serve"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            output = queue.Queue()
            reader = threading.Thread(target=lambda: [output.put(line) for line in iter(process.stdout.readline, b"")], daemon=True); reader.start()
            def send(value):
                process.stdin.write(json.dumps(value).encode() + b"\n"); process.stdin.flush()
            def receive():
                return json.loads(output.get(timeout=10))
            try:
                send(dict(jsonrpc="2.0", id=1, method="initialize", params=dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="test", version="1"))))
                self.assertEqual(receive()["result"]["serverInfo"]["name"], "Revolution.RevMCP")
                send(dict(jsonrpc="2.0", method="notifications/initialized"))
                send(dict(jsonrpc="2.0", id=2, method="tools/list"))
                tools = receive()["result"]["tools"]
                self.assertEqual(len(tools), 7)
                self.assertEqual(sum(t["name"].startswith("cli_player_") for t in tools), 3)
                tool = next(t for t in tools if t["name"].startswith("cli_echo_"))
                send(dict(jsonrpc="2.0", id=3, method="tools/call", params=dict(name=tool["name"], arguments=dict(arguments={"seconds": 0}))))
                result = receive()["result"]
                self.assertFalse(result["isError"])
                self.assertEqual(result["structuredContent"]["data"]["seconds"], 0)
                send(dict(jsonrpc="2.0", id=4, method="tools/call", params=dict(name=tool["name"], arguments=dict(arguments={"seconds": 30}))))
                send(dict(jsonrpc="2.0", method="notifications/cancelled", params=dict(requestId=4, reason="test")))
                self.assertTrue(receive()["result"]["isError"])
            finally:
                process.stdin.close(); process.wait(timeout=15)
                errors = process.stderr.read().decode(errors="replace")
                process.stdout.close(); process.stderr.close()
                self.assertEqual(process.returncode, 0, errors)


if __name__ == "__main__":
    unittest.main()
