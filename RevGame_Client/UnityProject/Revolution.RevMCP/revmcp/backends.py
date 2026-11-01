"""把 Agent 的统一调用接到三个真实执行入口。

Editor 走 Revolution.RevCLI 的文件工单；游戏走相同注册表（Editor 转发或
Player 本机 HTTP）；外部工具走人工登记的可执行文件。这里只适配传输，不实现
背包、战斗等玩法。网络成功/进程退出不等于业务正确，后置断言交给 workflow。
"""
from __future__ import annotations
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from revautomation.client import RevClient, RevError, RevCancelled, atomic_json, read_json
from revautomation.cli import validate_action
from .schema import validate, check_schema, object_schema
from .activity import Activity
from revautomation import player


class RuntimeClient:
    """读取 Unity 产生的凭据连接一个 session；不自动重试已提交的变更。"""
    def __init__(self, endpoint_file, timeout=120):
        self.endpoint_file, self.timeout = Path(endpoint_file), timeout
        self.endpoint = read_json(self.endpoint_file)
        url = urllib.parse.urlsplit(self.endpoint["url"])
        if url.scheme != "http" or url.hostname not in ("127.0.0.1", "localhost", "::1") or url.username or url.password or url.path not in ("", "/") or url.query or url.fragment:
            raise RevError("Runtime URL must be a loopback HTTP origin; use device port forwarding")
        if self.endpoint.get("protocol") != 1 or not self.endpoint.get("token") or not self.endpoint.get("sessionId"):
            raise RevError("Invalid runtime endpoint file")
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, path, data=None):
        body = json.dumps(data, ensure_ascii=False, allow_nan=False).encode("utf-8") if data is not None else None
        req = urllib.request.Request(self.endpoint["url"].rstrip("/") + path, body, method=method,
            headers={"Authorization": "Bearer " + self.endpoint["token"], "X-Rev-Session": self.endpoint["sessionId"], "Content-Type": "application/json"})
        try:
            with self.opener.open(req, timeout=min(5, self.timeout)) as response:
                raw = response.read(24 * 1024 * 1024 + 1)
            if len(raw) > 24 * 1024 * 1024:
                raise RevError("Runtime response exceeds 24 MiB")
            return json.loads(raw)
        except urllib.error.HTTPError as exc:
            message = exc.read(4096).decode("utf-8", errors="replace")
            raise RevError(f"Runtime HTTP {exc.code}: {message}") from None

    def call(self, action, payload=None, confirm=False, cancel=None):
        # POST 只接单，后续 GET 才读取最终结果。取消时 DELETE 同一个 id，
        # 不重新 POST 一个新 id；否则网络抖动可能让业务动作执行两次。
        if cancel is not None and cancel.is_set():
            raise RevCancelled("Runtime request cancelled before submission")
        request_id = uuid.uuid4().hex
        self.request("POST", "/call", dict(id=request_id, action=action, payload=payload or {}, confirm=confirm, timeoutSeconds=min(self.timeout, 3600)))
        started = time.monotonic()
        deadline = started + self.timeout
        polls = 0
        try:
            while True:
                if cancel is not None and cancel.is_set():
                    raise RevCancelled("Runtime request cancelled: " + request_id)
                response = self.request("GET", "/jobs/" + request_id)
                if not response.get("pending"):
                    if response.get("id") != request_id or response.get("sessionId") != self.endpoint["sessionId"]:
                        raise RevError("Runtime response identity mismatch")
                    return response
                if time.monotonic() >= deadline:
                    raise TimeoutError("Runtime timeout; inspect job " + request_id)
                # 短操作不白等固定80ms；长作业退让到50ms，仍只GET原工单。
                polls += 1
                cadence = .005 if polls == 1 else .02 if time.monotonic() - started < .3 else .05
                time.sleep(min(cadence, max(0, deadline - time.monotonic())))
        except (RevCancelled, TimeoutError, KeyboardInterrupt):
            try:
                self.request("DELETE", "/jobs/" + request_id)
            except (OSError, RevError):
                pass
            raise


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RevError("Runtime redirects are refused")


