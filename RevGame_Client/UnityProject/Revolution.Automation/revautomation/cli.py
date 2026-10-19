"""Human/agent CLI: structured JSON output and stable nonzero failure codes."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys

from .client import RevClient, RevError, atomic_json, find_unity, read_json
from .report import export_report

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PROJECT = ROOT.parent / "RevolutionFrameWork_Unity"


def parser():
    p = argparse.ArgumentParser(description="Revolution 编辑器与 GM 自动化（Python 3.10+，无需 pip）")
    p.add_argument("--project", type=Path, default=DEFAULT_PROJECT)
    p.add_argument("--mode", choices=("auto", "live", "batch"), default="auto")
    p.add_argument("--unity", help="Unity.exe 路径，也可用 UNITY_EDITOR")
    p.add_argument("--timeout", type=float, default=120)
    p.add_argument("--no-graphics", action="store_true", help="batch 禁用图形；不适合渲染性能测量")
    p.add_argument("--output", type=Path, help="同时保存完整 JSON 响应")
    p.add_argument("--confirm", action="store_true", help="显式允许已标记高危 GM、移除旧表或覆盖已生成骨架")
    sub = p.add_subparsers(dest="family", required=True)
    sub.add_parser("doctor", help="检查工程、桥接与编辑器定位")
    sub.add_parser("commands", help="离线列出所有内置 CLI 操作")
    job = sub.add_parser("job", help="离线查看作业进度或请求取消")
    job.add_argument("id")
    job.add_argument("--cancel", action="store_true")
    pipeline = sub.add_parser("pipeline", help="顺序执行多个编辑器/GM/场景动作；失败停止，最后执行 finally")
    pipeline.add_argument("file", type=Path)
    call = sub.add_parser("call", help="调用动作 ID（也支持扩展包注册的动作）")
    call.add_argument("action")
    call.add_argument("--json", type=Path, help="动作参数 JSON 文件")
    call.add_argument("--set", action="append", default=[], metavar="KEY=JSON")
    report = sub.add_parser("report", help="从 scenario 结果生成 JSON/CSV/HTML")
    report.add_argument("result", type=Path)
    report.add_argument("--directory", type=Path, required=True)
    for family, operations in {
        "editor": ("launch", "status", "play", "stop", "refresh", "menus", "menu"),
        "demo": ("ui-prefabs",),
        "ab": ("config", "configure", "scan", "validate", "build", "map", "res-paths", "sound-paths", "check-map", "dependencies", "sizes", "artifacts", "snapshot", "diff", "mark", "mark-source", "rename", "delete", "clean-names", "sync", "overlay", "copy", "mode"),
        "excel": ("settings", "configure", "read", "plan", "export", "mark", "orphans", "clean"),
        "ui": ("scan", "preview", "generate"),
        "platform": ("resources", "linkxml"),
        "hot": ("manifest", "check", "demo-prepare", "demo-clear", "demo-urls", "demo-open"),
        "gm": ("list", "exec"),
        "scenario": ("run", "validate"),
    }.items():
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
            if family == "ab" or family == "hot":
                action.add_argument("--target", help="如 StandaloneWindows64 / Android / WebGL")
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
    for attr, key in (("command", "command"), ("scene", "scene"), ("path", "path"), ("target", "target"),
                      ("source", "sources"), ("prefab", "prefab"), ("class_name", "className"),
                      ("name_space", "nameSpace"), ("output_folder", "outputFolder")):
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


def validate_action(action, payload):
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
        kind = types.get(key)
        valid = (kind != "string" or isinstance(value, str)) and (kind != "bool" or type(value) is bool)
        valid = valid and (kind not in ("array", "string[]") or isinstance(value, list))
        valid = valid and (kind != "string[]" or all(isinstance(item, str) for item in value))
        if not valid:
            raise RevError(f"{key} 类型应为 {kind}。")
    for key, group in (("configJson", action.split(".")[0]), ("userConfigJson", "excelUser")):
        if key not in payload:
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
    spec = read_json(path)
    if not isinstance(spec, dict) or set(spec) - {"name", "steps", "finally"} or not isinstance(spec.get("steps"), list) or not spec["steps"]:
        raise RevError("pipeline 需要非空 steps 数组，可选 name、finally。")
    if not isinstance(spec.get("finally", []), list):
        raise RevError("finally 必须是数组。")
    phases = {phase: [] for phase in ("steps", "finally")}
    # 执行前检查所有步骤，避免先改了一半工程才发现脚本拼错。
    for phase in phases:
        for step in spec.get(phase, []):
            if not isinstance(step, dict) or set(step) - {"action", "payload", "file"}:
                raise RevError("pipeline 步骤只支持 action、payload 或 file。")
            if "file" in step and "payload" in step:
                raise RevError("同一步骤不能同时提供 file 与 payload。")
            payload = read_json(path.parent / step["file"]) if "file" in step else step.get("payload", {})
            action = step.get("action")
            validate_action(action, payload)
            phases[phase].append((action, payload))
    result = dict(success=True, name=spec.get("name", path.stem), steps=[], cleanup=[])
    def invoke(action, payload):
        return invoke_action(client, action, payload, confirm)
    try:
        for action, payload in phases["steps"]:
            response = invoke(action, payload)
            result["steps"].append(dict(action=action, response=response))
            if not response.get("success"):
                result["success"] = False
                break
    except (RevError, OSError, ValueError, TimeoutError) as exc:
        result.update(success=False, error=str(exc))
    finally:
        original_timeout = client.timeout
        client.timeout = min(client.timeout, 120)
        for action, payload in phases["finally"]:
            try:
                response = invoke(action, payload)
                result["cleanup"].append(dict(action=action, response=response))
                result["success"] = result["success"] and response.get("success", False)
            except (RevError, OSError, ValueError, TimeoutError) as exc:
                result["cleanup"].append(dict(action=action, error=str(exc)))
                result["success"] = False
        client.timeout = original_timeout
    return result


def invoke_action(client, action, payload, confirm, report_dir=None):
    response = client.launch() if action == "editor.launch" else client.call(action, payload, confirm,
        lambda update: print(json.dumps(update, ensure_ascii=False), file=sys.stderr))
    if action == "scenario.run" and (response.get("data") or {}).get("resultPath"):
        directory = report_dir or ROOT / "results" / Path(response["jobDirectory"]).name
        response["reports"] = export_report(Path(response["data"]["resultPath"]), directory)
    return response


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        if args.timeout <= 0 or not args.timeout < 604800:
            raise RevError("--timeout 必须大于 0 且小于 604800 秒。")
        if args.family == "commands":
            result = read_json(ROOT / "commands.json")
        elif args.family == "report":
            result = export_report(args.result, args.directory)
        elif args.family == "scenario" and args.operation == "validate":
            payload = payload_of(args)
            result = dict(success=True, name=payload.get("name", "scenario"), message="场景 JSON 格式有效；GM 是否存在及业务结果需在 Play 中检查。")
        else:
            client = RevClient(args.project, args.mode, args.unity, args.timeout, not args.no_graphics)
            if args.family == "doctor":
                try:
                    unity = str(find_unity(client.project, args.unity))
                except RevError as exc:
                    unity = str(exc)
                result = dict(project=str(client.project), endpoint=client.endpoint(), unity=unity,
                              bridgeInstalled=bool(list((client.project / "Assets").rglob("RevAutomationBridge.cs"))))
            elif args.family == "job":
                result = client.job(args.id, args.cancel)
            elif args.family == "pipeline":
                result = run_pipeline(client, args.file, args.confirm)
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
        print(json.dumps(dict(success=False, error=type(exc).__name__, message=str(exc)), ensure_ascii=False), file=sys.stderr)
        return 2
