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

    def test_invalid_validation_becomes_failure_with_issues(self):
        result = editor_outcome("gal.story.validate", {"success": True, "data": {"valid": False, "issues": ["missing"]}})
        self.assertFalse(result["success"])
        self.assertEqual(result["data"]["issues"], ["missing"])
        self.assertEqual(result["error"], "GalValidationFailed")

    def test_ordered_simulation(self):
        args = parser().parse_args(["gal", "story.simulate", "--path", "Assets/Gal.asset",
                                    "--entry", "intro", "--step", "advance", "--step", "choose:lamp"])
        self.assertEqual(payload_of(args)["operations"], ["advance", "choose:lamp"])
