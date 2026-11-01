"""开机恢复与一键打包：不依赖 Unity，验证残留状态判定、编辑器定位与分步失败语义。"""
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation import client as client_module
from revautomation import cli, connect, daemon, daemon_link, fastpath, ship
from revautomation.client import RevClient, atomic_json, find_unity, hub_editors, read_remembered_editor, remember_editor, stale_lock
from revautomation.errors import RevError


def make_project(root: Path, version="2022.3.15f1c1", bridge=True) -> Path:
    project = root / "proj"
    (project / "Assets").mkdir(parents=True)
    (project / "ProjectSettings").mkdir()
    if version:
        (project / "ProjectSettings/ProjectVersion.txt").write_text(f"m_EditorVersion: {version}\n", encoding="utf-8")
    if bridge:
        folder = project / "Assets/Revolution/Editor/RevAutomation"
        folder.mkdir(parents=True)
        (folder / "RevAutomationBridge.cs").write_text("// fixture", encoding="utf-8")
    return project


def fake_exe(path: Path) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("x", encoding="utf-8")
    return path


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = make_project(self.root)
        env = {k: v for k, v in os.environ.items() if k != "UNITY_EDITOR"}
        env["APPDATA"] = str(self.root / "appdata")
        env["ProgramFiles"] = str(self.root / "pf")
        patcher = patch.dict(os.environ, env, clear=True)
        patcher.start()
        self.addCleanup(patcher.stop)
        (self.root / "appdata/UnityHub").mkdir(parents=True)

    def hub(self, rows):
        (self.root / "appdata/UnityHub/editors-v2.json").write_text(json.dumps(dict(schema_version="2", data=rows)), encoding="utf-8")


class FindUnityTests(Base):
    def test_non_default_install_is_found_through_hub_config(self):
        exe = fake_exe(self.root / "E盘/Unity安装/2022.3.15f1c1/Editor/Unity.exe")
        other = fake_exe(self.root / "E盘/Unity安装/2023.1.0f1/Editor/Unity.exe")
        self.hub([dict(version="2023.1.0f1", location=[str(other)]), dict(version="2022.3.15f1c1", location=[str(exe)])])
        self.assertEqual(find_unity(self.project), exe)

    def test_secondary_install_path_is_searched(self):
        exe = fake_exe(self.root / "second/2022.3.15f1c1/Editor/Unity.exe")
        (self.root / "appdata/UnityHub/secondaryInstallPath.json").write_text(json.dumps(str(self.root / "second")), encoding="utf-8")
        self.assertEqual(find_unity(self.project), exe)

    def test_wrong_version_is_refused_rather_than_guessed(self):
        other = fake_exe(self.root / "x/2021.3.0f1/Editor/Unity.exe")
        self.hub([dict(version="2021.3.0f1", location=[str(other)])])
        with self.assertRaises(RevError) as raised:
            find_unity(self.project)
        self.assertIn("2022.3.15f1c1", str(raised.exception))
        self.assertIn("2021.3.0f1", str(raised.exception))

    def test_explicit_path_wins_and_missing_explicit_path_fails(self):
        exe = fake_exe(self.root / "custom/Unity.exe")
        self.assertEqual(find_unity(self.project, str(exe)), exe.resolve())
        with self.assertRaises(RevError):
            find_unity(self.project, str(self.root / "missing.exe"))

    def test_remembered_editor_survives_missing_project_version(self):
        project = make_project(self.root / "b", version=None)
        exe = fake_exe(self.root / "remembered/Unity.exe")
        remember_editor(project, exe, "2022.3.15f1c1")
        self.assertEqual(find_unity(project), exe)
        self.assertEqual(read_remembered_editor(project)["version"], "2022.3.15f1c1")

    def test_remembered_editor_of_another_version_is_ignored(self):
        exe = fake_exe(self.root / "old/Unity.exe")
        remember_editor(self.project, exe, "2021.3.0f1")
        with self.assertRaises(RevError):
            find_unity(self.project)

    def test_remembered_editor_that_was_uninstalled_falls_back_to_hub(self):
        remember_editor(self.project, self.root / "gone/Unity.exe", "2022.3.15f1c1")
        exe = fake_exe(self.root / "hub/Unity.exe")
        self.hub([dict(version="2022.3.15f1c1", location=[str(exe)])])
        self.assertEqual(find_unity(self.project), exe)

    def test_corrupt_hub_files_do_not_crash(self):
        (self.root / "appdata/UnityHub/editors-v2.json").write_text("{坏", encoding="utf-8")
        (self.root / "appdata/UnityHub/editors.json").write_text("[1,2]", encoding="utf-8")
        self.assertEqual(hub_editors(), [])