def external_specs(path):
    """校验显式清单。脚本路径和固定启动参数由项目登记，AI 只提供 JSON 数据。"""
    if path is None:
        return []
    document = read_json(Path(path))
    if not isinstance(document, dict) or set(document) != {"version", "tools"} or document["version"] != 1 or not isinstance(document["tools"], list):
        raise RevError("External manifest requires version=1 and tools[]")
    result, names = [], set()
    for spec in document["tools"]:
        if set(spec) - {"name", "description", "command", "cwd", "inputSchema", "timeoutSeconds", "readOnly", "confirm", "output"}:
            raise RevError("Unknown external tool manifest field")
        name = spec.get("name")
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", name) or name in names:
            raise RevError("External tool names must be unique ASCII identifiers")
        names.add(name)
        command = spec.get("command")
        if not isinstance(command, list) or not command or any(not isinstance(x, str) for x in command):
            raise RevError("command must be an argument array")
        executable = Path(command[0])
        if not executable.is_absolute() or not executable.is_file() or executable.suffix.lower() in (".cmd", ".bat", ".ps1"):
            raise RevError("Use an absolute executable (Python for .py); shell scripts are not supported")
        cwd = Path(spec.get("cwd", Path(path).resolve().parent))
        if not cwd.is_absolute() or not cwd.is_dir():
            raise RevError("cwd must be an existing absolute directory")
        schema = spec.get("inputSchema")
        check_schema(schema)
        if schema["type"] != "object":
            raise RevError("External inputSchema root must be object")
        timeout = spec.get("timeoutSeconds", 120)
        if type(timeout) not in (int, float) or not 0 < timeout <= 3600:
            raise RevError("External timeoutSeconds must be 0..3600")
        if spec.get("output", "json") not in ("json", "text"):
            raise RevError("output must be json/text")
        if any(type(spec[k]) is not bool for k in ("readOnly", "confirm") if k in spec):
            raise RevError("readOnly/confirm must be boolean")
        # Fixed trusted arguments; all AI-controlled data goes over JSON stdin.
        result.append(dict(spec, cwd=str(cwd), inputSchema=schema, target="cli", id=name))
    return result


def call_external(spec, arguments, confirm, cancel, limit=4 * 1024 * 1024):
    """把参数写入 stdin，以 shell=False 启动登记程序，再读取有大小限制的输出。

    不把模型生成的文字拼成 PowerShell/cmd 命令。已有 CLI 如只接受 argv，
    应提供一个固定包装脚本读取 JSON、校验后调用它，而不是开放任意 shell。
    """
    validate(arguments, spec["inputSchema"])
    if spec.get("confirm") and not confirm:
        raise RevError("External tool requires confirm=true")
    # Temporary files avoid pipe deadlocks and unbounded in-memory buffering.
    with tempfile.TemporaryFile() as stdin, tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        stdin.write(json.dumps(arguments, ensure_ascii=False, allow_nan=False).encode("utf-8")); stdin.seek(0)
        # 外部 Python 在 Windows 重定向 stdin 时可能按系统代码页读中文；协议统一 UTF-8。
        environment = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
        process = subprocess.Popen(spec["command"], cwd=spec["cwd"], stdin=stdin, stdout=stdout, stderr=stderr, shell=False, env=environment)
        deadline = time.monotonic() + spec.get("timeoutSeconds", 120)
        try:
            while process.poll() is None:
                if cancel is not None and cancel.is_set():
                    raise RevCancelled("External tool cancelled; inspect its side effects")
                if time.monotonic() >= deadline:
                    raise TimeoutError("External tool timed out")
                if os.fstat(stdout.fileno()).st_size + os.fstat(stderr.fileno()).st_size > limit:
                    raise RevError("External output exceeds 4 MiB")
                time.sleep(.05)
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=2)
        stdout.seek(0); stderr.seek(0)
        out, err = stdout.read(limit + 1), stderr.read(limit + 1)
    if len(out) + len(err) > limit:
        raise RevError("External output exceeds 4 MiB")
    text, errors = out.decode("utf-8-sig"), err.decode("utf-8-sig", errors="replace")
    data = json.loads(text) if spec.get("output", "json") == "json" and text.strip() else text
    success = process.returncode == 0 and not (isinstance(data, dict) and data.get("success") is False)
    return dict(success=success, exitCode=process.returncode, data=data, stderr=errors)


def gm_line(command, arguments):
    schema = command["inputSchema"]
    validate(arguments, schema)
    if "args" in schema["properties"]:
        values = arguments.get("args", [])
    else:
        count = len(schema["properties"])
        present = [i for i in range(count) if "arg" + str(i) in arguments]
        last = max(present, default=-1)
        values = []
        for i in range(last + 1):
            key = "arg" + str(i)
            if key not in arguments:
                raise RevError("GM positional arguments cannot contain holes; supply earlier optional arguments")
            value = arguments[key]
            values.append(("true" if value else "false") if isinstance(value, bool) else str(value))
    if any('"' in v or any(c in v for c in "\r\n\0") for v in values):
        raise RevError("GM parser cannot represent quote/newline/NUL in an argument")
    return command["name"] + "".join(' "' + v + '"' for v in values)


