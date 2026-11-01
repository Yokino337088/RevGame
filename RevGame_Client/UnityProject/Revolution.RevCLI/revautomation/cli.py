"""Human/agent CLI: structured JSON output and stable nonzero failure codes."""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import sys
import time

from .client import RevClient, RevError, atomic_json, read_json
from .media import write_gif
from .report import export_evidence, export_report
from . import daemon
from . import pipeline as pipelines

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PROJECT = ROOT.parent / "RevolutionFrameWork_Unity"
UI_KINDS = ("Canvas", "EventSystem", "Panel", "Image", "RawImage", "Text", "Button", "Toggle", "Slider", "Scrollbar", "Dropdown", "InputField", "ScrollView")


_PARSER = None


def parser():
    """argparse 解析器：构建要遍历整份 commands.json（约 65ms），而它解析时不保存状态，所以进程内只建一次。

    一次性的命令行进程只调用一次，没有任何区别；常驻服务每条命令都会解析（先判定能否转发、再真正执行），
    不缓存就会白白多花两次构建。
    """
    global _PARSER
    if _PARSER is None:
        _PARSER = _build_parser()
    return _PARSER


def _build_parser():
    p = argparse.ArgumentParser(description="Revolution 编辑器与 GM 自动化（Python 3.10+，无需 pip）")
    p.add_argument("--project", type=Path, default=DEFAULT_PROJECT)
    p.add_argument("--mode", choices=("auto", "live", "batch"), default="auto")
    p.add_argument("--unity", help="Unity.exe 路径，也可用 UNITY_EDITOR")
    p.add_argument("--timeout", type=float, default=120)
    p.add_argument("--no-graphics", action="store_true", help="batch 禁用图形；不适合渲染性能测量")
    p.add_argument("--output", type=Path, help="同时保存完整 JSON 响应")
    p.add_argument("--confirm", action="store_true", help="显式允许已标记高危 GM、移除旧表或覆盖已生成骨架")
    p.add_argument("--no-daemon", action="store_true", help="不转发给常驻服务，强制在本进程执行")
    sub = p.add_subparsers(dest="family", required=True)
    sub.add_parser("doctor", help="只读体检：Python、工程、桥接代码、Unity 位置、编辑器、常驻服务、IL2CPP 工具链")
    connecting = sub.add_parser("connect", help="开机后的一键连接：体检、打开 Unity、等编译、起常驻服务、核对能力；可重复执行")
    connecting.add_argument("--no-launch", action="store_true", help="Unity 没开时不自动打开")
    connecting.add_argument("--no-service", action="store_true", help="不启动常驻服务")
    connecting.add_argument("--idle-timeout", type=float, default=1800, help="常驻服务空闲多少秒后退出，0 表示不退出")
    shipping = sub.add_parser("ship", help="一键打整包：切平台 → 导表 → 打 AB → 构建 Player，失败即停")
    shipping.add_argument("--target", help="如 StandaloneWindows64 / Android；不给则用当前平台")
    shipping.add_argument("--excel", choices=("none", "full", "data"), default="none", help="导表方式；full 会生成代码并等待编译")
    shipping.add_argument("--no-ab", action="store_true", help="跳过 AB 打包")
    shipping.add_argument("--player", type=Path, help="Player 输出的完整路径，必须在新建空目录下；不给则不构建 Player")
    shipping.add_argument("--backend", choices=("current", "mono", "il2cpp"), default="current")
    shipping.add_argument("--development", action="store_true")
    shipping.add_argument("--automation", action="store_true", help="带 REV_AUTOMATION 宏，供运行时 GM 与自动化测试")
    shipping.add_argument("--frame-timing", action="store_true")
    shipping.add_argument("--force", action="store_true", help="导表时忽略未改动判断")
    shipping.add_argument("--skip-invalid", action="store_true", help="导表时跳过有错的表")
    service = sub.add_parser("daemon", help="常驻服务：启动一次，之后的调用不再付 Python 启动代价")
    service_ops = service.add_subparsers(dest="operation", required=True)
    for operation in ("start", "stop", "status", "serve"):
        command = service_ops.add_parser(operation)
        if operation in ("start", "serve"):
            command.add_argument("--idle-timeout", type=float, default=1800, help="空闲多少秒后自动退出，0 表示不自动退出")
            command.add_argument("--port", type=int, default=0, help="0 表示随机端口")
    commands = sub.add_parser("commands", help="查询离线命令清单，或当前工程实际安装的能力")
    commands.add_argument("--live", action="store_true", help="从 Unity 获取当前实际注册的能力")
    commands.add_argument("--action", help="只显示指定动作的参数契约")
    commands.add_argument("--query", help="按关键词筛选动作 ID 与说明；不要读 commands.json 全文")
    job = sub.add_parser("job", help="离线查看作业进度或请求取消")
    job.add_argument("id")
    job.add_argument("--cancel", action="store_true")
    pipeline = sub.add_parser("pipeline", help="顺序执行多个编辑器/GM/场景动作；失败停止，最后执行 finally")
    pipeline.add_argument("file", type=Path)
    pipeline.add_argument("--validate", action="store_true", help="只校验流程，不执行 Unity 操作")
    call = sub.add_parser("call", help="调用动作 ID（也支持扩展包注册的动作）")
    call.add_argument("action")
    call.add_argument("--json", type=Path, help="动作参数 JSON 文件")
    call.add_argument("--set", action="append", default=[], metavar="KEY=JSON")
    report = sub.add_parser("report", help="从 scenario 结果生成 JSON/CSV/HTML")
    report.add_argument("result", type=Path)
    report.add_argument("--directory", type=Path, required=True)
    gif = sub.add_parser("gif", help="把多张截图合成 GIF（离线，不需要 Unity）")
    gif.add_argument("frames", nargs="+", type=Path)
    gif.add_argument("--out", type=Path, required=True)
    gif.add_argument("--delay", type=int, default=120, help="每帧停留毫秒")
    gif.add_argument("--max-width", type=int, default=640)
    capture = sub.add_parser("capture", help="连续抓 N 帧并合成 GIF（需要 Unity 在 Play 中）")
    capture.add_argument("--out", type=Path, required=True, help="输出的 .gif 路径")
    capture.add_argument("--frames", type=int, default=12)
    capture.add_argument("--interval", type=int, default=120, help="两帧间隔毫秒（同时作为 GIF 帧间隔）")
    capture.add_argument("--action", default="runtime.screenshot")
    capture.add_argument("--json", type=Path, help="动作参数 JSON")
    capture.add_argument("--max-width", type=int, default=640)
    capture.add_argument("--report", action="store_true", help="同时生成带图/动图的 HTML 报告")
    evidence = sub.add_parser("evidence", help="把结果目录里的截图/动图整理成 HTML 报告")
    evidence.add_argument("--directory", type=Path, required=True)
    evidence.add_argument("--title", default="Revolution 自动化测试报告")
    evidence.add_argument("--subtitle", default="证据与截图")
    evidence.add_argument("--summary", type=Path, help="指标 JSON（可选）")
    evidence.add_argument("--note", action="append", default=[])
    player = sub.add_parser("player", help="独立Windows Player启动/状态/退出")
    player_ops = player.add_subparsers(dest="operation", required=True)
    for operation in ("launch", "status", "stop"):
        command = player_ops.add_parser(operation)
        command.add_argument("--directory", required=True)
        if operation == "launch":
            command.add_argument("--executable", required=True)
            command.add_argument("--port", type=int, default=18766)
            command.add_argument("--arguments", default="[]", help="Player参数JSON字符串数组")
    game = sub.add_parser("game", help="直连游戏运行时（编辑器 Play 或独立 Player）：调 GM、调游戏能力、看状态")
    game_ops = game.add_subparsers(dest="operation", required=True)
    for operation, text in (("status", "运行时服务身份与连通性"), ("actions", "本局注册的游戏能力"), ("gms", "本局注册的 GM 及参数"),
                            ("gm", "执行一条 GM"), ("call", "调用一个游戏能力")):
        command = game_ops.add_parser(operation, help=text)
        command.add_argument("--session", help="独立 Player 的会话目录；不给则用编辑器 Play 的凭据")
        if operation in ("actions", "gms"):
            command.add_argument("--query", help="按名称或说明筛选")
        if operation == "gm":
            command.add_argument("name", help="GM 完整名称，例如 背包/添加道具")
            command.add_argument("values", nargs="*", help="按顺序的参数")
        if operation == "call":
            command.add_argument("name", help="游戏能力 id，例如 ui.click")
            command.add_argument("--json", type=Path, help="参数 JSON 文件")
            command.add_argument("--set", action="append", default=[], metavar="KEY=JSON")
    families = {}
    for spec in read_json(ROOT / "commands.json")["actions"]:
        family, operation = spec["id"].split(".", 1)
        families.setdefault(family, []).append(operation)
    for family, operations in families.items():
        group = sub.add_parser(family)
        ops = group.add_subparsers(dest="operation", required=True)
        for operation in operations:
            action = ops.add_parser(operation)
            action.add_argument("--json", type=Path, help="完整动作参数，见 commands.json")
            action.add_argument("--set", action="append", default=[], metavar="KEY=JSON")
            if family == "gm" and operation == "exec":
                action.add_argument("command", help='完整 GM 文本，例如 "背包/添加道具 1001 2"')
            if family == "scenario":
                action.add_argument("file", type=Path)
                action.add_argument("--repeat", type=int)
                action.add_argument("--warmup", type=int)
                action.add_argument("--report-dir", type=Path)
            if family == "editor" and operation == "play":
                action.add_argument("--scene")
            if family == "editor" and operation == "menu":
                action.add_argument("path", help="菜单全路径，不带快捷键")
            if not (family == "editor" and operation == "menu"):
                action.add_argument("--path")
            for flag in ("id", "query", "type", "name", "destination", "parent", "property", "text", "value", "kind"):
                action.add_argument("--" + flag)
            for flag in ("offset", "limit", "index"):
                action.add_argument("--" + flag, type=int)
            if family in ("ab", "hot", "build", "platform"):
                action.add_argument("--target", help="如 StandaloneWindows64 / Android / WebGL")
            if family == "build" and operation == "player":
                action.add_argument("--backend", choices=("current", "mono", "il2cpp"))
                action.add_argument("--development", action="store_true", default=None)
                action.add_argument("--automation", action="store_true", default=None)
                action.add_argument("--frame-timing", action="store_true", default=None)
            if family == "test":
                action.add_argument("--test-mode", choices=("EditMode", "PlayMode"))
            if family == "script":
                action.add_argument("--script", help="Assets 下的 .cs 路径")
                action.add_argument("--class-name")
                action.add_argument("--namespace", dest="name_space")
                action.add_argument("--base-class")
            if family == "excel":
                action.add_argument("--source", action="append")
                action.add_argument("--data-only", action="store_true")
                action.add_argument("--force", action="store_true")
            if family == "ui":
                action.add_argument("--prefab")
                action.add_argument("--class-name")
                action.add_argument("--namespace", dest="name_space")
                action.add_argument("--output-folder")
    return p