@unittest.skipUnless(os.name == "nt", "独占锁语义只在 Windows 上成立")
class StaleLockTests(Base):
    def lock(self):
        path = self.project / "Temp/UnityLockfile"
        path.parent.mkdir(exist_ok=True)
        path.write_text("", encoding="utf-8")
        return path

    def test_no_lockfile_is_unknown(self):
        self.assertIsNone(stale_lock(self.project))

    def test_leftover_lockfile_after_power_loss_is_stale_and_project_is_not_open(self):
        self.lock()
        self.assertTrue(stale_lock(self.project))
        self.assertFalse(RevClient(self.project, "live")._editor_open())

    def test_exclusively_held_lockfile_means_unity_is_running(self):
        path = self.lock()
        with patch("revautomation.client.os.open", side_effect=PermissionError(13, "held")):
            self.assertIs(stale_lock(self.project), False)
            self.assertTrue(RevClient(self.project, "live")._editor_open())
        self.assertTrue(path.exists())

    def test_fresh_heartbeat_with_unheld_lock_is_not_an_endpoint(self):
        """强杀后 15 秒内心跳还新鲜：不能据此认为 Unity 在线，否则 launch 会去等一个死进程。"""
        import time
        self.lock()
        root = self.project / "Library/RevolutionAutomation"
        root.mkdir(parents=True)
        atomic_json(root / "endpoint.json", dict(pid=1, project=str(self.project), protocol=1, heartbeatUnix=time.time()))
        client = RevClient(self.project, "live")
        self.assertIsNone(client.endpoint())
        with patch("revautomation.client.os.open", side_effect=PermissionError(13, "held")):
            self.assertIsNotNone(client.endpoint(), "锁被持有时心跳照常可信")

    def test_wait_ready_fails_fast_when_unity_is_gone(self):
        import time
        self.lock()
        client = RevClient(self.project, "live", timeout=120)
        started = time.monotonic()
        with self.assertRaises(RevError) as raised:
            client.wait_ready()
        self.assertLess(time.monotonic() - started, 15)
        self.assertIn("connect", str(raised.exception))

    def test_reused_pid_in_old_endpoint_does_not_count_as_open(self):
        """重启后 endpoint.json 里的 PID 可能被别的进程占用，不能据此判定 Unity 在运行。"""
        root = self.project / "Library/RevolutionAutomation"
        root.mkdir(parents=True)
        atomic_json(root / "endpoint.json", dict(pid=os.getpid(), project=str(self.project), protocol=1, heartbeatUnix=0))
        self.assertFalse(RevClient(self.project, "live")._editor_open())


