"""编辑器流程编排：保留类型的结果引用、精确 JSON 断言和有期限的只读轮询。

pipeline 控制 Editor 动作；跨 Editor/GM/Player/外部工具的计划使用 RevMCP。
两者都先检查完整流程再执行，失败进入清理，不通过重试变更来掩盖失败。
"""
from __future__ import annotations

import math
import time
from pathlib import Path

from .client import RevError, atomic_json, read_json


def pointer(document, path):
    if not isinstance(path, str) or (path and not path.startswith("/")):
        raise RevError("JSON Pointer 必须为空或以 / 开头。")
    value = document
    for part in path.split("/")[1:] if path else []:
        key = part.replace("~1", "/").replace("~0", "~")
        try:
            if isinstance(value, list):
                if not key.isdecimal() or (len(key) > 1 and key.startswith("0")):
                    raise KeyError(key)
                value = value[int(key)]
            elif isinstance(value, dict):
                value = value[key]
            else:
                raise KeyError(key)
        except (KeyError, IndexError):
            raise RevError(f"JSON Pointer 不存在：{path}") from None
    return value


def references(value):
    if isinstance(value, dict):
        if "$ref" in value:
            if set(value) != {"$ref"} or not isinstance(value["$ref"], str):
                raise RevError("引用必须是单独的 {\"$ref\":\"步骤ID#/JSON/Pointer\"} 对象。")
            step, sep, path = value["$ref"].partition("#")
            if not sep or not step or (path and not path.startswith("/")):
                raise RevError("引用格式为 步骤ID#/JSON/Pointer。")
            yield step, path
        else:
            for child in value.values():
                yield from references(child)
    elif isinstance(value, list):
        for child in value:
            yield from references(child)


def resolve(value, responses):
    if isinstance(value, dict):
        if "$ref" in value:
            step, path = next(references(value))
            if step not in responses:
                raise RevError(f"引用的步骤尚未产生结果：{step}")
            return pointer(responses[step], path)
        return {key: resolve(child, responses) for key, child in value.items()}
    if isinstance(value, list):
        return [resolve(child, responses) for child in value]
    return value


def validate_condition(condition):
    if not isinstance(condition, dict) or set(condition) - {"path", "equals", "exists"}:
        raise RevError("断言仅支持 path 与 equals 或 exists。")
    path = condition.get("path")
    if not isinstance(path, str) or (path and not path.startswith("/")):
        raise RevError("断言 path 需要 JSON Pointer。")
    if ("equals" in condition) == ("exists" in condition):
        raise RevError("断言必须且只能指定 equals 或 exists。")
    if "exists" in condition and type(condition["exists"]) is not bool:
        raise RevError("exists 必须是布尔值。")


def matches(document, condition):
    try:
        actual = pointer(document, condition["path"])
    except RevError:
        return condition.get("exists") is False
    if "exists" in condition:
        return condition["exists"]
    expected = condition["equals"]
    # JSON booleans and numbers are different types, despite Python True == 1.
    return (type(actual) is bool) == (type(expected) is bool) and actual == expected