def payload_of(args):
    data = read_json(args.json) if getattr(args, "json", None) else {}
    if not isinstance(data, dict):
        raise RevError("动作参数必须是 JSON 对象。")
    for value in getattr(args, "set", []):
        key, sep, raw = value.partition("=")
        if not sep or not key:
            raise RevError("--set 格式为 KEY=JSON，例如 --set enabled=true")
        try:
            data[key] = json.loads(raw)
        except ValueError as exc:
            raise RevError(f"--set 的值必须是合法 JSON；字符串需要双引号：{key}") from exc
    for attr, key in (("backend", "backend"), ("development", "development"), ("automation", "automation"), ("frame_timing", "frameTiming"), ("command", "command"), ("scene", "scene"), ("path", "path"), ("target", "target"),
                      ("source", "sources"), ("prefab", "prefab"), ("class_name", "className"),
                      ("name_space", "nameSpace"), ("output_folder", "outputFolder"), ("test_mode", "mode"), ("script", "script"), ("base_class", "baseClass"),
                      *((key, key) for key in ("id", "query", "type", "name", "destination", "parent", "property", "text", "value", "kind", "offset", "limit", "index"))):
        value = getattr(args, attr, None)
        if value is not None:
            data[key] = value
    for key, attr in (("dataOnly", "data_only"), ("force", "force")):
        if getattr(args, attr, False):
            data[key] = True
    if args.family == "scenario":
        data = read_json(args.file)
        if not isinstance(data, dict):
            raise RevError("场景脚本必须是 JSON 对象。")
        if args.repeat is not None:
            data["repeat"] = args.repeat
        if args.warmup is not None:
            data["warmup"] = args.warmup
        validate_scenario(data)
    return data


