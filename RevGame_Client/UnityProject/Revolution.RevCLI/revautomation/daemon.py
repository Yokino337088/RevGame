"""常驻服务的管理面：start / stop / status，以及"这条命令能不能转发"的判定。

传输细节在 daemon_link.py（刻意保持轻量），服务端实现在 daemon_server.py，只有 `daemon serve` 才会加载。
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import time

from .errors import RevError
from . import daemon_link as link
from .daemon_link import (  # noqa: F401  对外保持原来的名字
    ENDPOINT_NAME, HEADERS_NAME, LOCAL_ONLY_FAMILIES, LOG_NAME, MAX_BODY, PROTOCOL,
    DaemonUnavailable, discard, forward, read_endpoint, request)


def daemon_dir(project) -> Path:
    return Path(link.daemon_dir(project))


def should_forward(args) -> bool:
    """完整解析后的判定。快速路径（fastpath.py）按原始参数做同样的判断，两者必须一致，测试里有对照表。"""
    if getattr(args, "no_daemon", False) or args.mode == "batch" or args.family in LOCAL_ONLY_FAMILIES:
        return False
    operation = getattr(args, "operation", None)
    if args.family == "pipeline" and args.validate:
        return False
    if args.family == "scenario" and operation == "validate":
        return False
    # 拉起 Unity 进程的动作留在调用方：常驻服务不该成为编辑器的父进程。
    if (args.family == "editor" and operation == "launch") or (args.family == "call" and args.action == "editor.launch"):
        return False
    return True


def status(project) -> dict:
    info = read_endpoint(project)
    root = daemon_dir(project)
    if info is None:
        return dict(success=True, running=False)
    try:
        code, body = request(info, "GET", "/health")
    except DaemonUnavailable:
        discard(project, info)
        return dict(success=True, running=False, staleCleaned=True)
    except (OSError, ValueError) as exc:
        return dict(success=False, running=False, message="常驻服务响应异常：" + str(exc))
    if code != 200:
        return dict(success=False, running=False, message=f"常驻服务返回 HTTP {code}：{body.get('message') if isinstance(body, dict) else ''}")
    url = f"http://127.0.0.1:{info['port']}"
    return dict(success=True, running=True, url=url, endpointFile=str(root / ENDPOINT_NAME),
                headerFile=str(root / HEADERS_NAME), **{k: v for k, v in body.items() if k != "success"})


def start(project, idle_timeout=1800.0, port=0, wait_seconds=15.0) -> dict:
    project = Path(project).resolve()
    if not (project / "Assets").is_dir() or not (project / "ProjectSettings").is_dir():
        raise RevError(f"不是 Unity 工程：{project}")
    current = status(project)
    if current.get("running") and not current.get("stale"):
        return dict(current, alreadyRunning=True)
    if current.get("running"):
        # 代码更新后还活着的旧服务：复用它的话，connect 报告就绪，下一条命令它却会自行退出。
        stop(project)
    root = daemon_dir(project)
    root.mkdir(parents=True, exist_ok=True)
    log = open(root / LOG_NAME, "wb")
    command = [sys.executable, str(Path(__file__).resolve().parents[1] / "rev.py"), "--project", str(project),
               "daemon", "serve", "--idle-timeout", str(idle_timeout), "--port", str(port)]
    kwargs = {}
    if os.name == "nt":
        # 脱离当前控制台与进程组：调用方的 shell 结束后服务仍应存活。
        kwargs["creationflags"] = 0x00000008 | 0x00000200 | 0x08000000
    else:
        kwargs["start_new_session"] = True
    try:
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=log, close_fds=True, **kwargs)
    finally:
        log.close()
    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        result = status(project)
        if result.get("running") and result.get("pid") == process.pid:
            return dict(result, alreadyRunning=False)
        if process.poll() is not None:
            break
        time.sleep(.1)
    tail = ""
    try:
        tail = (root / LOG_NAME).read_text(encoding="utf-8", errors="replace")[-800:]
    except OSError:
        pass
    raise RevError(f"常驻服务未能在 {wait_seconds:.0f}s 内就绪（退出码 {process.poll()}）。日志：{tail.strip() or '空'}")


def stop(project, wait_seconds=5.0) -> dict:
    info = read_endpoint(project)
    if info is None:
        return dict(success=True, running=False, message="常驻服务没有运行。")
    try:
        request(info, "POST", "/shutdown", {}, timeout=5.0)
    except DaemonUnavailable:
        discard(project, info)
        return dict(success=True, running=False, staleCleaned=True)
    except (OSError, ValueError) as exc:
        raise RevError("停止请求未得到确认：" + str(exc)) from exc
    if link.wait_for(lambda: read_endpoint(project) is None, wait_seconds):
        return dict(success=True, running=False, stopped=True)
    raise RevError("已发送停止请求，但服务在时限内没有退出。")


def control(operation, project, idle_timeout=1800.0, port=0) -> dict:
    if operation == "start":
        return start(project, idle_timeout, port)
    if operation == "stop":
        return stop(project)
    if operation == "status":
        return status(project)
    if operation == "serve":
        from .daemon_server import serve
        return serve(project, idle_timeout, port)
    raise RevError("未知的 daemon 操作：" + str(operation))
