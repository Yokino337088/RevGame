"""常驻服务：鉴权、串行、转发回退语义、快速路径与空闲退出。不依赖 Unity，执行器用假实现。"""
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation import daemon, daemon_link, fastpath
from revautomation.client import RevError
from revautomation.daemon_server import RevDaemon, default_run

REV_PY = Path(__file__).resolve().parents[1] / "rev.py"


def make_project(root: Path) -> Path:
    project = root / "proj"
    (project / "Assets").mkdir(parents=True)
    (project / "ProjectSettings").mkdir()
    return project


class DaemonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = make_project(Path(self.temp.name))
        self.calls = []
        self.release = threading.Event()
        self.release.set()

        def call_executor(project, request):
            self.calls.append(request)
            self.release.wait(5)
            if request["action"] == "boom":
                raise TimeoutError("等待超时")
            if request["action"] == "bad":
                raise RevError("参数不合法")
            return dict(success=True, echo=request["action"], arguments=request["arguments"])

        def run_executor(project, argv, cwd):
            self.calls.append(dict(argv=argv, cwd=cwd))
            return 0, "out:" + " ".join(argv), "err"

        self.daemon = RevDaemon(self.project, idle_timeout=0, call_executor=call_executor, run_executor=run_executor)
        self.thread = threading.Thread(target=self.daemon.serve_forever, daemon=True)
        self.thread.start()
        for _ in range(100):
            if daemon.read_endpoint(self.project):
                break
            time.sleep(.02)
        self.info = daemon.read_endpoint(self.project)
        self.assertIsNotNone(self.info)
        self.addCleanup(self.stop)

    def stop(self):
        self.daemon.shutdown()
        self.thread.join(5)

    def raw(self, method, path, body=None, headers=None, host=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.info["port"], timeout=5)
        try:
            merged = {"Authorization": "Bearer " + self.info["token"]}
            merged.update(headers or {})
            if host is not None:
                merged["Host"] = host
            payload = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode("utf-8"))
            connection.request(method, path, payload, merged)
            response = connection.getresponse()
            data = response.read()
            return response.status, json.loads(data.decode("utf-8") or "{}")
        finally:
            connection.close()

    def test_call_roundtrip_with_chinese_and_token_file(self):
        status, body = self.raw("POST", "/call", {"action": "gameobject.find", "arguments": {"query": "立方体"}})
        self.assertEqual((status, body["success"], body["arguments"]["query"]), (200, True, "立方体"))
        header = (daemon.daemon_dir(self.project) / daemon.HEADERS_NAME).read_text(encoding="ascii").strip()
        self.assertEqual(header, "Authorization: Bearer " + self.info["token"])

    def test_authentication_host_and_origin_are_enforced(self):
        self.assertEqual(self.raw("GET", "/health", headers={"Authorization": "Bearer wrong"})[0], 401)
        self.assertEqual(self.raw("GET", "/health", headers={"Authorization": ""})[0], 401)
        self.assertEqual(self.raw("GET", "/health", host="evil.example:%d" % self.info["port"])[0], 403)
        self.assertEqual(self.raw("GET", "/health", headers={"Origin": "http://evil.example"})[0], 403)
        self.assertEqual(self.calls, [])

    def test_request_validation_never_reaches_executor(self):
        for body in ({"arguments": {}}, {"action": ""}, {"action": "x", "arguments": []}, {"action": "x", "confirm": "yes"},
                     {"action": "x", "timeout": 0}, {"action": "x", "timeout": True}, {"action": "x", "extra": 1}):
            self.assertEqual(self.raw("POST", "/call", body)[0], 400, body)
        self.assertEqual(self.raw("POST", "/call", b"[]")[0], 400)
        self.assertEqual(self.raw("POST", "/call", b"{\"action\":\"x\",\"timeout\":NaN}")[0], 400)
        self.assertEqual(self.raw("POST", "/run", {"argv": "x", "cwd": self.temp.name})[0], 400)
        self.assertEqual(self.raw("POST", "/run", {"argv": ["a"], "cwd": str(self.project / "missing")})[0], 400)
        self.assertEqual(self.calls, [])

    def test_oversized_body_is_rejected(self):
        status, body = self.raw("POST", "/call", b"{" + b" " * (daemon.MAX_BODY + 1) + b"}")
        self.assertEqual(status, 400)

    def test_executor_failures_map_to_status_and_service_survives(self):
        self.assertEqual(self.raw("POST", "/call", {"action": "bad"})[0], 400)
        self.assertEqual(self.raw("POST", "/call", {"action": "boom"})[0], 504)
        self.assertEqual(self.raw("GET", "/health")[1]["calls"], 2)
        self.assertTrue(self.raw("POST", "/call", {"action": "ok"})[1]["success"])

    def test_calls_are_serialized_and_busy_is_reported(self):
        self.release.clear()
        results = []
        first = threading.Thread(target=lambda: results.append(self.raw("POST", "/call", {"action": "slow", "timeout": 5})))
        first.start()
        for _ in range(100):
            if self.raw("GET", "/health")[1]["busy"]:
                break
            time.sleep(.02)
        health = self.raw("GET", "/health")[1]
        self.assertTrue(health["busy"])
        self.assertEqual(health["current"], "call slow")
        status, body = self.raw("POST", "/call", {"action": "second", "timeout": .3})
        self.assertEqual((status, body["error"]), (503, "Busy"))
        self.release.set()
        first.join(5)
        self.assertEqual(results[0][0], 200)
        self.assertEqual([c["action"] for c in self.calls], ["slow"])

    def test_busy_run_is_reported_as_not_executed_before_the_client_gives_up(self):
        from revautomation import daemon_server
        self.release.clear()
        first = threading.Thread(target=lambda: self.raw("POST", "/call", {"action": "slow", "timeout": 5}))
        first.start()
        for _ in range(100):
            if self.raw("GET", "/health")[1]["busy"]:
                break
            time.sleep(.02)
        with patch.object(daemon_server, "RUN_QUEUE_SECONDS", .3):
            started = time.monotonic()
            with self.assertRaises(RevError) as raised:
                daemon.forward(self.project, ["call", "editor.status"], self.temp.name, 120)
        self.assertLess(time.monotonic() - started, 5, "必须远早于客户端读超时返回")
        self.assertIn("没有执行", str(raised.exception))
        self.assertNotIn("结果未知", str(raised.exception))
        self.release.set()
        first.join(5)
        self.assertFalse(any("argv" in call for call in self.calls), "排队超时的命令不能被执行")

    def test_run_forwards_argv_and_cwd(self):
        status, body = self.raw("POST", "/run", {"argv": ["call", "editor.status"], "cwd": self.temp.name})
        self.assertEqual(status, 200)
        self.assertEqual((body["exitCode"], body["stdout"], body["stderr"]), (0, "out:call editor.status", "err"))
        self.assertEqual(self.calls[-1]["cwd"], self.temp.name)

    def test_shutdown_removes_published_files(self):
        self.assertEqual(self.raw("POST", "/shutdown", {})[0], 200)
        self.thread.join(5)
        self.assertFalse(self.thread.is_alive())
        self.assertIsNone(daemon.read_endpoint(self.project))
        self.assertFalse((daemon.daemon_dir(self.project) / daemon.HEADERS_NAME).exists())

    def test_status_reports_running_without_exposing_token(self):
        result = daemon.status(self.project)
        self.assertTrue(result["running"])
        self.assertNotIn(self.info["token"], json.dumps(result))
        self.assertTrue(result["headerFile"].endswith(daemon.HEADERS_NAME))

    def test_start_reuses_a_running_service(self):
        result = daemon.start(self.project)
        self.assertTrue(result["alreadyRunning"])
        self.assertEqual(result["pid"], self.info["pid"])

    def _stop_real_service(self):
        """stop 在端点文件消失时返回，进程可能还占着日志；Windows 上不等它退出就删不掉临时目录。"""
        daemon.stop(self.project)
        log = daemon.daemon_dir(self.project) / daemon.LOG_NAME
        moved = log.with_name(log.name + ".closed")
        for _ in range(100):
            try:
                os.replace(log, moved)
                return
            except FileNotFoundError:
                return
            except PermissionError:
                time.sleep(.05)

    def test_start_replaces_a_service_running_old_code(self):
        self.assertFalse(daemon.status(self.project)["stale"])
        self.daemon.stamp = ("旧代码",)
        self.assertTrue(daemon.status(self.project)["stale"])
        result = daemon.start(self.project)
        self.addCleanup(self._stop_real_service)
        self.assertFalse(result["alreadyRunning"])
        self.assertNotEqual(result["pid"], self.info["pid"])
        self.assertFalse(result["stale"])
        self.thread.join(5)
        self.assertFalse(self.thread.is_alive(), "旧服务要退出")

    def test_raw_socket_client_matches_http_server(self):
        status, body = daemon_link.request(self.info, "POST", "/call", {"action": "x", "arguments": {"中文": "值"}})
        self.assertEqual((status, body["arguments"]["中文"]), (200, "值"))
        self.assertEqual(daemon_link.request(self.info, "GET", "/health")[0], 200)
        self.assertEqual(daemon_link.request(dict(self.info, token="wrong"), "GET", "/health")[0], 401)


