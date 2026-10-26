# -*- coding: utf-8 -*-
"""报告命名与版式的回归测试：中文标题+日期，样式继承项目文档，不再写 report.html。"""
import sys
import tempfile
import unittest
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from revautomation.report import REPORT_CSS, export_evidence, report_file_name


class ReportNamingTest(unittest.TestCase):
    def test_file_name_has_chinese_title_and_date(self):
        name = report_file_name("第一章流程自动化测试", date(2026, 10, 25))
        self.assertEqual(name, "第一章流程自动化测试_2026-10-25.html")

    def test_illegal_characters_are_replaced(self):
        name = report_file_name('登录/战斗:验收?"*', date(2026, 10, 25))
        for bad in '\\/:*?"<>|':
            self.assertNotIn(bad, name)
        self.assertTrue(name.endswith("_2026-10-25.html"))

    def test_empty_title_falls_back(self):
        self.assertEqual(report_file_name("   ", date(2026, 10, 25)), "自动化测试报告_2026-10-25.html")

    def test_export_writes_dated_file_and_project_style(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            (directory / "01-shot.png").write_bytes(b"\x89PNG\r\n\x1a\n")
            info = export_evidence(directory, "登录流程验收", "单步截图", {"结论": "通过"})
            target = Path(info["html"])
            self.assertEqual(target.name, "登录流程验收_%s.html" % date.today().isoformat())
            self.assertFalse((directory / "report.html").exists())
            text = target.read_text(encoding="utf-8")
            # 视觉必须来自项目那套工程手册风，而不是另配色。
            self.assertIn("--green:#326956", text)
            self.assertIn("--paper:#f6f7f2", text)
            self.assertIn("登录流程验收", text)
            self.assertIn("01-shot.png", text)

    def test_export_creates_missing_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp) / "还没建" / "结果目录"
            info = export_evidence(directory, "空目录首次导出", "无截图")
            self.assertTrue(Path(info["html"]).is_file())


class ReportStyleTest(unittest.TestCase):
    def test_style_is_self_contained(self):
        self.assertNotIn("<link", REPORT_CSS)
        self.assertNotIn("http", REPORT_CSS)


if __name__ == "__main__":
    unittest.main()
