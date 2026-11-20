"""验证旧编译回执或错误平台不能放行 AB 发布。"""
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

# 使用被测试的同批工具，避免主机 Python 已装包掩盖问题。
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from revautomation import contracts, ship


class VerifiedShipTests(unittest.TestCase):
    """不启动 Unity，只模拟不同阶段真实回执的结构及失败顺序。"""

    def execute(self, compile_id="compile-A", bundle_target="StandaloneWindows64"):
        """替换单个回执的身份，收集是否实际调用了发布入口。"""
        actions = []
        target = "StandaloneWindows64"
        payloads = {
            "build.settings": {"target": target},
            "editor.compilation": {"requestId": compile_id, "finished": True, "failed": False},
            "ab.build-incremental": {"target": bundle_target, "totalBundles": 2},
            "ab.publish-incremental": {"target": target, "published": True},
        }

        def invoke(client, action, payload, confirm):
            """同编译请求 ID 及各阶段业务结果通过实际管线判断。"""
            actions.append(action)
            return {"success": True, "id": "compile-A", "data": payloads.get(action, {})}

        return ship.run(None, target=target, verified=True, invoke=invoke), actions

    def test_rejects_stale_compilation(self):
        """异代编译成功也不能放行到 AB。"""
        result, actions = self.execute(compile_id="older-job")
        self.assertFalse(result["success"])
        self.assertNotIn("ab.build-incremental", actions)

    def test_rejects_wrong_platform(self):
        """平台错配时不得执行发布。"""
        result, actions = self.execute(bundle_target="Android")
        self.assertFalse(result["success"])
        self.assertNotIn("ab.publish-incremental", actions)

    def test_publish_uses_one_verified_identity(self):
        """正确编译和平台才按映射校验后发布，回执贯穿相同的构建身份。"""
        result, actions = self.execute()
        self.assertTrue(result["success"])
        self.assertEqual(actions[-3:], ["ab.build-incremental", "ab.check-map", "ab.publish-incremental"])
        self.assertEqual({step["buildId"] for step in result["steps"]}, {result["buildId"]})

    def test_inline_contract_is_authoritative(self):
        """在线 DTO 为可选扩展提供输入类型，缺失包依然可诊断。"""
        actual = {
            "id": "resource.inspect", "keys": ["path"], "readOnly": True,
            "inputSchemaJson": '{"type":"object","properties":{"path":{"type":"string"}}}',
        }
        offline = {"actions": [{"id": "resource.inspect", "keys": ["path"]}], "parameterTypes": {"path": "string"}}
        observed = contracts.audit(offline, {"protocol": 1, "actions": [actual]})
        self.assertTrue(observed["success"])
        self.assertEqual(observed["onlineActions"][0]["inputSchema"]["properties"]["path"]["type"], "string")
        actual["readOnly"] = False
        saved = dict(offline["actions"][0], readOnly=True)
        self.assertFalse(contracts.merge_editor_action(actual, saved)["readOnly"])
        actual["keys"] += ["extra"]
        self.assertFalse(contracts.audit(offline, {"protocol": 1, "actions": [actual]})["success"])

    def test_failed_smoke_still_closes_owned_play(self):
        """动作回执失败后仍关闭本次启动的 Play，阻止构建 Player。"""
        actions = []

        def invoke(client, action, payload, confirm):
            """复用最小 Editor 回执，保证验证确实经过通常的构建入口。"""
            actions.append(action)
            data = {
                "build.settings": {"target": "StandaloneWindows64"},
                "editor.status": {"playing": False},
                "editor.compilation": {"requestId": "fresh", "finished": True, "failed": False},
            }
            return {"success": True, "id": "fresh", "data": data.get(action, {})}

        result = ship.run(None, target="StandaloneWindows64", ab=False, player="delivery", verified=True,
                          invoke=invoke, smoke=lambda: {"success": False})
        self.assertFalse(result["success"])
        self.assertEqual(actions[-2:], ["editor.play", "editor.stop"])

    def test_smoke_keeps_build_budget_outside_mcp_step_limit(self):
        """整包预算可达四小时，RevMCP 仍只接收受支持的单工单超时。"""
        entry = Path(__file__)
        plan = {"steps": [{}], "finally": [{}]}
        with patch.object(ship.subprocess, "run", return_value=Mock(returncode=0, stdout=b'{"success":true}')) as run:
            self.assertTrue(ship.smoke_runner(entry, entry.parent, plan, 14400)()["success"])
        command = run.call_args.args[0]
        self.assertEqual(command[command.index("--timeout") + 1], "3600")
        self.assertEqual(run.call_args.kwargs["timeout"], 14400)