class InspectTests(Base):
    def setUp(self):
        super().setUp()
        patcher = patch("revautomation.connect._toolchain", return_value=None)
        patcher.start()
        self.addCleanup(patcher.stop)

    def step(self, report, name):
        return next(item for item in report["steps"] if item["step"] == name)

    def test_offline_project_reports_each_step_without_side_effects(self):
        exe = fake_exe(self.root / "u/Unity.exe")
        self.hub([dict(version="2022.3.15f1c1", location=[str(exe)])])
        before = sorted(str(p) for p in self.project.rglob("*"))
        report = connect.inspect(self.project)
        self.assertTrue(report["success"])
        self.assertEqual(report["state"], "offline")
        self.assertEqual(report["editor"], str(exe))
        self.assertEqual(sorted(str(p) for p in self.project.rglob("*")), before, "体检必须只读")

    def test_missing_bridge_code_blocks_and_says_how_to_fix(self):
        project = make_project(self.root / "nobridge", bridge=False)
        report = connect.inspect(project)
        self.assertFalse(report["success"])
        self.assertIn("Assets/Revolution", self.step(report, "bridge-code")["fix"])

    def test_invalid_project_stops_early(self):
        report = connect.inspect(self.root / "nothing")
        self.assertEqual((report["success"], report["state"]), (False, "invalid-project"))

    def test_unknown_unity_location_gives_a_fix(self):
        report = connect.inspect(self.project)
        self.assertFalse(report["success"])
        self.assertIn("--unity", self.step(report, "unity-editor")["fix"])

    @unittest.skipUnless(os.name == "nt", "独占锁语义只在 Windows 上成立")
    def test_fresh_heartbeat_of_a_dead_editor_is_not_online(self):
        """崩溃后 15 秒内心跳还算新鲜，但锁已经没人持有：不能判成在线。"""
        (self.project / "Temp").mkdir()
        (self.project / "Temp/UnityLockfile").write_text("", encoding="utf-8")
        with patch.object(RevClient, "endpoint", return_value=dict(pid=1, sessionId="s")):
            self.assertEqual(connect.inspect(self.project)["state"], "stale-lock")

    @unittest.skipUnless(os.name == "nt", "独占锁语义只在 Windows 上成立")
    def test_stale_lock_is_reported_as_recoverable(self):
        exe = fake_exe(self.root / "u/Unity.exe")
        self.hub([dict(version="2022.3.15f1c1", location=[str(exe)])])
        (self.project / "Temp").mkdir()
        (self.project / "Temp/UnityLockfile").write_text("", encoding="utf-8")
        report = connect.inspect(self.project)
        self.assertEqual(report["state"], "stale-lock")
        self.assertTrue(report["success"], "残留锁不阻塞 connect")


class ConnectTests(Base):
    def setUp(self):
        super().setUp()
        exe = fake_exe(self.root / "u/Unity.exe")
        self.hub([dict(version="2022.3.15f1c1", location=[str(exe)])])
        self.exe = exe
        for target, value in (("revautomation.connect._toolchain", None),
                              ("revautomation.connect.daemon.start", dict(success=True, pid=123)),
                              ("revautomation.connect._capability_gap", dict(liveActions=170, missingInUnity=[], unknownToCli=[]))):
            patcher = patch(target, return_value=value)
            setattr(self, target.rsplit(".", 1)[1].lstrip("_"), patcher.start())
            self.addCleanup(patcher.stop)

    def run_connect(self, endpoint=None, **kwargs):
        with patch.object(RevClient, "endpoint", return_value=endpoint), \
                patch.object(RevClient, "launch", return_value=dict(success=True)) as launch, \
                patch.object(RevClient, "wait_ready", return_value=dict(success=True)) as ready, \
                patch.object(RevClient, "call", return_value=dict(success=True, data=dict(editorPath=str(self.exe), unityVersion="2022.3.15f1c1"))):
            return connect.connect(self.project, **kwargs), launch, ready

    def test_cold_start_launches_unity_then_starts_service_and_remembers_editor(self):
        result, launch, _ = self.run_connect()
        self.assertTrue(result["success"])
        self.assertEqual(result["state"], "connected")
        launch.assert_called_once()
        self.start.assert_called_once()
        self.assertEqual(read_remembered_editor(self.project)["path"], str(self.exe))

    def test_already_online_is_idempotent_and_does_not_launch_again(self):
        result, launch, ready = self.run_connect(endpoint=dict(pid=1, sessionId="s"))
        self.assertTrue(result["success"])
        launch.assert_not_called()
        ready.assert_called_once()

    def test_no_launch_reports_failure_instead_of_opening_unity(self):
        result, launch, _ = self.run_connect(launch=False)
        self.assertFalse(result["success"])
        launch.assert_not_called()

    def test_no_service_skips_daemon(self):
        result, _, _ = self.run_connect(start_daemon=False)
        self.assertTrue(result["success"])
        self.start.assert_not_called()

    def test_bridge_older_than_cli_is_reported_not_hidden(self):
        self.capability_gap.return_value = dict(liveActions=120, missingInUnity=["scene.compose"], unknownToCli=[])
        result, _, _ = self.run_connect()
        self.assertTrue(result["success"], "能连上就算连接成功，缺的动作单独报告")
        step = next(item for item in result["steps"] if item["step"] == "capabilities")
        self.assertFalse(step["ok"])
        self.assertIn("scene.compose", step["detail"])

    def test_blocking_problem_stops_before_touching_unity(self):
        project = make_project(self.root / "nobridge", bridge=False)
        with patch.object(RevClient, "launch") as launch:
            result = connect.connect(project)
        self.assertFalse(result["success"])
        launch.assert_not_called()


