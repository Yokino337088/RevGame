"""Bounded discovery and evidence isolation; speed must not weaken dispatch safety."""
import json
from pathlib import Path
import struct
import sys
import tempfile
import threading
import unittest
import zlib
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT.parent / "Revolution.RevCLI")]
from revmcp.backends import Hub, RuntimeClient
from revmcp.reporting import run_with_reports
from revmcp.server import Server
from revautomation.client import RevCancelled, RevError


class DiscoveryPerformanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.hub = Hub(cli_root=ROOT.parent / "Revolution.RevCLI", reports=self.root / "reports")
        self.editor = Mock()
        self.editor.endpoint.return_value = dict(sessionId="first", playing=False, paused=False, compiling=False)
        self.hub.editor = self.editor
        self.hub.game_call = Mock(side_effect=RevError("No Play"))
        self.editor.call.side_effect = self.respond

    def respond(self, action, *args, **kwargs):
        if action == "editor.capabilities":
            return dict(success=True, data=dict(actions=[dict(id="editor.status", keys=[]), dict(id="editor.undo", keys=[])], errors=[]))
        return dict(success=True, data={})

    def count(self, action):
        return sum(c.args[0] == action for c in self.editor.call.call_args_list)

    def test_short_queries_reuse_editor_catalog_without_runtime_requests(self):
        for _ in range(3):
            self.assertTrue(self.hub.call("editor", "editor.status")["success"])
        self.assertEqual(self.count("editor.capabilities"), 1)
        self.assertEqual(self.count("editor.status"), 3)
        self.hub.game_call.assert_not_called()

    def test_session_domain_play_and_timeout_invalidate(self):
        self.hub.call("editor", "editor.status")
        self.editor.endpoint.return_value["sessionId"] = "second"
        self.hub.call("editor", "editor.status")
        self.editor.endpoint.return_value["domainId"] = "reloaded"
        self.hub.call("editor", "editor.status")
        self.editor.endpoint.return_value["playing"] = True
        self.hub.call("editor", "editor.status")
        self.hub._editor_catalog_until = 0
        self.hub.call("editor", "editor.status")
        self.assertEqual(self.count("editor.capabilities"), 5)

    def test_offline_endpoint_does_not_reuse_cached_capabilities(self):
        self.hub.call("editor", "editor.status")
        self.editor.endpoint.return_value = None
        self.hub.call("editor", "editor.status")
        self.hub.call("editor", "editor.status")
        self.assertEqual(self.count("editor.capabilities"), 3)

    def test_mutation_and_failed_response_invalidate_without_replay(self):
        self.hub.call("editor", "editor.undo")
        self.hub.call("editor", "editor.status")
        self.assertEqual(self.count("editor.capabilities"), 2)
        self.editor.call.side_effect = lambda action, *a, **k: self.respond(action) if action == "editor.capabilities" else dict(success=False)
        self.hub.call("editor", "editor.undo")
        self.hub.call("editor", "editor.status")
        self.assertEqual(self.count("editor.undo"), 2)
        self.assertEqual(self.count("editor.capabilities"), 3)

    def test_exception_invalidates_and_never_resubmits(self):
        self.hub.call("editor", "editor.status")
        self.editor.call.side_effect = TimeoutError("already submitted")
        with self.assertRaises(TimeoutError):
            self.hub.call("editor", "editor.status")
        self.assertIsNone(self.hub._editor_catalog)
        self.assertEqual(self.count("editor.status"), 2)

    def test_explicit_discover_is_fresh_and_cancel_precedes_lookup(self):
        self.hub.discover()
        self.hub.discover()
        self.assertEqual(self.count("editor.capabilities"), 2)
        token = threading.Event()
        token.set()
        with self.assertRaises(RevCancelled):
            self.hub.call("editor", "editor.status", cancel=token)
        self.assertEqual(self.count("editor.status"), 0)

    def test_cli_does_not_need_unity_discovery(self):
        with patch("revmcp.backends.player.execute", return_value=dict(success=True)):
            self.hub.call("cli", "player.status", {"directory": str(self.root)})
        self.editor.call.assert_not_called()
        self.hub.game_call.assert_not_called()

    def test_unknown_action_is_not_dispatched_from_offline_manifest(self):
        with self.assertRaises(RevError):
            self.hub.call("editor", "asset.delete", {"path": "Assets/not-present"}, confirm=True)
        self.assertEqual(self.count("asset.delete"), 0)