def validate_scenario(data):
    if not isinstance(data, dict):
        raise RevError("场景脚本必须是 JSON 对象。")
    if "name" in data and not isinstance(data["name"], str):
        raise RevError("name 必须是字符串。")
    allowed = {"name", "repeat", "warmup", "setup", "steps", "cleanup", "teardown", "stopOnFailure", "failOnUnityError", "thresholds"}
    if set(data) - allowed:
        raise RevError(f"未知场景字段：{sorted(set(data) - allowed)}")
    for key, minimum in (("repeat", 1), ("warmup", 0)):
        value = data.get(key, minimum)
        if type(value) is not int or value < minimum or value > 1000000:
            raise RevError(f"{key} 必须是 {minimum}..1000000 的整数。")
    if not data.get("steps"):
        raise RevError("场景至少需要一个 steps 步骤。")
    for key in ("stopOnFailure", "failOnUnityError"):
        if key in data and type(data[key]) is not bool:
            raise RevError(f"{key} 必须是布尔值。")
    thresholds = data.get("thresholds", {})
    if not isinstance(thresholds, dict) or set(thresholds) - {"maxFrameP95Ms", "maxFrameMs", "maxMonoUsedBytes"}:
        raise RevError("thresholds 包含未知字段或不是对象。")
    for key, value in thresholds.items():
        if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
            raise RevError(f"{key} 必须是有限非负数。")
        if key == "maxMonoUsedBytes" and (type(value) is not int or value > 9223372036854775807):
            raise RevError("maxMonoUsedBytes 必须是 int64 范围内的非负整数。")
    step_fields = {"kind", "command", "expected", "frames", "seconds", "timeoutSeconds", "intervalSeconds"}
    for phase in ("setup", "steps", "cleanup", "teardown"):
        if not isinstance(data.get(phase, []), list):
            raise RevError(f"{phase} 必须是数组。")
        for step in data.get(phase, []):
            if not isinstance(step, dict) or set(step) - step_fields:
                raise RevError(f"{phase} 存在非法/未知步骤字段。")
            kind = step.get("kind")
            if kind not in ("gm", "wait", "until", "assert"):
                raise RevError(f"未知步骤类型：{kind}")
            if kind != "wait" and not isinstance(step.get("command"), str):
                raise RevError("GM/条件/断言步骤需要 command。")
            if kind in ("assert", "until") and not isinstance(step.get("expected"), str):
                raise RevError("断言与条件步骤需要 expected 字符串（与 GM Message 精确比较）。")
            for key in ("seconds", "timeoutSeconds", "intervalSeconds", "frames"):
                value = step.get(key, 0)
                if type(value) not in (int, float) or not 0 <= value <= 86400:
                    raise RevError(f"{key} 必须是 0..86400 的有限数。")
                if key == "frames" and type(value) is not int:
                    raise RevError("frames 必须是整数。")
            if kind == "until" and step.get("timeoutSeconds", 0) <= 0:
                raise RevError("until 必须配置大于 0 的 timeoutSeconds，防止永久等待。")


