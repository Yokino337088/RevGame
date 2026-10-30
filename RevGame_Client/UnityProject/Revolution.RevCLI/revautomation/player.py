"""本机Player生命周期管理；启动身份与endpoint绑定，拒绝按任意PID杀进程。"""
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import urllib.request
import uuid
from .client import RevError, atomic_json, read_json


def specs():
    fields = {"executable": {"type": "string"}, "directory": {"type": "string"},
              "port": {"type": "integer", "minimum": 1024, "maximum": 65535},
              "arguments": {"type": "array", "items": {"type": "string"}},
              "timeout": {"type": "number", "minimum": 1, "maximum": 120}}
    return [dict(id="player." + name, target="cli", description=desc, readOnly=name == "status", confirm=False,
                 inputSchema=dict(type="object", properties=fields if name == "launch" else {"directory": fields["directory"]},
                                  required=["executable", "directory"] if name == "launch" else ["directory"], additionalProperties=False))
            for name, desc in [("launch", "启动指定Windows Player，等待新会话凭据和身份；返回不含token"),
                               ("status", "检查本次Player HTTP会话身份；不操作Editor"),
                               ("stop", "向已验证的Player发送runtime.quit；拒绝任意PID强杀")]]


def request(endpoint, method, path, data=None):
    # 仅本机直连，避免代理外传运行时凭据；跨设备应由明确端口转发接入。
    from urllib.parse import urlsplit
    url = urlsplit(endpoint["url"])
    if url.scheme != "http" or url.hostname != "127.0.0.1" or url.path or url.query or url.fragment or url.username:
        raise RevError("Player endpoint必须是127.0.0.1 HTTP origin")
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(endpoint["url"] + path, data=body, method=method,
        headers={"Authorization": "Bearer " + endpoint["token"], "X-Rev-Session": endpoint["sessionId"], "Content-Type": "application/json"})
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            raise RevError("Player endpoint不允许重定向")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(req, timeout=3) as response:
        return json.loads(response.read(1024 * 1024))


def session(directory):
    folder = Path(directory).resolve()
    record = read_json(folder / "player.json")
    endpoint = read_json(folder / "runtime.json")
    if endpoint.get("launchId") != record["launchId"] or endpoint.get("pid") != record["pid"]:
        raise RevError("Player身份不匹配；拒绝复用旧endpoint")
    health = request(endpoint, "GET", "/health")
    if any(health.get(key) != endpoint.get(key) for key in ("sessionId", "pid", "launchId")):
        raise RevError("HTTP实例与本次会话不符")
    return folder, record, endpoint, health


def execute(action, arguments, cancel=None):
    if action == "player.launch":
        return launch(**arguments, cancel=cancel)
    folder, record, endpoint, health = session(arguments["directory"])
    if action == "player.status":
        return dict(success=True, data=health, endpointPath=str(folder / "runtime.json"))
    if action != "player.stop":
        raise RevError("未知Player动作")
    ident = uuid.uuid4().hex
    request(endpoint, "POST", "/call", dict(id=ident, action="runtime.quit", payload={}, timeoutSeconds=10))
    deadline = time.monotonic() + 12
    accepted = False
    while time.monotonic() < deadline:
        if not accepted:
            result = request(endpoint, "GET", "/jobs/" + ident)
            if not result.get("pending"):
                if not result.get("success"):
                    raise RevError("Player退出被拒绝：" + str(result.get("message")))
                accepted = True
        # 桥在OnDestroy清除本次endpoint；不把POST成功误报成已退出。
        if accepted and not (folder / "runtime.json").exists():
            return dict(success=True, data=dict(pid=record["pid"], stopped=True, endpointRemoved=True))
        time.sleep(.1)
    raise TimeoutError("Player退出尚未确认，保留会话目录检查日志")


def launch(executable, directory, port=18766, arguments=None, timeout=60, cancel=None):
    exe = Path(executable).resolve()
    folder = Path(directory).resolve()
    if not exe.is_file() or exe.suffix.lower() != ".exe":
        raise RevError("首版launcher需要存在的Windows .exe；不执行shell脚本")
    if folder.exists() and any(folder.iterdir()):
        raise RevError("每次launch使用新的空会话目录，避免覆盖凭据与日志")
    if type(port) is not int or not 1024 <= port <= 65535 or not 1 <= timeout <= 120:
        raise RevError("端口1024..65535；timeout=1..120秒")
    extra = arguments or []
    if not isinstance(extra, list) or any(not isinstance(x, str) for x in extra):
        raise RevError("arguments需要字符串数组")
    if any(x.lower().startswith(("-revautomation", "-logfile", "-batchmode", "-nographics")) for x in extra):
        raise RevError("launch保留自动化/日志参数；性能Player必须有图形")
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", port))
    folder.mkdir(parents=True, exist_ok=True)
    launch_id = uuid.uuid4().hex
    endpoint_path = folder / "runtime.json"
    env = os.environ.copy()
    env["REV_AUTOMATION_LAUNCH_ID"] = launch_id
    env["REV_AUTOMATION_ENDPOINT"] = str(endpoint_path)
    command = [str(exe), "-revAutomation", "-revAutomationPort", str(port), "-logFile", str(folder / "Player.log"), *extra]
    # 游戏窗口需要实际显示以测渲染；只隐藏辅助控制台窗口，不使用无图形模式。
    process = subprocess.Popen(command, cwd=exe.parent, env=env, stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    atomic_json(folder / "player.json", dict(pid=process.pid, launchId=launch_id, executable=str(exe), command=command))
    deadline = time.monotonic() + timeout
    try:
        while time.monotonic() < deadline:
            if cancel is not None and cancel.is_set():
                raise RevError("Player启动已取消")
            if process.poll() is not None:
                raise RevError("Player提前退出，exit=" + str(process.returncode) + "；检查Player.log")
            if endpoint_path.exists():
                try:
                    _, _, _, health = session(folder)
                    return dict(success=True, data=health, endpointPath=str(endpoint_path), sessionDirectory=str(folder))
                except (OSError, ValueError):
                    pass
            time.sleep(.15)
        raise TimeoutError("未获得本次Player endpoint；检查构建是否包含自动化宏与Player.log")
    except BaseException:
        # 只结束本次Popen持有的子进程，绝不按缓存PID终止别人的实例。
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        raise