class FastPathTests(unittest.TestCase):
    """快速路径按原始参数判断；必须与完整解析后的 should_forward 一致，否则就是两套规则。"""

    CASES = [
        ["call", "editor.status"], ["editor", "status"], ["gameobject", "find", "--query", "x"],
        ["--timeout", "30", "call", "editor.status"], ["--mode=live", "call", "editor.status"],
        ["--confirm", "build", "player", "--path", "Builds/x/Game.exe"], ["pipeline", "plan.json"],
        ["capture", "--out", "a.gif"], ["editor", "launch"], ["call", "editor.launch"],
        ["scenario", "validate", "x.json"], ["pipeline", "plan.json", "--validate"],
        ["--mode", "batch", "call", "editor.status"], ["--no-daemon", "call", "editor.status"],
        ["gif", "a.png", "--out", "b.gif"], ["doctor"], ["commands"], ["job", "abc"], ["daemon", "status"],
        ["player", "status", "--directory", "x"], ["report", "r.json", "--directory", "d"], ["evidence", "--directory", "d"],
    ]

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = make_project(Path(self.temp.name))

    def test_decision_agrees_with_full_parser(self):
        from revautomation import cli
        for argv in self.CASES:
            parsed = cli.parser().parse_args(["--project", str(self.project), *argv])
            full = daemon.should_forward(parsed)
            fast = fastpath.decide(["--project", str(self.project), *argv]) is not None
            if fast:
                self.assertTrue(full, "快速路径转发了完整规则会拒绝的命令：%s" % argv)
            # 快速路径允许更保守（少转发），但绝不能更激进。

    def test_common_commands_are_actually_fast_forwarded(self):
        for argv in (["call", "editor.status"], ["editor", "status"], ["gameobject", "find", "--query", "x"],
                     ["--mode=live", "call", "editor.status"], ["--timeout", "30", "call", "editor.status"],
                     ["--confirm", "build", "player"]):
            self.assertIsNotNone(fastpath.decide(["--project", str(self.project), *argv]), argv)

    def test_uncertain_input_falls_back_to_full_cli(self):
        for argv in (["--proj", str(self.project), "call", "editor.status"], ["--help"], ["-h"], [], ["--project"],
                     ["--timeout", "abc", "call", "x"], ["--timeout", "0", "call", "x"], ["--unknown", "call", "x"]):
            self.assertIsNone(fastpath.decide(argv), argv)

    def test_default_project_matches_cli(self):
        from revautomation import cli
        self.assertEqual(os.path.realpath(fastpath.decide(["call", "editor.status"])[0]), os.path.realpath(cli.DEFAULT_PROJECT))

    def test_no_service_returns_none_without_error(self):
        self.assertIsNone(fastpath.try_run(["--project", str(self.project), "call", "editor.status"]))

    def test_end_to_end_subprocess_does_not_import_cli(self):
        calls = []
        service = RevDaemon(self.project, idle_timeout=0, call_executor=lambda *_: {},
                            run_executor=lambda project, argv, cwd: (calls.append((argv, cwd)) or (3, "out:" + argv[-1] + "\n", "err\n")))
        thread = threading.Thread(target=service.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(lambda: (service.shutdown(), thread.join(5)))
        for _ in range(100):
            if daemon.read_endpoint(self.project):
                break
            time.sleep(.02)
        process = subprocess.run([sys.executable, "-X", "importtime", str(REV_PY), "--project", str(self.project), "call", "立方体"],
                                 capture_output=True, text=True, encoding="utf-8", cwd=self.temp.name)
        self.assertEqual(process.returncode, 3)
        self.assertEqual(process.stdout, "out:立方体\n")
        self.assertIn("err", process.stderr)
        imported = set(re.findall(r"\|\s+([\w.]+)$", process.stderr, re.MULTILINE))
        self.assertNotIn("revautomation.cli", imported)
        self.assertNotIn("argparse", imported)
        self.assertNotIn("revautomation.client", imported)
        self.assertEqual(calls[0][0][-2:], ["call", "立方体"])
        self.assertEqual(os.path.realpath(calls[0][1]), os.path.realpath(self.temp.name))


class ForwardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = make_project(Path(self.temp.name))

    def test_no_service_means_local_execution(self):
        self.assertIsNone(daemon.forward(self.project, ["call", "editor.status"], self.temp.name, 5))

    def test_dead_service_cleans_stale_files_then_runs_locally(self):
        root = daemon.daemon_dir(self.project)
        root.mkdir(parents=True)
        info = dict(protocol=1, pid=1, port=1, token="t", project=str(self.project))
        (root / daemon.ENDPOINT_NAME).write_text(json.dumps(info), encoding="utf-8")
        (root / daemon.HEADERS_NAME).write_text("x", encoding="ascii")
        self.assertIsNone(daemon.forward(self.project, ["call", "x"], self.temp.name, 5))
        self.assertFalse((root / daemon.ENDPOINT_NAME).exists())
        self.assertFalse((root / daemon.HEADERS_NAME).exists())

    def test_request_sent_then_connection_lost_never_falls_back(self):
        root = daemon.daemon_dir(self.project)
        root.mkdir(parents=True)
        info = dict(protocol=1, pid=1, port=65000, token="t", project=str(self.project))
        (root / daemon.ENDPOINT_NAME).write_text(json.dumps(info), encoding="utf-8")
        with patch("revautomation.daemon_link.request", side_effect=ConnectionResetError("reset")):
            with self.assertRaises(RevError) as raised:
                daemon.forward(self.project, ["editor", "play"], self.temp.name, 5)
        self.assertIn("不会在本地重放", str(raised.exception))
        self.assertTrue((root / daemon.ENDPOINT_NAME).exists())

    def test_other_project_endpoint_is_ignored(self):
        root = daemon.daemon_dir(self.project)
        root.mkdir(parents=True)
        info = dict(protocol=1, pid=1, port=1, token="t", project=str(Path(self.temp.name) / "elsewhere"))
        (root / daemon.ENDPOINT_NAME).write_text(json.dumps(info), encoding="utf-8")
        self.assertIsNone(daemon.read_endpoint(self.project))

    def test_endpoint_without_project_is_ignored(self):
        root = daemon.daemon_dir(self.project)
        root.mkdir(parents=True)
        (root / daemon.ENDPOINT_NAME).write_text(json.dumps(dict(protocol=1, pid=1, port=1, token="t")), encoding="utf-8")
        self.assertIsNone(daemon.read_endpoint(self.project))

    def test_should_forward_rules(self):
        from revautomation import cli

        def parse(*argv):
            return cli.parser().parse_args(["--project", str(self.project), *argv])

        self.assertTrue(daemon.should_forward(parse("call", "editor.status")))
        self.assertTrue(daemon.should_forward(parse("editor", "status")))
        for local in (("gif", "a.png", "--out", "b.gif"), ("doctor",), ("commands",), ("job", "abc"), ("daemon", "status")):
            self.assertFalse(daemon.should_forward(parse(*local)), local)
        self.assertFalse(daemon.should_forward(parse("--no-daemon", "call", "editor.status")))
        self.assertFalse(daemon.should_forward(parse("--mode", "batch", "call", "editor.status")))
        self.assertFalse(daemon.should_forward(parse("call", "editor.launch")))
        self.assertFalse(daemon.should_forward(parse("editor", "launch")))
        self.assertFalse(daemon.should_forward(parse("pipeline", "x.json", "--validate")))


class RunExecutorTests(unittest.TestCase):
    """服务端用真实的 cli.main 执行。用 `--mode live` 且没有编辑器：确定地得到一个 RevError，不需要 Unity。"""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = make_project(Path(self.temp.name))

    def run_cli(self, *argv, cwd=None):
        return default_run(self.project, ["--project", str(self.project), "--mode", "live", *argv], cwd or self.temp.name)

    def test_run_executes_the_real_cli_and_reports_its_error(self):
        code, out, err = self.run_cli("call", "editor.status")
        self.assertEqual(code, 2)
        self.assertIn("没有在线 Unity 桥接", json.loads(err)["message"])

    def test_unexpected_error_during_execution_is_reported_as_unknown_not_rejected(self):
        with patch("revautomation.cli.main", side_effect=KeyError("中途")):
            code, out, err = self.run_cli("call", "editor.status")
        self.assertEqual(code, 1)
        self.assertIn("ResultUnknown", err)
        self.assertIn("不要重放", err)

    def test_run_rejects_a_different_project(self):
        other = make_project(Path(self.temp.name) / "other")
        code, out, err = default_run(self.project, ["--project", str(other), "--mode", "live", "call", "editor.status"], self.temp.name)
        self.assertEqual(code, 2)
        self.assertIn("ProjectMismatch", err)

    def test_server_refuses_commands_that_must_stay_local(self):
        for argv in (["commands"], ["doctor"], ["--no-daemon", "call", "editor.status"], ["editor", "launch"], ["call", "editor.launch"]):
            code, out, err = self.run_cli(*argv)
            self.assertEqual(code, 2, argv)
            self.assertIn("NotForwardable", err, argv)

    def test_argparse_errors_return_code_not_crash(self):
        code, out, err = self.run_cli("no-such-family")
        self.assertEqual(code, 2)
        self.assertIn("usage", err)

    def test_cwd_is_restored_and_relative_output_uses_caller_directory(self):
        before = os.getcwd()
        caller = Path(self.temp.name) / "caller"
        caller.mkdir()
        code, _, err = default_run(self.project, ["--project", str(self.project), "--mode", "live", "--output", "result.json",
                                                  "call", "editor.status"], str(caller))
        self.assertEqual(code, 2)
        self.assertTrue((caller / "result.json").is_file())
        self.assertEqual(os.getcwd(), before)


class IdleTests(unittest.TestCase):
    def test_idle_service_exits_and_cleans_up(self):
        with tempfile.TemporaryDirectory() as temp:
            project = make_project(Path(temp))
            service = RevDaemon(project, idle_timeout=1.2, call_executor=lambda *_: {}, run_executor=lambda *_: (0, "", ""))
            thread = threading.Thread(target=service.serve_forever, daemon=True)
            thread.start()
            thread.join(6)
            self.assertFalse(thread.is_alive())
            self.assertIsNone(daemon.read_endpoint(project))


if __name__ == "__main__":
    unittest.main()