class ShipTests(unittest.TestCase):
    check_report = dict(code=dict(level="None"), container=dict(level="None"), data=dict(level="None"), bundle="tables", autoByFolder=False)

    def invoke_with(self, fail=None, active="StandaloneWindows64", raises=None):
        calls = []

        def invoke(client, action, payload, confirm):
            calls.append((action, payload, confirm))
            if action == raises:
                raise TimeoutError("超时")
            if action == fail:
                return dict(success=False, message="失败原因")
            return dict(success=True, data=dict(target=active, **self.check_report))
        return calls, invoke

    def actions(self, calls):
        return [call[0] for call in calls]

    def test_full_chain_order_with_platform_switch(self):
        calls, invoke = self.invoke_with(active="Android")
        result = ship.run(None, target="StandaloneWindows64", excel="full", ab=True, player="Builds/x/Game.exe", backend="il2cpp", development=True, invoke=invoke)
        self.assertTrue(result["success"])
        self.assertEqual(self.actions(calls), ["editor.wait", "scene.list", "build.settings", "build.target", "editor.wait", "excel.check", "excel.export", "editor.wait", "ab.build", "build.player"])
        player = calls[-1][1]
        self.assertEqual((player["target"], player["backend"], player["development"]), ("StandaloneWindows64", "il2cpp", True))

    def test_platform_already_active_is_not_switched(self):
        calls, invoke = self.invoke_with(active="StandaloneWindows64")
        ship.run(None, target="StandaloneWindows64", invoke=invoke)
        self.assertNotIn("build.target", self.actions(calls))

    def test_data_only_export_does_not_wait_for_compile(self):
        calls, invoke = self.invoke_with()
        ship.run(None, excel="data", ab=False, invoke=invoke)
        self.assertEqual(self.actions(calls), ["editor.wait", "excel.check", "excel.export"])
        self.assertEqual(calls[2][1], dict(dataOnly=True))

    def test_stops_at_first_failure_and_names_the_step(self):
        for failing, later in (("excel.export", "ab.build"), ("ab.build", "build.player"), ("build.target", "excel.export")):
            calls, invoke = self.invoke_with(fail=failing, active="Android")
            result = ship.run(None, target="StandaloneWindows64", excel="full", player="x/G.exe", invoke=invoke)
            self.assertFalse(result["success"], failing)
            self.assertNotIn(later, self.actions(calls), failing)
            self.assertEqual(result["steps"][-1]["action"], failing)
            self.assertIsNotNone(result["failedStep"])

    def test_response_without_success_field_is_a_failure(self):
        result = ship.run(None, ab=True, invoke=lambda *a: dict(message="没有 success 字段"))
        self.assertFalse(result["success"])
        self.assertEqual(len(result["steps"]), 1, "第一步就停")

    def test_timeout_is_a_step_failure_and_is_never_retried(self):
        calls, invoke = self.invoke_with(raises="build.player")
        result = ship.run(None, player="x/G.exe", invoke=invoke)
        self.assertFalse(result["success"])
        self.assertEqual(self.actions(calls).count("build.player"), 1)
        self.assertIn("超时", result["steps"][-1]["message"])

    def test_confirm_force_and_skip_invalid_reach_the_export(self):
        calls, invoke = self.invoke_with()
        ship.run(None, excel="full", ab=False, confirm=True, force=True, skip_invalid=True, invoke=invoke)
        action, payload, confirm = next(call for call in calls if call[0] == "excel.export")
        self.assertEqual(payload, dict(dataOnly=False, force=True, skipInvalid=True))
        self.assertTrue(confirm)

    def test_dirty_scene_stops_before_any_slow_step_when_building_a_player(self):
        calls = []

        def invoke(client, action, payload, confirm):
            calls.append(action)
            return dict(success=True, data=dict(scenes=[dict(path="Assets/A.unity", dirty=True)]))
        result = ship.run(None, excel="full", player="x/G.exe", invoke=invoke)
        self.assertFalse(result["success"])
        self.assertIn("Assets/A.unity", result["message"])
        self.assertEqual(calls, ["editor.wait", "scene.list"])

    def test_dirty_scene_does_not_block_bundles_only(self):
        calls = []

        def invoke(client, action, payload, confirm):
            calls.append(action)
            return dict(success=True, data=dict(scenes=[dict(path="Assets/A.unity", dirty=True)]))
        self.assertTrue(ship.run(None, invoke=invoke)["success"])
        self.assertEqual(calls, ["editor.wait", "ab.build"])

    def test_unmarked_data_folder_stops_before_writing_anything(self):
        self.check_report = dict(self.check_report, bundle="")
        calls, invoke = self.invoke_with()
        result = ship.run(None, excel="full", invoke=invoke)
        self.assertFalse(result["success"])
        self.assertIn("excel.mark", result["message"])
        self.assertNotIn("excel.export", self.actions(calls))
        self.assertEqual(result["failedStep"], "导表前体检")

    def test_unmarked_data_folder_is_fine_when_not_building_bundles(self):
        self.check_report = dict(self.check_report, bundle="")
        calls, invoke = self.invoke_with()
        self.assertTrue(ship.run(None, excel="data", ab=False, invoke=invoke)["success"])

    def test_bad_output_directory_stops_the_chain(self):
        self.check_report = dict(self.check_report, code=dict(level="Error", message="目录不可写"))
        calls, invoke = self.invoke_with()
        result = ship.run(None, excel="full", invoke=invoke)
        self.assertFalse(result["success"])
        self.assertIn("目录不可写", result["message"])
        self.assertNotIn("excel.export", self.actions(calls))

    def test_data_only_export_ignores_code_directory_problems(self):
        self.check_report = dict(self.check_report, code=dict(level="Error", message="目录不可写"))
        calls, invoke = self.invoke_with()
        self.assertTrue(ship.run(None, excel="data", ab=False, invoke=invoke)["success"])

    def test_nothing_to_do_and_bad_mode_are_rejected(self):
        with self.assertRaises(RevError):
            ship.run(None, excel="none", ab=False, invoke=lambda *a: {})
        with self.assertRaises(RevError):
            ship.run(None, excel="all", invoke=lambda *a: {})