def prepare(path, validate_action, manifest):
    path = Path(path)
    spec = read_json(path)
    if not isinstance(spec, dict) or set(spec) - {"name", "steps", "finally"} or not isinstance(spec.get("steps"), list) or not spec["steps"]:
        raise RevError("pipeline 需要非空 steps 数组，可选 name、finally。")
    if not isinstance(spec.get("finally", []), list):
        raise RevError("finally 必须是数组。")
    phases = {"steps": [], "finally": []}
    ids = set()
    known = {item["id"]: item for item in manifest["actions"]}
    for phase in phases:
        for step in spec.get(phase, []):
            if not isinstance(step, dict) or set(step) - {"id", "action", "payload", "file", "expect", "until"}:
                raise RevError("pipeline 步骤只支持 id/action/payload/file/expect/until。")
            if "file" in step and "payload" in step:
                raise RevError("同一步骤不能同时提供 file 与 payload。")
            if "file" in step and not isinstance(step["file"], str):
                raise RevError("file 必须是文件路径。")
            payload = read_json(path.parent / step["file"]) if "file" in step else step.get("payload", {})
            action = step.get("action")
            conditions = step.get("expect", [])
            if not isinstance(conditions, list):
                raise RevError("expect 必须是数组。")
            for condition in conditions:
                validate_condition(condition)
            until = step.get("until")
            if until is not None:
                if not isinstance(until, dict) or set(until) - {"path", "equals", "exists", "timeoutSeconds", "intervalSeconds"}:
                    raise RevError("until 字段无效。")
                validate_condition({k: v for k, v in until.items() if k not in ("timeoutSeconds", "intervalSeconds")})
                for key, default in (("timeoutSeconds", 30), ("intervalSeconds", .2)):
                    value = until.get(key, default)
                    if type(value) not in (int, float) or not math.isfinite(value) or not 0 < value <= 86400:
                        raise RevError(f"until.{key} 需要 0..86400 的有限正数。")
                if not known.get(action, {}).get("readOnly"):
                    raise RevError("until 只能轮询 commands.json 中声明 readOnly 的查询动作；GM 条件请使用 scenario。")
            for source, _ in references([payload, conditions, until]):
                if source not in ids:
                    raise RevError(f"步骤引用必须指向之前的步骤：{source}")
            validate_action(action, payload, allow_refs=True)
            step_id = step.get("id")
            if step_id is not None:
                if not isinstance(step_id, str) or not step_id or "#" in step_id or step_id in ids:
                    raise RevError("步骤 id 必须非空、不含 # 且唯一。")
                ids.add(step_id)
            phases[phase].append(dict(action=action, payload=payload, id=step_id, expect=conditions, until=until))
    return spec, phases


def run(client, path, confirm, invoke, validate_action, manifest, output=None, validate_only=False):
    spec, phases = prepare(path, validate_action, manifest)
    if validate_only:
        return dict(success=True, message="pipeline 格式、参数、引用与断言有效；未执行 Unity 操作。")
    result = dict(success=True, name=spec.get("name", Path(path).stem), steps=[], cleanup=[])
    responses = {}

    def save():
        if output:
            atomic_json(output, result)

    def execute(step):
        payload = resolve(step["payload"], responses)
        validate_action(step["action"], payload)
        until = resolve(step["until"], responses)
        start = time.monotonic()
        deadline = start + (until.get("timeoutSeconds", 30) if until else client.timeout)
        original = client.timeout
        polls = 0
        try:
            while True:
                if until:
                    client.timeout = max(.01, min(original, deadline - time.monotonic()))
                response = invoke(client, step["action"], payload, confirm)
                polls += 1
                if step["id"]:
                    responses[step["id"]] = response
                if not response.get("success") or not until or matches(response, until):
                    break
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    response = dict(response, success=False, error="ConditionTimeout", message="until 条件在期限内未满足")
                    break
                time.sleep(min(until.get("intervalSeconds", .2), remaining))
            for condition in resolve(step["expect"], responses):
                if not matches(response, condition):
                    response = dict(response, success=False, error="AssertionFailed", message=f"结构化断言失败：{condition}")
                    break
            if step["id"]:
                responses[step["id"]] = response
            return dict(id=step["id"], action=step["action"], response=response, polls=polls, seconds=time.monotonic() - start)
        finally:
            client.timeout = original

    try:
        for step in phases["steps"]:
            row = execute(step)
            result["steps"].append(row)
            result["success"] = bool(row["response"].get("success"))
            save()
            if not result["success"]:
                break
    except (RevError, OSError, ValueError, TimeoutError) as exc:
        result.update(success=False, error=str(exc))
    except KeyboardInterrupt:
        result.update(success=False, cancelled=True, error="Cancelled")
    finally:
        original = client.timeout
        client.timeout = min(original, 120)
        for step in phases["finally"]:
            try:
                row = execute(step)
                result["cleanup"].append(row)
                if not row["response"].get("success"):
                    result["success"] = False
            except (RevError, OSError, ValueError, TimeoutError, KeyboardInterrupt) as exc:
                result["cleanup"].append(dict(action=step["action"], error=str(exc)))
                result["success"] = False
            save()
        client.timeout = original
        save()
    return result