class EvidenceRoutingTests(unittest.TestCase):
    """覆盖旧输出禁门、被嵌套 Unity 工程的文档选择及各调用入口的产物数量。"""

    def setUp(self):
        """用临时目录模拟 Unity 进程所属仓库，所有真实写出仅发生在本测试内。"""
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        (self.root / ".git").mkdir()
        self.project = self.root / "Client/Unity/Project"
        (self.project / "Assets").mkdir(parents=True)
        (self.project / "ProjectSettings").mkdir()
        self.hub = Hub(project=self.project, cli_root=ROOT.parent / "Revolution.RevCLI")
        self.docs = self.root / "Client/文档/AI自动化测试报告"
        self.legacy = self.project.parent / "Revolution.RevMCP/results"

    def shot(self, index):
        """构造一张有效的 1×1 PNG，实际走预览缩放和 GIF 生成。"""
        path = self.root / f"source-{index}.png"
        data = b"\x89PNG\r\n\x1a\n"
        for kind, content in ((b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)),
                              (b"IDAT", zlib.compress(bytes((0, 0, index, 32)))), (b"IEND", b"")):
            data += struct.pack(">I", len(content)) + kind + content + struct.pack(">I", zlib.crc32(kind + content))
        path.write_bytes(data)
        return dict(mimeType="image/png", path=str(path))

    def test_standalone_screenshot_keeps_image_and_raw_json_only(self):
        """单次截图仍返回真实 MCP 画面，现有文档目录中无额外 HTML。"""
        self.docs.mkdir(parents=True)
        self.hub.call = Mock(return_value=dict(success=True, data=self.shot(1)))
        response = Server(self.hub)._call(dict(name="rev_call",
            arguments=dict(target="game", action="runtime.screenshot")), None)
        result = response["structuredContent"]
        self.assertEqual(Path(result["reportPath"]).parent, self.root / ".codex/work/revmcp")
        self.assertTrue(any(item["type"] == "image" for item in response["content"]))
        self.assertFalse(list(self.docs.rglob("*")))
        self.assertFalse(self.legacy.exists())

    def test_formal_plan_discovers_document_directory_and_bounds_attachments(self):
        """在现有测试文档生成一份报告，原始 JSON 与大批截图来源不复制到文档树。"""
        self.docs.mkdir(parents=True)
        result = dict(success=True, name="界面回归", steps=[self.shot(i) for i in range(6)], cleanup=[])
        with patch("revmcp.reporting.workflow.run", return_value=result):
            saved = run_with_reports(self.hub, {})
        report = Path(saved["evidencePath"])
        self.assertEqual(report.parent.parent, self.docs)
        self.assertIn("画面/01.png", report.read_text(encoding="utf-8"))
        self.assertEqual(len(list(report.parent.rglob("*.png"))), 3)
        self.assertEqual(len(list(report.parent.rglob("*.gif"))), 1)
        self.assertFalse(list(self.docs.rglob("*.json")))
        self.assertEqual(list(self.hub.reports.iterdir()), [Path(saved["reportPath"])])
        self.assertFalse(self.legacy.exists())

    def test_explicit_document_path_wins_without_becoming_a_game_argument(self):
        """报告落到项目内点名的主题路径，业务步骤不会收到目录字段。"""
        self.docs.mkdir(parents=True)
        alternate = self.root / "Client/文档/本轮验收"
        with patch("revmcp.reporting.workflow.run", return_value=dict(success=True, steps=[], cleanup=[])) as run:
            result = run_with_reports(self.hub, {"name": "确认", "reportDirectory": alternate.relative_to(self.root).as_posix()})
        self.assertEqual(run.call_args.args[1], {"name": "确认"})
        self.assertEqual(Path(result["evidencePath"]).parent.parent, alternate)
        self.assertFalse(list(self.docs.rglob("*")))

    def test_forbidden_output_has_no_side_effects(self):
        """旧 results 和越界文档路径在写文件或执行计划前即被拒绝。"""
        with self.assertRaises(ValueError):
            Hub(project=self.project, cli_root=ROOT.parent / "Revolution.RevCLI", reports=self.legacy / "nested")
        for path in (self.legacy, self.hub.reports, self.root.parent / "escape"):
            with self.subTest(path=path), patch("revmcp.reporting.workflow.run") as run:
                with self.assertRaises(ValueError):
                    run_with_reports(self.hub, {"reportDirectory": str(path)})
                run.assert_not_called()
        self.assertFalse(self.legacy.exists())
        self.assertFalse(self.hub.reports.exists())

    def test_missing_document_directory_does_not_guess_or_recreate_legacy(self):
        """工作区无现有文档时保留真实工单，并说明 HTML 的路径需求。"""
        with patch("revmcp.reporting.workflow.run", return_value=dict(success=True, steps=[], cleanup=[])):
            result = run_with_reports(self.hub, {})
        self.assertTrue(Path(result["reportPath"]).is_file())
        self.assertIn("reportDirectory", result["evidenceError"])
        self.assertFalse(list(self.root.rglob("*.html")))
        self.assertFalse(self.legacy.exists())


