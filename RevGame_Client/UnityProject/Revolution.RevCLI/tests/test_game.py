"""game 命令族：用一个按 Unity 协议行事的假服务验证鉴权、轮询、不重放与参数拼接。"""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation import cli, daemon, fastpath, game
from revautomation.errors import RevError

TOKEN, SESSION = "t" * 64, "s" * 32


class FakeRuntime(BaseHTTPRequestHandler):
    jobs, posts, deletes = {}, [], []
    pending_polls = 2
    never_finish = False
    capabilities = dict(gmEnabled=True, actions=[dict(id="ui.click", description="点按钮"), dict(id="runtime.state", description="状态")],
                        commands=[dict(name="背包/添加道具", description="加道具", highRisk=False), dict(name="清档", description="删存档", highRisk=True)])

    def log_message(self, *args):
        pass

    def reply(self, status, body):
        raw = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def authorized(self):
        if self.headers.get("Authorization") != "Bearer " + TOKEN:
            self.reply(401, dict(success=False, error="Unauthorized"))
            return False
        if self.headers.get("X-Rev-Session") != SESSION:
            self.reply(409, dict(success=False, error="SessionChanged"))
            return False
        return True

    def do_GET(self):
        if not self.authorized():
            return
        if self.path == "/health":
            return self.reply(200, dict(sessionId=SESSION, pid=1, product="Fake"))
        ident = self.path[len("/jobs/"):]
        job = self.jobs[ident]
        if self.never_finish or job["polls"] < self.pending_polls:
            job["polls"] += 1
            return self.reply(200, dict(success=True, pending=True, id=ident))
        return self.reply(200, job["result"])

    def do_DELETE(self):
        if self.authorized():
            self.deletes.append(self.path)
            self.reply(200, dict(success=True, pending=True))

    def do_POST(self):
        if not self.authorized():
            return
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        self.posts.append(body)
        action, payload, confirm = body["action"], body["payload"], body.get("confirm", False)
        if action == "runtime.capabilities":
            result = dict(success=True, data=self.capabilities)
        elif action == "gm.exec":
            line = payload["command"]
            if line.startswith("清档") and not confirm:
                result = dict(success=False, error="InvalidOperationException", message="高危 GM 要求 confirm=true")
            else:
                result = dict(success=True, data=dict(success=True, message="执行：" + line))
        else:
            result = dict(success=True, data=dict(action=action, payload=payload))
        self.jobs[body["id"]] = dict(polls=0, result=dict(result, id=body["id"], sessionId=SESSION))
        self.reply(202, dict(success=True, id=body["id"], sessionId=SESSION))


class GameTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.project = Path(temp.name) / "proj"
        (self.project / "Library/RevolutionAutomation").mkdir(parents=True)
        FakeRuntime.jobs, FakeRuntime.posts, FakeRuntime.deletes = {}, [], []
        FakeRuntime.never_finish = False
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeRuntime)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.write_endpoint()

    def write_endpoint(self, **override):
        value = dict(protocol=1, url=f"http://127.0.0.1:{self.server.server_port}", token=TOKEN, sessionId=SESSION)
        value.update(override)
        (self.project / "Library/RevolutionAutomation/runtime.json").write_text(json.dumps(value), encoding="utf-8")

    def run_game(self, operation, **kwargs):
        return game.run(operation, self.project, kwargs.pop("session", None), kwargs.pop("timeout", 10), kwargs.pop("confirm", False), **kwargs)

    def test_gm_quotes_every_argument_and_waits_for_the_final_result(self):
        result = self.run_game("gm", name="背包/添加道具", values=["1001", "2 个"])
        self.assertTrue(result["success"])
        self.assertEqual(FakeRuntime.posts[-1]["payload"], dict(command='背包/添加道具 "1001" "2 个"'))
        self.assertIn("执行", result["data"]["message"])

    def test_high_risk_gm_needs_confirm_and_failure_is_reported(self):
        self.assertFalse(self.run_game("gm", name="清档")["success"])
        self.assertTrue(self.run_game("gm", name="清档", confirm=True)["success"])
        self.assertIs(FakeRuntime.posts[-1]["confirm"], True)

    def test_listing_filters_gms_and_actions(self):
        gms = self.run_game("gms", query="道具")
        self.assertEqual([item["name"] for item in gms["items"]], ["背包/添加道具"])
        self.assertEqual(self.run_game("actions")["total"], 2)

    def test_call_passes_payload_through(self):
        result = self.run_game("call", name="ui.click", payload=dict(objectPath="Canvas/Start"))
        self.assertEqual(result["data"]["payload"], dict(objectPath="Canvas/Start"))

    def test_timeout_cancels_the_same_job_and_never_reposts(self):
        FakeRuntime.never_finish = True
        with self.assertRaises(TimeoutError) as raised:
            self.run_game("gm", name="背包/添加道具", values=["1"], timeout=.4)
        self.assertIn("不要重放", str(raised.exception))
        self.assertEqual(len(FakeRuntime.posts), 1, "超时后绝不换新 id 重发")
        self.assertEqual(FakeRuntime.deletes, ["/jobs/" + FakeRuntime.posts[0]["id"]])

    def test_wrong_token_and_changed_session_are_clear_errors(self):
        self.write_endpoint(token="x" * 64)
        with self.assertRaises(RevError):
            self.run_game("status")
        self.write_endpoint(sessionId="z" * 32)
        with self.assertRaises(RevError) as raised:
            self.run_game("status")
        self.assertIn("会话已更换", str(raised.exception))

    def test_only_loopback_endpoints_are_accepted(self):
        for url in ("http://10.0.0.5:1", "https://127.0.0.1:1", "http://user@127.0.0.1:1", "http://127.0.0.1:1/x"):
            self.write_endpoint(url=url)
            with self.assertRaises(RevError, msg=url):
                self.run_game("status")

    def test_missing_endpoint_explains_how_to_open_the_service(self):
        (self.project / "Library/RevolutionAutomation/runtime.json").unlink()
        with self.assertRaises(RevError) as raised:
            self.run_game("status")
        self.assertIn("runtime start", str(raised.exception))

    def test_player_session_directory_is_used_when_given(self):
        session = self.project / "session"
        session.mkdir()
        (self.project / "Library/RevolutionAutomation/runtime.json").rename(session / "runtime.json")
        self.assertTrue(self.run_game("status", session=str(session))["success"])

    def test_gm_line_rejects_what_the_parser_cannot_represent(self):
        for name, values in (("", []), ("a b", []), ("x", ['带"引号']), ("x", ["换\n行"])):
            with self.assertRaises(RevError):
                game.gm_line(name, values)


class GameCliTests(unittest.TestCase):
    def test_parser_and_local_routing(self):
        args = cli.parser().parse_args(["--project", "x", "game", "gm", "背包/添加道具", "1001", "2"])
        self.assertEqual((args.name, args.values), ("背包/添加道具", ["1001", "2"]))
        self.assertFalse(daemon.should_forward(args), "直连 HTTP，不经过常驻服务")
        self.assertIsNone(fastpath.decide(["--project", "x", "game", "status"]))

    def test_set_values_become_payload(self):
        with patch("revautomation.game.run", return_value=dict(success=True)) as run, patch("builtins.print"):
            cli.main(["--project", "x", "game", "call", "ui.input", "--set", "text=你好", "--set", "submit=true"])
        self.assertEqual(run.call_args[0][7], dict(text="你好", submit=True))


if __name__ == "__main__":
    unittest.main()
