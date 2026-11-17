"""Gal 的参数翻译和查询退出码回归，Unity 路径需在目标编辑器实测。"""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.cli import editor_outcome, parser, payload_of, validate_action


class GalCliTests(unittest.TestCase):
    def test_inline_document_check_only(self):
        with tempfile.TemporaryDirectory() as work:
            document = Path(work) / "story.json"
            document.write_text('{"Schema":1,"Title":"纸上星光"}', encoding="utf-8-sig")
            args = parser().parse_args(["gal", "story.import", "--document", str(document), "--check-only"])
            payload = payload_of(args)
            self.assertEqual(json.loads(payload["document"])["Title"], "纸上星光")
            self.assertEqual(set(payload), {"document", "checkOnly"})
            self.assertTrue(payload["checkOnly"])
            validate_action("gal.story.import", payload)

    def test_create_arguments(self):
        args = parser().parse_args(["gal", "story.create", "--path", "Assets/GameRes/Gal/First.asset",
                                    "--id", "first", "--title", "初见", "--bundle", "gal_first"])
        payload = payload_of(args)
        self.assertEqual(payload, {"id": "first", "title": "初见", "path": "Assets/GameRes/Gal/First.asset",
                                   "bundle": "gal_first"})
        validate_action("gal.story.create", payload)

    def test_generate_from_document_with_default_path_and_bundle(self):
        with tempfile.TemporaryDirectory() as work:
            document = Path(work) / "story.json"
            document.write_text('{"Schema":1,"Id":"lantern"}', encoding="utf-8-sig")
            args = parser().parse_args(["gal", "story.generate", "--document", str(document), "--check-only"])
            payload = payload_of(args)
            self.assertEqual(set(payload), {"document", "checkOnly"})
            self.assertEqual(json.loads(payload["document"])["Id"], "lantern")
            self.assertTrue(payload["checkOnly"])
            validate_action("gal.story.generate", payload)

    def test_panel_generation_accepts_shared_theme(self):
        path = "Assets/GameRes/UI/Gal/RevGalPanelTheme.asset"
        args = parser().parse_args(["gal", "ui.create", "--theme", path, "--overwrite"])
        payload = payload_of(args)
        self.assertEqual(payload, {"theme": path, "overwrite": True})
        validate_action("gal.ui.create", payload)

    def test_invalid_validation_becomes_failure_with_issues(self):
        for action in ("gal.story.validate", "gal.story.contract", "gal.story.generate"):
            with self.subTest(action=action):
                result = editor_outcome(action, {"success": True, "data": {"valid": False, "issues": ["missing"]}})
                self.assertFalse(result["success"])
                self.assertEqual(result["data"]["issues"], ["missing"])
                self.assertEqual(result["error"], "GalValidationFailed")

    def test_host_contract_is_readonly_and_takes_story_asset(self):
        args = parser().parse_args(["gal", "story.contract", "--path", "Assets/Gal.asset"])
        payload = payload_of(args)
        self.assertEqual(payload, {"path": "Assets/Gal.asset"})
        validate_action("gal.story.contract", payload)
        manifest = Path(__file__).resolve().parents[1] / "commands.json"
        spec = next(s for s in json.loads(manifest.read_text(encoding="utf-8"))["actions"] if s["id"] == "gal.story.contract")
        self.assertTrue(spec["readOnly"])

    def test_ordered_simulation(self):
        args = parser().parse_args(["gal", "story.simulate", "--path", "Assets/Gal.asset",
                                    "--entry", "intro", "--step", "advance", "--step", "choose:lamp"])
        self.assertEqual(payload_of(args)["operations"], ["advance", "choose:lamp"])

    def test_find_node_with_filters(self):
        args = parser().parse_args(["gal", "node.find", "--path", "Assets/Gal.asset", "--query", "旅人", "--kind", "Line",
                                    "--limit", "5"])
        payload = payload_of(args)
        self.assertEqual(payload, {"path": "Assets/Gal.asset", "query": "旅人", "kind": "Line", "limit": 5})
        validate_action("gal.node.find", payload)

    def test_node_documents_use_same_transport_with_hash_and_dry_run(self):
        documents = (
            ("node.add", {"Id": "inserted", "Kind": "Line", "Text": "加一句", "Next": "closing"}, []),
            ("node.update", {"Text": "只修改正文", "Stage": {"Left": "-"}}, ["--id", "intro"]),
            ("node.patch", {"operations": [{"op": "remove", "id": "old", "redirect": "after"}]}, []),
        )
        with tempfile.TemporaryDirectory() as work:
            document = Path(work) / "node.json"
            for operation, contents, extra in documents:
                with self.subTest(operation=operation):
                    document.write_text(json.dumps(contents, ensure_ascii=False), encoding="utf-8-sig")
                    args = parser().parse_args(["gal", operation, "--path", "Assets/Gal.asset", "--document", str(document),
                                                "--expected-hash", "observed", "--check-only", *extra])
                    payload = payload_of(args)
                    self.assertEqual(json.loads(payload["document"]), contents)
                    self.assertEqual(payload["expectedHash"], "observed")
                    self.assertTrue(payload["checkOnly"])
                    validate_action("gal." + operation, payload)

    def test_remove_redirects_by_node_id(self):
        args = parser().parse_args(["gal", "node.remove", "--path", "Assets/Gal.asset", "--id", "old",
                                    "--redirect", "after", "--expected-hash", "observed"])
        payload = payload_of(args)
        self.assertEqual(payload, {"path": "Assets/Gal.asset", "id": "old", "redirect": "after", "expectedHash": "observed"})
        validate_action("gal.node.remove", payload)
