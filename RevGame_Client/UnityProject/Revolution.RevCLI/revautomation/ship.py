"""一键打整包：切平台 → 导表 → 打 AB → 构建 Player，每步失败即停并返回分步报告。

为什么用 Python 编排而不是放进 Unity：切平台和导表都会触发域重载，Unity 内的流程会被打断；
而 RevClient 本来就能跨重载等待，所以由调用方串起来最稳。

这里故意不做失败重试：构建类动作超时不代表没有执行，自动重放可能覆盖产物或重复写资源。
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
import uuid

from .errors import RevError

EXCEL_MODES = ("none", "full", "data")


def smoke_runner(entry, project, plan, timeout):
    """冻结业务验收计划，经标准输入调用同批 RevMCP。计划需声明结束后的恢复动作。"""
    if not entry.is_file():
        raise RevError("找不到相邻的 Revolution.RevMCP。")
    if not isinstance(plan, dict) or not plan.get("steps") or not plan.get("finally"):
        raise RevError("交付验收计划需要 steps 和 finally。")
    serialized = json.dumps(plan, ensure_ascii=False, allow_nan=False).encode("utf-8")

    def run_plan():
        """执行一次并保留断言及清理结果，工具失败不会自动重放。"""
        # 出包整体预算可超过 RevMCP 的单步上限，分别传递总预算与单次工单预算。
        command = [sys.executable, str(entry), "--project", str(project), "--timeout", str(min(timeout, 3600)), "--full", "run", "-"]
        try:
            completed = subprocess.run(command, input=serialized, capture_output=True, timeout=timeout)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RevError("业务验收回执不可用：" + str(error)) from error
        try:
            result = json.loads(completed.stdout)
        except ValueError as error:
            stderr = completed.stderr.decode("utf-8", errors="replace").strip().splitlines()
            reason = stderr[-1][:400] if stderr else str(error)
            raise RevError(f"业务验收回执不可用（退出码 {completed.returncode}）：{reason}") from error
        if not isinstance(result, dict):
            raise RevError("业务验收未返回对象回执。")
        return {"success": completed.returncode == 0 and result.get("success") is True, "data": result}

    return run_plan


def run(client, target=None, excel="none", ab=True, player=None, backend="current", development=False,
        automation=False, frame_timing=False, confirm=False, force=False, skip_invalid=False, invoke=None,
        verified=False, smoke=None) -> dict:
    """执行常规出包；verified 使用本次完整编译和同平台 AB 回执，smoke 为业务真实验收。"""
    if excel not in EXCEL_MODES:
        raise RevError("excel 必须为 none/full/data。")
    if excel == "none" and not ab and not player and smoke is None:
        raise RevError("至少需要导表、打 AB、构建 Player 中的一步。")
    if verified and not target:
        raise RevError("校验交付需要显式平台。")
    if verified and skip_invalid:
        raise RevError("校验交付需要完整导表。")
    build_id = uuid.uuid4().hex if verified or smoke else None
    steps = []

    def step(name, action, payload=None, needs_confirm=False):
        """记录动作接单与结果。状态未知的失败原样保留，调用方可查同一工单。"""
        started = time.monotonic()
        try:
            response = smoke() if action == "rev_run" else invoke(client, action, payload or {}, needs_confirm)
            # 缺 success 字段不能当成功：宁可误报失败，不能让后面的步骤建立在没确认的结果上。
            ok = isinstance(response, dict) and response.get("success") is True
            message = response.get("message", "")
        except (RevError, OSError, ValueError, TimeoutError) as exc:
            response, ok, message = None, False, f"{type(exc).__name__}: {exc}"
        steps.append(dict(step=name, action=action, success=bool(ok), seconds=round(time.monotonic() - started, 1), message=message,
                          data=(response or {}).get("data"), jobDirectory=(response or {}).get("jobDirectory"),
                          requestId=(response or {}).get("id"), buildId=build_id))
        return bool(ok), response

    def finish(success, message):
        """返回完整分步回执以定位失败，构建身份贯穿本轮各阶段。"""
        return dict(success=success, message=message, target=target, steps=steps, buildId=build_id,
                    failedStep=next((s["step"] for s in steps if not s["success"]), None))

    ok, _ = step("等待编辑器就绪", "editor.wait")
    if not ok:
        return finish(False, "编辑器未就绪，未执行任何构建步骤")
    if player or verified:
        # 放在最前面：Player 构建会拒绝未保存场景。不提前查的话，要等导表和 AB 跑完几分钟才失败。
        ok, opened = step("检查场景已保存", "scene.list")
        if not ok:
            return finish(False, "读取场景状态失败")
        dirty = [scene.get("path") or scene.get("name") for scene in ((opened or {}).get("data") or {}).get("scenes", []) if scene.get("dirty")]
        if dirty:
            steps[-1]["success"] = False
            return finish(False, "存在未保存场景，先 scene.save：" + ", ".join(dirty))
    if target:
        ok, settings = step("读取当前平台", "build.settings")
        if not ok:
            return finish(False, "读取构建设置失败")
        active = ((settings or {}).get("data") or {}).get("target")
        if active != target:
            ok, _ = step("切换平台", "build.target", dict(target=target))
            if not ok:
                return finish(False, "平台切换失败；确认已安装目标平台支持（build targets）")
            ok, _ = step("等待平台切换完成", "editor.wait")
            if not ok:
                return finish(False, "平台切换后编辑器未就绪")
    if excel != "none":
        # 先体检再导出：等导出到一半才失败，数据文件已经写了，工程停在半截状态。
        ok, checked = step("导表前体检", "excel.check")
        if not ok:
            return finish(False, "导表体检失败")
        report = (checked or {}).get("data") or {}
        for key in (("data",) if excel == "data" else ("code", "container", "data")):
            if (report.get(key) or {}).get("level") == "Error":
                steps[-1]["success"] = False
                return finish(False, f"导表输出目录有问题（{key}）：{report[key].get('message')}")
        if ab and not report.get("bundle") and not report.get("autoByFolder"):
            # 数据目录不在任何包里，运行时读不到表，AB 校验也会拦下。
            steps[-1]["success"] = False
            return finish(False, "数据目录没有 AB 标记：先 excel.mark（参数 bundle）标记后再打包")
        payload = dict(dataOnly=excel == "data")
        if force:
            payload["force"] = True
        if skip_invalid:
            payload["skipInvalid"] = True
        ok, _ = step("导表", "excel.export", payload, confirm)
        if not ok:
            return finish(False, "导表失败，后续步骤未执行")
        if excel == "full":
            # 全量导出会生成代码并触发编译，不等完 AB 和 Player 用的还是旧程序集。
            ok, _ = step("等待导表后的编译", "editor.wait")
            if not ok:
                return finish(False, "导表生成的代码没有编译通过，后续步骤未执行")
    if verified:
        # 只等到编辑器空闲不能证明刚导入的脚本编译过，必须匹配本次主动编译的工单。
        for name, action in (("刷新导入", "editor.refresh"), ("等待导入", "editor.wait")):
            ok, _ = step(name, action)
            if not ok:
                return finish(False, name + "失败")
        ok, request = step("请求完整重编译", "editor.recompile")
        if not ok or not request.get("id"):
            steps[-1]["success"] = False
            return finish(False, "未取得完整重编译工单")
        ok, _ = step("等待本次重编译", "editor.wait")
        if not ok:
            return finish(False, "重编译未就绪")
        ok, response = step("校验本次编译诊断", "editor.compilation")
        diagnostic = (response or {}).get("data") or {}
        if not ok or diagnostic.get("requestId") != request["id"] or diagnostic.get("finished") is not True or diagnostic.get("failed") is not False:
            steps[-1]["success"] = False
            return finish(False, "编译诊断与工单不符或编译失败")
    if ab and verified:
        payload = dict(target=target)
        for name, action in (("校验平台资源", "ab.validate"), ("增量构建 AB", "ab.build-incremental"),
                             ("检查映射", "ab.check-map"), ("增量发布 AB", "ab.publish-incremental")):
            ok, response = step(name, action, payload)
            data = (response or {}).get("data") or {}
            if action == "ab.build-incremental":
                ok = ok and data.get("target") == target and data.get("totalBundles", -1) >= 0
            if action == "ab.publish-incremental":
                ok = ok and data.get("target") == target and data.get("published") is True
            if not ok:
                steps[-1]["success"] = False
                return finish(False, name + "失败或回执不符")
    elif ab:
        ok, _ = step("打 AB 包", "ab.build", dict(target=target) if target else {})
        if not ok:
            return finish(False, "AB 打包失败，未构建 Player")
    if smoke is not None:
        ok, status = step("确认编辑器可冷启动", "editor.status")
        if not ok or ((status or {}).get("data") or {}).get("playing") is not False:
            steps[-1]["success"] = False
            return finish(False, "需要编辑态才能启动独立业务验收")
        launched, _ = step("启动业务验收 Play", "editor.play")
        if not launched:
            return finish(False, "无法确认已进入验收 Play")
        # RevMCP 在 Play 中预检真实业务注册表。无论断言是否通过都关闭本次开出的 Play。
        passed = False
        stopped = False
        try:
            passed, _ = step("运行显式真实验收计划", "rev_run")
        finally:
            stopped, _ = step("关闭本次验收 Play", "editor.stop")
        if not passed or not stopped:
            return finish(False, "业务验收或清理失败")
    if player:
        payload = dict(path=str(player), backend=backend)
        if target:
            payload["target"] = target
        for key, value in (("development", development), ("automation", automation), ("frameTiming", frame_timing)):
            if value:
                payload[key] = True
        ok, _ = step("构建 Player", "build.player", payload)
        if not ok:
            return finish(False, "Player 构建失败；不自动重放，先看 BuildReport")
    return finish(True, "全部步骤完成")