class CompactToolsTests(unittest.TestCase):
    def test_compact_tools_never_discovers_at_initialization(self):
        hub = Mock()
        tools = Server(hub, compact_tools=True).tools()["tools"]
        self.assertEqual([t["name"] for t in tools], ["rev_discover", "rev_call", "rev_run"])
        hub.discover.assert_not_called()

    def test_filtered_discovery_pages_without_losing_failure_reasons(self):
        hub = Mock()
        hub.discover.return_value = dict(success=True, actions=[dict(id="material.inspect", target="editor"), dict(id="material.set", target="editor"),
                                                              dict(id="scene.list", target="editor")], commands=[], unavailable=[dict(target="game", reason="No Play")])
        hub.save.return_value = "query.json"
        result = Server(hub)._call(dict(name="rev_discover", arguments=dict(query="material", target="editor", offset=1, limit=1)), None)["structuredContent"]
        self.assertEqual(result["actionTotal"], 2)
        self.assertEqual(result["actions"][0]["id"], "material.set")
        self.assertEqual(result["unavailable"][0]["reason"], "No Play")


class RuntimePollingTests(unittest.TestCase):
    def client(self):
        client = RuntimeClient.__new__(RuntimeClient)
        client.timeout = 1
        client.endpoint = dict(sessionId="same")
        return client

    def test_short_query_polls_same_identity_with_five_ms_first_wait(self):
        client = self.client()
        calls = []
        def request(method, path, data=None):
            calls.append((method, path, data))
            if method == "POST":
                return dict(accepted=True)
            if len(calls) == 2:
                return dict(pending=True)
            return dict(id=calls[0][2]["id"], sessionId="same", success=True)
        client.request = request
        with patch("revmcp.backends.time.sleep") as sleep:
            self.assertTrue(client.call("state")["success"])
            self.assertAlmostEqual(sleep.call_args.args[0], .005)
        self.assertEqual(sum(m == "POST" for m, _, _ in calls), 1)
        self.assertEqual(calls[1][1], calls[2][1])

    def test_cancelled_token_does_not_post(self):
        client = self.client()
        client.request = Mock()
        token = threading.Event()
        token.set()
        with self.assertRaises(RevCancelled):
            client.call("mutation", cancel=token)
        client.request.assert_not_called()


if __name__ == "__main__":
    unittest.main()
