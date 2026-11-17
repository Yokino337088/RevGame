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
        for action in ("gal.story.validate", "gal.story.generate"):
            with self.subTest(action=action):
                result = editor_outcome(action, {"success": True, "data": {"valid": False, "issues": ["missing"]}})
                self.assertFalse(result["success"])
                self.assertEqual(result["data"]["issues"], ["missing"])
                self.assertEqual(result["error"], "GalValidationFailed")

    def test_ordered_simulation(self):
        args = parser().parse_args(["gal", "story.simulate", "--path", "Assets/Gal.asset",
                                    "--entry", "intro", "--step", "advance", "--step", "choose:lamp"])
        self.assertEqual(payload_of(args)["operations"], ["advance", "choose:lamp"])