def validate_action(action, payload, allow_refs=False):
    if not isinstance(action, str) or not action or not isinstance(payload, dict):
        raise RevError("动作需要非空 action 和 payload 对象。")
    manifest = read_json(ROOT / "commands.json")
    spec = next((item for item in manifest["actions"] if item["id"] == action), None)
    if spec is not None and spec.get("keys") is not None:
        unknown = set(payload) - set(spec["keys"])
        if unknown:
            raise RevError(f"{action} 未知参数：{sorted(unknown)}")
    if action == "scenario.run":
        validate_scenario(payload)
    types = manifest.get("parameterTypes", {})
    for key, value in payload.items():
        if allow_refs and isinstance(value, dict) and "$ref" in value:
            list(pipelines.references(value))
            continue
        kind = types.get(key)
        valid = (kind != "string" or isinstance(value, str)) and (kind != "bool" or type(value) is bool)
        valid = valid and (kind not in ("array", "string[]") or isinstance(value, list))
        valid = valid and (kind != "string[]" or all(isinstance(item, str) for item in value))
        valid = valid and (kind != "int" or type(value) is int)
        if not valid:
            raise RevError(f"{key} 类型应为 {kind}。")
        if key in ("changes", "scenes") and isinstance(value, list):
            allowed = {"property", "value"} if key == "changes" else {"path", "enabled"}
            for item in value:
                if not isinstance(item, dict) or set(item) - allowed:
                    raise RevError(f"{key} 元素只能包含 {sorted(allowed)}。")
                for field, child in item.items():
                    if allow_refs and isinstance(child, dict) and "$ref" in child:
                        list(pipelines.references(child))
                    elif (field == "enabled" and type(child) is not bool) or (field != "enabled" and not isinstance(child, str)):
                        raise RevError(f"{key}.{field} 类型无效。")
                required = {"property", "value"} if key == "changes" else {"path"}
                if required - set(item):
                    raise RevError(f"{key} 元素缺少 {sorted(required - set(item))}。")
    def unresolved(value):
        if allow_refs and isinstance(value, dict) and "$ref" in value:
            list(pipelines.references(value))
            return True
        return False
    if action == "transform.set":
        if not any(key in payload for key in ("position", "eulerAngles", "localScale")):
            raise RevError("transform.set 需要 position、eulerAngles 或 localScale 至少一项。")
        space = payload.get("space", "local")
        if not unresolved(space) and space not in ("local", "world"):
            raise RevError("space 必须为 local 或 world。")
        for key in ("position", "eulerAngles", "localScale"):
            if key not in payload or unresolved(payload[key]):
                continue
            vector = payload[key]
            if not isinstance(vector, list) or len(vector) != 3:
                raise RevError(f"{key} 必须是三个有限数字组成的数组。")
            for coordinate in vector:
                if unresolved(coordinate):
                    continue
                if type(coordinate) not in (int, float) or abs(coordinate) > 3.4028234663852886e38 or not math.isfinite(coordinate):
                    raise RevError(f"{key} 必须为 Single 范围内的有限数字。")
    if action == "gameobject.primitive":
        primitive = payload.get("type")
        if not unresolved(primitive) and primitive not in ("Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"):
            raise RevError("type 必须为 Cube/Sphere/Capsule/Cylinder/Plane/Quad。")
        name = payload.get("name")
        if not unresolved(name) and (not isinstance(name, str) or not name.strip()):
            raise RevError("gameobject.primitive 需要非空 name。")
    if action == "rect.set":
        pair_keys = ("anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta", "offsetMin", "offsetMax")
        if not any(key in payload for key in pair_keys):
            raise RevError("rect.set 需要 anchorMin/anchorMax/pivot/anchoredPosition/sizeDelta/offsetMin/offsetMax 至少一项。")
        if ("offsetMin" in payload or "offsetMax" in payload) and ("anchoredPosition" in payload or "sizeDelta" in payload):
            raise RevError("offsetMin/offsetMax 不能与 anchoredPosition/sizeDelta 同时使用。")
        for key in pair_keys:
            if key not in payload or unresolved(payload[key]):
                continue
            pair = payload[key]
            if not isinstance(pair, list) or len(pair) != 2:
                raise RevError(f"{key} 必须是两个有限数字组成的数组。")
            for coordinate in pair:
                if unresolved(coordinate):
                    continue
                if type(coordinate) not in (int, float) or abs(coordinate) > 1e7 or not math.isfinite(coordinate):
                    raise RevError(f"{key} 必须为绝对值不超过 1e7 的有限数字。")
    if action == "ui.create":
        kind = payload.get("kind")
        if unresolved(kind):
            pass
        elif kind not in UI_KINDS:
            raise RevError("kind 必须为 " + "/".join(UI_KINDS) + "。")
        name = payload.get("name")
        if not unresolved(name) and name is not None and (not isinstance(name, str) or not name.strip()):
            raise RevError("name 不能是空白字符串；不需要自定义名称时省略它。")
        if kind not in ("Canvas", "EventSystem") and not unresolved(kind) and not payload.get("parent"):
            raise RevError("该节点必须提供 parent（某个 Canvas 或其子节点的 id）。")
    if action == "scene.compose":
        nodes = payload.get("nodes")
        if not unresolved(nodes) and (not isinstance(nodes, list) or not nodes):
            raise RevError("scene.compose 需要非空 nodes 数组；节点字段见 commands --action scene.compose。")
    if action == "scene.capture":
        view = payload.get("view", "iso")
        if not unresolved(view) and view not in ("iso", "top", "front", "side", "camera", "sceneview"):
            raise RevError("view 必须为 iso/top/front/side/camera/sceneview。")
        for key in ("width", "height"):
            value = payload.get(key, 16)
            if not unresolved(value) and not 16 <= value <= 4096:
                raise RevError(f"{key} 必须为 16..4096。")
    if action == "script.create":
        path = payload.get("path")
        if not unresolved(path) and (not isinstance(path, str) or not path.startswith("Assets/") or not path.endswith(".cs")):
            raise RevError("script.create 的 path 需要 Assets 下的 .cs 文件。")
    if action == "script.attach" and not any(key in payload for key in ("id", "objectPath")):
        raise RevError("script.attach 需要 id 或 objectPath。")
    if action == "script.attach" and not payload.get("script"):
        raise RevError("script.attach 需要 script（Assets 下的 .cs 路径）。")
    if action == "prefab.unpack":
        unpack = payload.get("unpackMode", "root")
        if not unresolved(unpack) and unpack not in ("root", "completely"):
            raise RevError("unpackMode 必须为 root 或 completely。")
    if action.startswith("material."):
        fields = ("path", "destination") if action == "material.clone" else ("path",)
        for field in fields:
            path = payload.get(field)
            if unresolved(path):
                continue
            write = action in ("material.create", "material.set") or field == "destination"
            roots = ("Assets/",) if write else ("Assets/", "Packages/")
            if not isinstance(path, str) or not path.startswith(roots) or "\\" in path or any(part in (".", "..") for part in path.split("/")) or not path.lower().endswith(".mat"):
                raise RevError(f"{field} 必须是规范的 {'Assets' if write else 'Assets/Packages'} 下 .mat 路径。")
        if action == "material.create" or "shader" in payload:
            shader = payload.get("shader")
            if not unresolved(shader) and (not isinstance(shader, str) or not shader.strip()):
                raise RevError("shader 必须是非空真实 Shader 名称。")
    if action == "shader.list":
        for field, default in (("offset", 0), ("limit", 200)):
            value = payload.get(field, default)
            if not unresolved(value) and (type(value) is not int or value < (0 if field == "offset" else 1) or (field == "limit" and value > 500)):
                raise RevError("Shader 查询 offset >= 0，limit 为 1..500。")
    if action == "material.set":
        properties = payload.get("properties", {})
        if not unresolved(properties):
            if not isinstance(properties, dict) or len(properties) > 512:
                raise RevError("properties 必须是最多 512 项的名称到 typed 值对象。")
            for name, value in properties.items():
                if not isinstance(name, str) or not name.strip() or len(name) > 128:
                    raise RevError("材质属性名必须是 1..128 字符的非空字符串。")
                if unresolved(value):
                    continue
                if isinstance(value, list):
                    if len(value) != 4:
                        raise RevError("Color/Vector 需要四个有限数字。")
                    coordinates = value
                elif type(value) in (int, float):
                    coordinates = [value]
                elif value is None:
                    continue
                elif isinstance(value, str):
                    if not value.startswith(("Assets/", "Packages/")) or "\\" in value or any(part in (".", "..") for part in value.split("/")):
                        raise RevError("纹理引用需要规范的 Assets/Packages 资产路径，清空使用 null。")
                    continue
                else:
                    raise RevError("材质属性值只能是数字、四维数组、纹理资产路径或 null。")
                for coordinate in coordinates:
                    if unresolved(coordinate):
                        continue
                    if type(coordinate) not in (int, float) or abs(coordinate) > 3.4028234663852886e38 or not math.isfinite(coordinate):
                        raise RevError("材质数值必须为 Single 范围内的有限数字。")
        for field in ("enableKeywords", "disableKeywords"):
            names = payload.get(field, [])
            if not unresolved(names) and (not isinstance(names, list) or len(names) > 256 or any(not isinstance(name, str) or not name.strip() or len(name) > 128 for name in names)):
                raise RevError(f"{field} 需要最多 256 个非空关键字，每个最多 128 字符。")
        enable, disable = payload.get("enableKeywords", []), payload.get("disableKeywords", [])
        if not unresolved(enable) and not unresolved(disable) and set(enable) & set(disable):
            raise RevError("同一个关键字不能同时启用和禁用。")
        queue = payload.get("renderQueue", -1)
        if not unresolved(queue) and (type(queue) is not int or not -1 <= queue <= 5000):
            raise RevError("renderQueue 必须是 -1..5000 整数。")
        if not properties and "shader" not in payload and "renderQueue" not in payload and not enable and not disable:
            raise RevError("material.set 需要至少一项修改。")
    for key, group in (("configJson", action.split(".")[0]), ("userConfigJson", "excelUser")):
        if key not in payload:
            continue
        if allow_refs and isinstance(payload[key], dict) and "$ref" in payload[key]:
            continue
        config = json.loads(payload[key])
        fields = manifest.get("configFields", {}).get(group)
        if fields is not None and (not isinstance(config, dict) or set(config) - set(fields)):
            raise RevError(f"{key} 不是配置对象或存在未知字段；字段清单见 commands.json。")
        schema = manifest.get("configParameterTypes", {}).get(group, {})
        for field, value in config.items():
            kind = schema.get(field)
            valid = (kind != "string" or isinstance(value, str)) and (kind != "bool" or type(value) is bool)
            if kind == "string[]":
                valid = isinstance(value, list) and all(isinstance(item, str) for item in value)
            if kind and kind.startswith("enum:"):
                valid = type(value) is int and str(value) in kind[5:].split(",")
            if not valid:
                raise RevError(f"{key}.{field} 的值应为 {kind}。")


