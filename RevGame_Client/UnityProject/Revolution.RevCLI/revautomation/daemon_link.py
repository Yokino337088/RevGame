"""常驻服务的传输层：只依赖 json/os/socket，故意不碰 argparse、client、pathlib。

为什么这么抠：实测单次 `python rev.py call` 约 500ms，其中 Unity 往返只有约 100ms，其余全是
解释器启动与模块导入（import cli 143ms、构建 argparse 解析器 65ms）。转发路径如果还要先把这些
都加载一遍，就和不转发一样慢，甚至更慢。所以"要不要转发"必须在导入 cli 之前决定。
"""
from __future__ import annotations

import json
import os
import socket
import time

from .errors import RevError

PROTOCOL = 1
ENDPOINT_NAME = "cli-daemon.json"
HEADERS_NAME = "cli-daemon.headers"
LOG_NAME = "cli-daemon.log"
MAX_BODY = 1 << 20
# 这些命令不需要 Unity，或必须留在调用方进程里（job 要在长调用期间也能响应取消、player 会拉起独立进程）。
# connect 要拉起 Unity 和服务本身，ship 跨越数十分钟且要逐步汇报，都不能塞进一次转发。
LOCAL_ONLY_FAMILIES = frozenset({"daemon", "gif", "report", "evidence", "player", "job", "doctor", "commands", "connect", "ship", "game"})
STALE_STATUS = 426


class DaemonUnavailable(RevError):
    """连接阶段就失败：请求没有发出去，回退到本进程执行是安全的。"""


def daemon_dir(project) -> str:
    return os.path.join(os.path.realpath(project), "Library", "RevolutionAutomation")


def code_stamp() -> tuple:
    """CLI 自身代码与动作清单的指纹。服务常驻在内存里，改了代码却继续跑旧逻辑是最难查的一类问题。"""
    root = os.path.dirname(os.path.abspath(__file__))
    paths = [os.path.join(root, name) for name in sorted(os.listdir(root)) if name.endswith(".py")]
    paths.append(os.path.join(os.path.dirname(root), "commands.json"))
    stamp = []
    for path in paths:
        try:
            info = os.stat(path)
        except OSError:
            continue
        stamp.append((os.path.basename(path), info.st_mtime_ns, info.st_size))
    return tuple(stamp)


def _same(left, right) -> bool:
    return os.path.normcase(os.path.realpath(left)) == os.path.normcase(os.path.realpath(right))


def read_endpoint(project):
    try:
        with open(os.path.join(daemon_dir(project), ENDPOINT_NAME), encoding="utf-8-sig") as handle:
            value = json.load(handle)
    except (OSError, ValueError):
        return None
    if not isinstance(value, dict) or value.get("protocol") != PROTOCOL:
        return None
    if type(value.get("port")) is not int or not isinstance(value.get("token"), str):
        return None
    if not isinstance(value.get("project"), str) or not value["project"]:
        return None
    try:
        if not _same(value["project"], project):
            return None
    except OSError:
        return None
    return value


def discard(project, info):
    """只清理确认已死的服务残留；token 对不上说明已经换了一个新服务，不能误删。"""
    current = read_endpoint(project)
    if current is None or current.get("token") != info.get("token"):
        return
    root = daemon_dir(project)
    for name in (ENDPOINT_NAME, HEADERS_NAME):
        try:
            os.unlink(os.path.join(root, name))
        except OSError:
            pass


def request(info, method, path, body=None, timeout=10.0, connect_timeout=1.0):
    """返回 (HTTP状态码, JSON)。连接失败抛 DaemonUnavailable；连接之后的任何失败都不能当作"没发出去"。"""
    try:
        sock = socket.create_connection(("127.0.0.1", info["port"]), timeout=connect_timeout)
    except OSError as exc:
        raise DaemonUnavailable(f"常驻服务没有响应：{exc}") from exc
    try:
        sock.settimeout(timeout)
        payload = b"" if body is None else json.dumps(body, ensure_ascii=False, allow_nan=False).encode("utf-8")
        head = [f"{method} {path} HTTP/1.1", f"Host: 127.0.0.1:{info['port']}", "Authorization: Bearer " + info["token"],
                "Connection: close", f"Content-Length: {len(payload)}"]
        if payload:
            head.append("Content-Type: application/json")
        sock.sendall(("\r\n".join(head) + "\r\n\r\n").encode("ascii") + payload)
        chunks = []
        while True:
            data = sock.recv(65536)
            if not data:
                break
            chunks.append(data)
    finally:
        sock.close()
    raw = b"".join(chunks)
    header, separator, content = raw.partition(b"\r\n\r\n")
    if not separator:
        raise OSError("常驻服务返回的响应不完整")
    lines = header.split(b"\r\n")
    status = int(lines[0].split(b" ", 2)[1])
    for line in lines[1:]:
        name, _, value = line.partition(b":")
        if name.strip().lower() == b"content-length" and int(value) != len(content):
            raise OSError("常驻服务返回的响应被截断")
    return status, json.loads(content.decode("utf-8") or "{}")


def forward(project, argv, cwd, timeout):
    """把整条命令行交给常驻服务。没有服务（或连接前就失败）返回 None，由调用方本地执行。

    关键约束：请求一旦发出，就绝不再回退本地重放——写操作可能已经生效，重放会变成重复修改。
    """
    info = read_endpoint(project)
    if info is None:
        return None
    try:
        status, body = request(info, "POST", "/run", {"argv": list(argv), "cwd": str(cwd)}, timeout=timeout + 30)
    except DaemonUnavailable:
        discard(project, info)
        return None
    except (OSError, ValueError) as exc:
        raise RevError("常驻服务已收到请求但连接中断，结果未知；不会在本地重放。请用 job 或 Console 核对副作用：" + str(exc)) from exc
    if status == STALE_STATUS:
        return None  # 服务发现自己是旧代码，请求没有执行，本地执行是安全的
    if status == 200 and isinstance(body, dict) and "exitCode" in body:
        return body
    message = body.get("message") if isinstance(body, dict) else None
    if status == 503:
        raise RevError(message or "常驻服务正忙，本次没有执行，可以重试。")
    if status >= 500:
        raise RevError(f"常驻服务执行中出错（HTTP {status}），结果未知；不会在本地重放，请读回确认：{message or '无详情'}")
    raise RevError(f"常驻服务拒绝了本次请求（HTTP {status}）：{message or '无详情'}；可用 --no-daemon 本地执行。")


def wait_for(predicate, seconds, step=.05):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(step)
    return False