class SurfaceTests(unittest.TestCase):
    def parse(self, *argv):
        return cli.parser().parse_args(["--project", "x", *argv])

    def test_connect_and_ship_stay_local_on_both_routing_paths(self):
        for argv in (("connect",), ("ship", "--target", "Android"), ("doctor",)):
            self.assertFalse(daemon.should_forward(self.parse(*argv)), argv)
            self.assertIsNone(fastpath.decide(["--project", "x", *argv]), argv)

    def test_ship_flags(self):
        args = self.parse("ship", "--target", "Android", "--excel", "data", "--no-ab", "--player", "b/x.apk", "--backend", "il2cpp", "--skip-invalid")
        self.assertEqual((args.target, args.excel, args.no_ab, args.backend, args.skip_invalid), ("Android", "data", True, "il2cpp", True))

    def test_query_filters_actions_without_unity(self):
        with patch("builtins.print") as output:
            code = cli.main(["--project", "x", "commands", "--query", "动画"])
        self.assertEqual(code, 0)
        result = json.loads(output.call_args[0][0])
        ids = {item["id"] for item in result["actions"]}
        self.assertTrue({"clip.create", "animator.create"} <= ids)
        self.assertNotIn("ab.build", ids)

    def test_every_family_in_the_manifest_has_a_subcommand(self):
        manifest = client_module.read_json(cli.ROOT / "commands.json")
        for item in manifest["actions"]:
            family, operation = item["id"].split(".", 1)
            self.assertEqual(self.parse(family, *([operation] if not (family == "gm" and operation == "exec") and not (family == "editor" and operation == "menu") and family != "scenario" else [operation, "x"])).operation, operation)

    def test_every_manifest_key_declares_a_type_so_mcp_schema_is_correct(self):
        manifest = client_module.read_json(cli.ROOT / "commands.json")
        missing = sorted({f"{item['id']}.{key}" for item in manifest["actions"] for key in item.get("keys") or [] if key not in manifest["parameterTypes"] and item["id"].split(".")[0] != "scenario" and item["id"] != "batch.call"})
        self.assertEqual(missing, [])

    def test_readonly_flag_is_explicit_on_all_new_actions(self):
        manifest = client_module.read_json(cli.ROOT / "commands.json")
        specs = {item["id"]: item for item in manifest["actions"]}
        for action in ("scene.capture", "excel.preview", "excel.check", "ab.issues", "ab.top-assets", "clip.inspect", "animator.inspect", "ui.snapshot", "bake.status"):
            self.assertTrue(specs[action]["readOnly"], action)
        for action in ("scene.compose", "script.create", "package.add", "lighting.bake", "ab.extract-shared"):
            self.assertFalse(specs[action].get("readOnly", False), action)

    def test_validation_of_new_actions(self):
        cli.validate_action("scene.compose", dict(nodes=[dict(name="A")]))
        cli.validate_action("scene.capture", dict(view="top", width=64, height=64))
        cli.validate_action("script.create", dict(path="Assets/Game/Player.cs"))
        cli.validate_action("script.attach", dict(id="instance:1", script="Assets/Game/Player.cs"))
        for action, payload in (("scene.compose", dict(nodes=[])), ("scene.capture", dict(view="bad")), ("scene.capture", dict(width=1)),
                                ("script.create", dict(path="Other/Player.cs")), ("script.create", dict(path="Assets/Player.txt")),
                                ("script.attach", dict(script="Assets/x.cs")), ("script.attach", dict(id="instance:1"))):
            with self.assertRaises(RevError, msg=(action, payload)):
                cli.validate_action(action, payload)


