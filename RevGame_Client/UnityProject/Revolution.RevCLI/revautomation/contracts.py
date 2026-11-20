"""统一离线清单、在线 Unity 类型契约和工具校验回执。"""
import hashlib
import json


# 旧 scenario 与批处理的请求由各自解释器校验，清单接口提供相同的发现类型。
COMPAT_TYPES = {
    "repeat": "int", "warmup": "int", "setup": "array", "steps": "array",
    "cleanup": "array", "teardown": "array", "stopOnFailure": "bool",
    "failOnUnityError": "bool", "thresholds": "object", "stopOnError": "bool",
    "validateOnly": "bool",
}
# 桥接支持的清单主版本，旧文件未声明时沿用第一版。
CONTRACT_VERSION = 1


def fingerprint(value):
    """按键排序编码，便于校验两次交付是否使用确切相同的契约。"""
    payload = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def merge_editor_action(registered, offline=None):
    """在线注册决定类型和可用性，预存清单补足旧动作元数据。"""
    previous = offline or {}
    action = dict(previous, **registered)
    # 类型化注册的权限属于当前 Unity 动作。只有旧注册才能用离线清单补充只读标记。
    typed = bool(registered.get("inputSchemaJson") or registered.get("inputSchema"))
    action["readOnly"] = bool(registered.get("readOnly") if typed else registered.get("readOnly") or previous.get("readOnly"))
    for source, target in (("inputSchemaJson", "inputSchema"), ("outputSchemaJson", "outputSchema")):
        value = action.pop(source, None)
        if value:
            schema = json.loads(value)
            if not isinstance(schema, dict) or schema.get("type") != "object":
                raise ValueError("在线动作 Schema 无效：" + action["id"])
            action[target] = schema
    return action


def audit(manifest, editor=None, status=None):
    """核对清单字段与真实桥接；缺少可选包单列，已注册同名参数键漂移会失败。"""
    version = manifest.get("contractVersion", CONTRACT_VERSION)
    errors = []
    if type(version) is not int or version != CONTRACT_VERSION:
        errors.append("命令清单主版本与当前工具不兼容")
    known = {}
    types = manifest.get("parameterTypes", {})
    for action in manifest.get("actions", []):
        action_id = action["id"]
        if action_id in known:
            errors.append("动作重复：" + action_id)
        known[action_id] = action
        for key in action.get("keys") or ():
            if not types.get(key) and not COMPAT_TYPES.get(key) and key not in action.get("inputSchema", {}).get("properties", {}):
                errors.append("参数缺少类型：" + action_id + "." + key)
    live = None
    if editor is not None:
        if editor.get("protocol") != 1:
            errors.append("Unity 桥接协议版本不兼容")
        errors.extend(editor.get("errors") or ())
        live = {}
        for registered in editor.get("actions", []):
            action_id = registered["id"]
            if action_id in live:
                errors.append("在线动作重复：" + action_id)
            expected = known.get(action_id, {})
            live[action_id] = merge_editor_action(registered, expected)
            if expected and set(expected.get("keys") or ()) != set(registered.get("keys") or ()):
                errors.append("线上线下参数键漂移：" + action_id)
    return {
        "success": not errors, "contractVersion": version, "errors": errors,
        "manifestHash": fingerprint(manifest), "onlineHash": None if live is None else fingerprint(live),
        "project": (status or {}).get("project"), "unityVersion": (status or {}).get("unityVersion"),
        "editorVersion": (editor or {}).get("version"),
        "onlineActions": [] if live is None else [live[key] for key in sorted(live)],
        "offlineOnly": [] if live is None else sorted(known.keys() - live.keys()),
    }
