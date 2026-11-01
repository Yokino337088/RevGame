"""一键打整包：切平台 → 导表 → 打 AB → 构建 Player，每步失败即停并返回分步报告。

为什么用 Python 编排而不是放进 Unity：切平台和导表都会触发域重载，Unity 内的流程会被打断；
而 RevClient 本来就能跨重载等待，所以由调用方串起来最稳。

这里故意不做失败重试：构建类动作超时不代表没有执行，自动重放可能覆盖产物或重复写资源。
"""
from __future__ import annotations

import time

from .errors import RevError

EXCEL_MODES = ("none", "full", "data")


def run(client, target=None, excel="none", ab=True, player=None, backend="current", development=False,
        automation=False, frame_timing=False, confirm=False, force=False, skip_invalid=False, invoke=None) -> dict:
    if excel not in EXCEL_MODES:
        raise RevError("excel 必须为 none/full/data。")
    if excel == "none" and not ab and not player:
        raise RevError("至少需要导表、打 AB、构建 Player 中的一步。")
    steps = []

    def step(name, action, payload=None, needs_confirm=False):
        started = time.monotonic()
        try:
            response = invoke(client, action, payload or {}, needs_confirm)
            # 缺 success 字段不能当成功：宁可误报失败，不能让后面的步骤建立在没确认的结果上。
            ok = isinstance(response, dict) and response.get("success") is True
            message = response.get("message", "")
        except (RevError, OSError, ValueError, TimeoutError) as exc:
            response, ok, message = None, False, f"{type(exc).__name__}: {exc}"
        steps.append(dict(step=name, action=action, success=bool(ok), seconds=round(time.monotonic() - started, 1), message=message,
                          data=(response or {}).get("data"), jobDirectory=(response or {}).get("jobDirectory")))
        return bool(ok), response

    def finish(success, message):
        return dict(success=success, message=message, target=target, steps=steps, failedStep=next((s["step"] for s in steps if not s["success"]), None))

    ok, _ = step("等待编辑器就绪", "editor.wait")
    if not ok:
        return finish(False, "编辑器未就绪，未执行任何构建步骤")
    if player:
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
    if ab:
        ok, _ = step("打 AB 包", "ab.build", dict(target=target) if target else {})
        if not ok:
            return finish(False, "AB 打包失败，未构建 Player")
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