class StaleDaemonTests(Base):
    def test_stamp_changes_when_code_changes(self):
        first = daemon_link.code_stamp()
        self.assertTrue(any(name == "commands.json" for name, _, _ in first))
        with patch("revautomation.daemon_link.os.stat", side_effect=lambda path: os.stat_result((0, 0, 0, 0, 0, 0, 7, 0, 0, 1))):
            self.assertNotEqual(daemon_link.code_stamp(), first)

    def test_stale_service_refuses_runs_and_caller_falls_back_locally(self):
        import threading
        import time
        from revautomation.daemon_server import RevDaemon
        calls = []
        service = RevDaemon(self.project, idle_timeout=0, call_executor=lambda *_: {}, run_executor=lambda *a: (calls.append(a) or (0, "", "")))
        thread = threading.Thread(target=service.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(lambda: (service.shutdown(), thread.join(5)))
        for _ in range(100):
            if daemon.read_endpoint(self.project):
                break
            time.sleep(.02)
        service.stamp = ("旧代码",)
        self.assertIsNone(daemon.forward(self.project, ["call", "x"], self.temp.name, 5), "返回 None 让调用方本地执行")
        self.assertEqual(calls, [], "旧服务不能执行新命令")
        thread.join(5)
        self.assertFalse(thread.is_alive(), "发现自己过期后要退出")
        self.assertIsNone(daemon.read_endpoint(self.project))


if __name__ == "__main__":
    unittest.main()
