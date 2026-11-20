"""验证性能轮次仍调用正常复位，采样缺失及不匹配基线明确失败。"""
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path[:0] = [str(Path(__file__).resolve().parents[1]),
                str(Path(__file__).resolve().parents[2] / "Revolution.RevCLI")]

from revmcp import benchmark
from revautomation.client import RevError


class BenchmarkTests(unittest.TestCase):
    """通过可复现的回执数据覆盖指标提取、重复轮次和比较门禁。"""

    def make(self):
        """提供相同负载下的预热、三轮测量与复位约定。"""
        plan = {"steps": [{"id": "sample"}], "finally": [{"id": "restore"}]}
        spec = {"identity": {"case": "horde", "revision": "v1", "device": "host", "workload": {"enemies": 240}},
                "metrics": {"p95": "/frame/p95"}, "warmup": 1, "repeat": 3}
        return plan, spec

    def test_warmup_and_comparison(self):
        """预热不进入中位数，只有同口径基线参与比较。"""
        plan, spec = self.make()
        readings = [{"success": True, "frame": {"p95": v}} for v in (70, 15, 12, 13)]
        with patch.object(benchmark.workflow, "run", side_effect=readings) as play:
            result = benchmark.run(None, plan, spec)
        self.assertEqual(play.call_count, 4)
        self.assertTrue(all(call.args[1]["finally"] for call in play.call_args_list))
        self.assertEqual(result["benchmark"]["median"], {"p95": 13})
        spec["baseline"] = result["benchmark"]
        spec["identity"] = dict(spec["identity"], revision="v2")
        with patch.object(benchmark.workflow, "run", return_value={"success": True, "frame": {"p95": 10}}):
            result = benchmark.run(None, plan, spec)
        self.assertEqual(result["benchmark"]["comparison"]["p95"]["delta"], -3)
        spec["identity"]["device"] = "different"
        with self.assertRaises(RevError):
            benchmark.run(None, plan, spec)

    def test_invalid_samples_and_failed_cleanup(self):
        """负值哨兵不作为测量，任一轮失败也不继续造局。"""
        plan, spec = self.make()
        with patch.object(benchmark.workflow, "run", return_value={"success": True, "frame": {"p95": -1}}):
            with self.assertRaises(RevError):
                benchmark.run(None, plan, spec)
        with patch.object(benchmark.workflow, "run", return_value={"success": False, "cleanup": [{"response": {"success": False}}]}) as play:
            result = benchmark.run(None, plan, spec)
        self.assertEqual(result["failedRound"], 1)
        self.assertEqual(play.call_count, 1)