def run_pipeline(client, path, confirm):
    return pipelines.run(client, path, confirm, invoke_action, validate_action, read_json(ROOT / "commands.json"))


def capture_frames(client, args):
    """连续抓帧：每次调用截图动作，收集 Unity 返回的绝对路径。

    为什么在客户端循环而不是在 Unity 里一次抓完：
    Unity 的截图动作是"下一帧末写出 PNG"，跨帧等待发生在客户端最稳；
    编辑器重载/卡帧时也不会把一串帧压在同一个作业里，失败能立刻看出是第几帧。
    """
    payload = read_json(args.json) if args.json else {}
    shots = []
    total = max(1, args.frames)
    for index in range(total):
        response = invoke_action(client, args.action, payload, args.confirm)
        if not response.get("success", True):
            raise RevError("第 %d 帧抓取失败：%s" % (index + 1, response.get("message") or response.get("error") or "未知原因"))
        path = (response.get("data") or {}).get("path")
        if not path:
            raise RevError("动作 %s 未返回 data.path，无法合成 GIF" % args.action)
        shots.append(Path(path))
        if index + 1 < total:
            time.sleep(max(0, args.interval) / 1000.0)
    return shots


def print_progress(update):
    print(json.dumps(update, ensure_ascii=False), file=sys.stderr)


