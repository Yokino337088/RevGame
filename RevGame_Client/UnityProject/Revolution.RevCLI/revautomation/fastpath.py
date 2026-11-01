"""零依赖快速转发：在导入 argparse/cli 之前就决定要不要把命令交给常驻服务。

只处理"一眼能确定"的情形；任何不确定（未知选项、缩写、--help、本地专属命令…）一律返回 None，
交给完整的 cli.main。所以这里即使判断保守了，也只是少省一点时间，不会改变命令的语义；
真正的参数解析仍由服务端用同一个 argparse 完成。
"""
from __future__ import annotations

import os
import sys

from . import daemon_link as link
from .errors import RevError

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_DEFAULT_PROJECT = os.path.join(os.path.dirname(_ROOT), "RevolutionFrameWork_Unity")
_VALUE_OPTIONS = frozenset({"--project", "--mode", "--unity", "--timeout", "--output"})
_FLAG_OPTIONS = frozenset({"--no-graphics", "--confirm"})


def decide(argv):
    """返回 (project, timeout) 表示应当尝试转发，返回 None 表示走完整 CLI。"""
    project, mode, timeout = _DEFAULT_PROJECT, "auto", 120.0
    index = 0
    while index < len(argv) and argv[index].startswith("-"):
        name, equals, inline = argv[index].partition("=")
        if name in _FLAG_OPTIONS and not equals:
            index += 1
            continue
        if name not in _VALUE_OPTIONS:
            return None  # --no-daemon、--help、缩写与未知选项都交给完整解析器
        if equals:
            value, index = inline, index + 1
        elif index + 1 < len(argv):
            value, index = argv[index + 1], index + 2
        else:
            return None
        if name == "--project":
            project = value
        elif name == "--mode":
            mode = value
        elif name == "--timeout":
            try:
                timeout = float(value)
            except ValueError:
                return None
    rest = argv[index:]
    if not rest or mode == "batch" or not 0 < timeout < 604800:
        return None
    family, tail = rest[0], rest[1:]
    if family in link.LOCAL_ONLY_FAMILIES or any(token in ("-h", "--help", "--validate") for token in tail):
        return None
    operation = tail[0] if tail and not tail[0].startswith("-") else None
    if (family == "editor" and operation == "launch") or (family == "scenario" and operation == "validate"):
        return None
    if family == "call" and operation == "editor.launch":
        return None
    return project, timeout


def try_run(argv):
    """能转发并拿到结果就返回退出码；没有常驻服务或不适合转发返回 None。"""
    decision = decide(argv)
    if decision is None:
        return None
    project, timeout = decision
    try:
        result = link.forward(project, argv, os.getcwd(), timeout)
    except RevError as exc:
        sys.stderr.write('{"success": false, "error": "RevError", "message": %s}\n' % _quote(str(exc)))
        return 2
    if result is None:
        return None
    sys.stdout.write(result.get("stdout", ""))
    sys.stderr.write(result.get("stderr", ""))
    return int(result["exitCode"])


def _quote(text):
    import json
    return json.dumps(text, ensure_ascii=False)
