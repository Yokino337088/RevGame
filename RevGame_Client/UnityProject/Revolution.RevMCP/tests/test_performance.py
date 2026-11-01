"""Bounded discovery and evidence isolation; speed must not weaken dispatch safety."""
import json
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT.parent / "Revolution.RevCLI")]
from revmcp.backends import Hub, RuntimeClient
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


class EvidencePerformanceTests(unittest.TestCase):
    def test_query_keeps_json_without_exporting_html(self):
        server = Server(Mock())
        with patch("revautomation.report.export_evidence") as export:
            self.assertIsNone(server.write_evidence(dict(success=True, data={}), "unused.json"))
            export.assert_not_called()

    def test_plan_evidence_isolated_and_standalone_screenshot_collected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            shot = root / "shot.png"
            shot.write_bytes(b"test-fixture")
            server = Server(Mock())
            with patch("revautomation.report.export_evidence", return_value={"html": "report"}) as export:
                server.write_evidence(dict(success=True, steps=[]), root / "first.json")
                self.assertEqual(export.call_args.args[0], root / "first")
                server.write_evidence(dict(success=True, data=dict(mimeType="image/png", path=str(shot))), root / "second.json")
                self.assertEqual(export.call_args.args[0], root / "second")
                self.assertTrue((root / "second" / "01-shot.png").is_file())
                self.assertFalse((root / "first" / "01-shot.png").exists())


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