def invoke_action(client, action, payload, confirm, report_dir=None, progress=print_progress):
    if action == "editor.launch":
        response = client.launch()
    elif action == "editor.wait":
        response = client.wait_ready()
    else:
        response = client.call(action, payload, confirm, progress)
    if action == "scenario.run" and (response.get("data") or {}).get("resultPath"):
        directory = report_dir or ROOT / "results" / Path(response["jobDirectory"]).name
        response["reports"] = export_report(Path(response["data"]["resultPath"]), directory)
    return response


def main(argv=None):
    raw = list(sys.argv[1:] if argv is None else argv)
    args = parser().parse_args(raw)
    try:
        if args.timeout <= 0 or not args.timeout < 604800:
            raise RevError("--timeout 必须大于 0 且小于 604800 秒。")
        if args.family != "daemon" and daemon.should_forward(args):
            forwarded = daemon.forward(args.project, raw, os.getcwd(), args.timeout)
            if forwarded is not None:
                sys.stdout.write(forwarded.get("stdout", ""))
                sys.stderr.write(forwarded.get("stderr", ""))
                return int(forwarded["exitCode"])
        if args.family == "daemon":
            result = daemon.control(args.operation, args.project, getattr(args, "idle_timeout", 1800.0), getattr(args, "port", 0))
        elif args.family == "connect":
            from . import connect as linking
            # 没有 Library 时要全量导入和编译，实测十几分钟；沿用单次调用的 120 秒会把正常启动判成失败。
            result = linking.connect(args.project, args.unity, max(args.timeout, 1800), args.idle_timeout, not args.no_service, not args.no_launch)
        elif args.family == "doctor":
            from . import connect as linking
            result = linking.inspect(args.project, args.unity, args.timeout)
        elif args.family == "ship":
            from . import ship
            client = RevClient(args.project, "live", args.unity, max(args.timeout, 14400))
            quiet = lambda c, action, payload, confirm: invoke_action(c, action, payload, confirm)
            result = ship.run(client, args.target, args.excel, not args.no_ab, args.player, args.backend, args.development,
                              args.automation, args.frame_timing, args.confirm, args.force, args.skip_invalid, quiet)
        elif args.family == "game":
            from . import game
            payload = None
            if args.operation == "call":
                payload = json.loads(args.json.read_text(encoding="utf-8-sig")) if args.json else {}
                for item in args.set:
                    key, sep, value = item.partition("=")
                    if not sep or not key:
                        raise RevError("--set 需要 KEY=JSON")
                    try:
                        payload[key] = json.loads(value)
                    except ValueError:
                        payload[key] = value
            result = game.run(args.operation, args.project, args.session, args.timeout, args.confirm,
                              getattr(args, "name", None), getattr(args, "values", ()), payload, getattr(args, "query", None))
        elif args.family == "player":
            from . import player
            payload = dict(directory=args.directory)
            if args.operation == "launch":
                payload.update(executable=args.executable, port=args.port, arguments=json.loads(args.arguments), timeout=min(args.timeout, 120))
            result = player.execute("player." + args.operation, payload)
        elif args.family == "commands":
            if args.live:
                result = RevClient(args.project, "live", args.unity, args.timeout).call("editor.capabilities")
                if result.get("success"):
                    result = result["data"]
            else:
                result = read_json(ROOT / "commands.json")
            if args.action:
                found = [item for item in result.get("actions", []) if item["id"] == args.action]
                if not found:
                    raise RevError(f"动作未找到：{args.action}")
                result = dict(success=True, actions=found)
            elif args.query:
                needle = args.query.lower()
                found = [item for item in result.get("actions", []) if needle in item["id"].lower() or needle in (item.get("description") or "").lower()]
                result = dict(success=True, total=len(found), actions=found)
        elif args.family == "report":
            result = export_report(args.result, args.directory)
        elif args.family == "gif":
            result = dict(success=True, **write_gif(args.frames, args.out, args.delay, args.max_width))
        elif args.family == "evidence":
            summary = read_json(args.summary) if args.summary else None
            result = dict(success=True, **export_evidence(
                args.directory, args.title, args.subtitle, summary, args.note))
        elif args.family == "capture" and args.mode == "batch":
            raise RevError("capture 需要 Play 中的在线编辑器：先 editor launch，再 editor play。")
        elif args.family == "scenario" and args.operation == "validate":
            payload = payload_of(args)
            result = dict(success=True, name=payload.get("name", "scenario"), message="场景 JSON 格式有效；GM 是否存在及业务结果需在 Play 中检查。")
        elif args.family == "pipeline" and args.validate:
            result = pipelines.run(None, args.file, args.confirm, invoke_action, validate_action, read_json(ROOT / "commands.json"), validate_only=True)
        else:
            client = RevClient(args.project, args.mode, args.unity, args.timeout, not args.no_graphics)
            if args.family == "job":
                result = client.job(args.id, args.cancel)
            elif args.family == "capture":
                frames = capture_frames(client, args)
                info = write_gif(frames, args.out, args.interval, args.max_width)
                result = dict(success=True, **info)
                if args.report:
                    result["report"] = export_evidence(
                        args.out.parent, "自动化测试报告",
                        "连续 %d 帧 · 间隔 %d ms" % (len(frames), args.interval),
                        {"帧数": len(frames), "帧间隔毫秒": args.interval, "GIF": args.out.name})
            elif args.family == "pipeline":
                result = pipelines.run(client, args.file, args.confirm, invoke_action, validate_action, read_json(ROOT / "commands.json"), output=args.output)
            else:
                action = args.action if args.family == "call" else args.family + "." + args.operation
                payload = payload_of(args)
                if action == "scenario.run" and args.mode == "batch":
                    raise RevError("scenario.run 需要 Play 中的在线编辑器。先 editor launch，再 editor play。")
                validate_action(action, payload)
                result = invoke_action(client, action, payload, args.confirm, getattr(args, "report_dir", None))
        if args.output:
            atomic_json(args.output, result)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if result.get("success", True) else 1
    except KeyboardInterrupt:
        print(json.dumps(dict(success=False, error="Cancelled", message="已请求取消；Unity 清理进度请看 jobDirectory。"), ensure_ascii=False), file=sys.stderr)
        return 130
    except (RevError, OSError, ValueError, TimeoutError) as exc:
        result = dict(success=False, error=type(exc).__name__, message=str(exc))
        if args.output:
            atomic_json(args.output, result)
        print(json.dumps(result, ensure_ascii=False), file=sys.stderr)
        return 2