class Hub:
    """统一目录与调度器。先 discover 查实际接口，再按 target/action 精确调用。"""
    def __init__(self, project=None, runtime_endpoint=None, cli_root=None, external_manifest=None, reports=None, timeout=120):
        self.timeout = timeout
        self.editor = RevClient(project, mode="live", timeout=timeout) if project else None
        self.runtime_endpoint = Path(runtime_endpoint) if runtime_endpoint else None
        self.external_manifest = external_manifest
        self.reports = Path(reports or (Path.cwd() / ".rev-mcp/results")).resolve()
        self.manifest = read_json(Path(cli_root) / "commands.json")
        self.known = {s["id"]: s for s in self.manifest["actions"]}
        # --project 时写到 Unity 忽略目录；纯 Player 写到本机 endpoint 旁边，纯 CLI 写到报告旁边。
        # Unity 执行日志由桥自身记录，Agent 流补足外部 CLI、调用拒绝和计划断言等 Unity 看不到的工作。
        activity_root = self.editor.root if self.editor else self.runtime_endpoint.parent if self.runtime_endpoint else self.reports.parent
        self.activity = Activity(activity_root / "activity")
        self._editor_catalog = None
        self._editor_catalog_key = None
        self._editor_catalog_until = 0.0

    def _editor_key(self):
        endpoint = self.editor.endpoint() if self.editor else None
        if not endpoint:
            return None
        return tuple(endpoint.get(key) for key in
                     ("sessionId", "domainId", "playing", "paused", "compiling", "updating", "compilationFailed"))

    def _catalog(self, target, cancel=None):
        """编辑器只发现编辑器；短缓存不缓存业务状态，也不能跨会话复用。"""
        if cancel is not None and cancel.is_set():
            raise RevCancelled("Capability lookup cancelled")
        if target == "cli":
            return dict(success=True, actions=player.specs() + external_specs(self.external_manifest), commands=[], unavailable=[])
        if target != "editor":
            return self.discover(cancel)
        key = self._editor_key()
        if self._editor_catalog is not None and key is not None and key == self._editor_catalog_key and time.monotonic() < self._editor_catalog_until:
            return self._editor_catalog
        result = self.discover(cancel, editor_only=True)
        if key is not None and not result["unavailable"]:
            self._editor_catalog, self._editor_catalog_key = result, key
            self._editor_catalog_until = time.monotonic() + 2.0
        else:
            self._editor_catalog = None
        return result

    def game_call(self, action, arguments, confirm=False, cancel=None):
        if self.runtime_endpoint:
            runtime = RuntimeClient(self.runtime_endpoint, self.timeout)
            project = runtime.endpoint.get("project")
            if self.editor and project and Path(project).resolve() != self.editor.project:
                raise RevError("Runtime endpoint belongs to another Unity project")
            return runtime.call(action, arguments, confirm, cancel)
        if not self.editor:
            raise RevError("Configure --project or --runtime-endpoint")
        if action == "gm.exec":
            return self.editor.call(action, arguments, confirm, cancel=cancel)
        return self.editor.call("runtime.call", {"runtimeAction": action, "argumentsJson": json.dumps(arguments, ensure_ascii=False)}, confirm, cancel=cancel)

    def discover(self, cancel=None, editor_only=False):
        self._editor_catalog = None
        # 编辑模式没有游戏能力时仍可返回 Editor 能力，并把缺项放进 unavailable。
        # 不能拿 commands.json 里的离线说明冒充目标工程已经安装的工具。
        result = dict(success=True, actions=player.specs(), commands=[], unavailable=[])
        if self.editor:
            try:
                response = self.editor.call("editor.capabilities", {}, cancel=cancel)
                if not response.get("success"):
                    raise RevError(str(response))
                if response["data"].get("errors"):
                    raise RevError("Editor registry errors: " + str(response["data"]["errors"]))
                for action in response["data"]["actions"]:
                    spec = self.known.get(action["id"], {})
                    result["actions"].append(dict(spec, **action, target="editor", readOnly=spec.get("readOnly", False)))
            except (OSError, RevError, TimeoutError) as exc:
                if isinstance(exc, RevCancelled):
                    raise
                result["unavailable"].append(dict(target="editor", reason=str(exc)))
        if editor_only:
            return result
        try:
            response = self.game_call("runtime.capabilities", {}, cancel=cancel)
            if not response.get("success"):
                raise RevError(str(response.get("message", response)))
            data = response["data"]
            result["actions"].extend(dict(a, target="game") for a in data["actions"])
            result["commands"] = data["commands"] if data.get("gmEnabled") else []
        except (OSError, RevError, TimeoutError) as exc:
            if isinstance(exc, RevCancelled):
                raise
            result["unavailable"].append(dict(target="game", reason=str(exc)))
        result["actions"].extend(external_specs(self.external_manifest))
        return result

    def call(self, target, action, arguments=None, confirm=False, cancel=None, catalog=None):
        # 用实时注册说明解释操作用途；intent 来自 Agent 明确提供的元数据，不从业务参数猜测。
        with self.activity.scope(description=self.known.get(action, {}).get("description", "")):
            ticket = self.activity.start(target, action, arguments)
        try:
            catalog = catalog if catalog is not None else self._catalog(target, cancel)
            specs = catalog.get("commands", []) if target == "gm" else catalog.get("actions", [])
            spec = next((s for s in specs if (s.get("name") if target == "gm" else s.get("id")) == action
                         and (target == "gm" or s.get("target") == target)), {})
            ticket[0]["description"] = spec.get("description", "")
            response = self._call(target, action, arguments, confirm, cancel, catalog)
        except (Exception, KeyboardInterrupt) as exc:
            self._editor_catalog = None
            self.activity.finish(ticket, error=exc)
            raise
        if not spec.get("readOnly") or not response.get("success"):
            self._editor_catalog = None
        self.activity.finish(ticket, response)
        return response

    def _call(self, target, action, arguments=None, confirm=False, cancel=None, catalog=None):
        arguments = arguments or {}
        catalog = catalog if catalog is not None else self._catalog(target, cancel)
        if target == "gm":
            command = next((c for c in catalog["commands"] if c["name"] == action), None)
            if command is None:
                raise RevError("GM is not currently registered: " + action)
            if command.get("highRisk") and not confirm:
                raise RevError("High-risk GM requires confirm=true")
            return self.game_call("gm.exec", {"command": gm_line(command, arguments)}, confirm, cancel)
        spec = next((s for s in catalog["actions"] if s["target"] == target and s["id"] == action), None)
        if spec is None:
            raise RevError(f"Capability not currently available: {target}/{action}")
        if target == "editor":
            if not self.editor:
                raise RevError("Editor is not configured")
            if spec.get("keys") is not None and set(arguments) - set(spec["keys"]):
                raise RevError("Unknown editor arguments")
            validate_action(action, arguments)
            return self.editor.call(action, arguments, confirm, cancel=cancel)
        validate(arguments, spec["inputSchema"])
        if target == "game":
            return self.game_call(action, arguments, confirm, cancel)
        if target == "cli":
            if action in ("player.launch", "player.status", "player.stop"):
                return player.execute(action, arguments, cancel)
            return call_external(spec, arguments, confirm, cancel)
        raise RevError("Unknown target")

    def validate_call(self, target, action, arguments, confirm, catalog):
        if target == "gm":
            spec = next((c for c in catalog["commands"] if c["name"] == action), None)
            if spec is None:
                raise RevError("GM unavailable: " + action)
            gm_line(spec, arguments)
            if spec.get("highRisk") and not confirm:
                raise RevError("High-risk GM requires confirm=true")
            return
        spec = next((s for s in catalog["actions"] if s["target"] == target and s["id"] == action), None)
        if spec is None:
            raise RevError("Capability unavailable: " + action)
        if spec.get("confirm") and not confirm:
            raise RevError("Capability requires confirm=true: " + action)
        if target == "editor":
            validate_action(action, arguments)
        else:
            validate(arguments, spec["inputSchema"])

    def save(self, result):
        self.reports.mkdir(parents=True, exist_ok=True)
        path = self.reports / (dt.datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8] + ".json")
        # Screenshots already have a file path. Avoid duplicating base64 in evidence JSON.
        def trim(value):
            if isinstance(value, dict):
                return {k: trim(v) for k, v in value.items() if k != "base64"}
            if isinstance(value, list):
                return [trim(v) for v in value]
            return value
        atomic_json(path, trim(result))
        return str(path)
