"""直连游戏运行时的自动化 HTTP 服务：调 GM、调游戏能力、看状态。

编辑器 Play 与独立 Player 用同一套命令，区别只在凭据文件在哪：
- 编辑器：先 `rev.py runtime start`，凭据写在 <工程>/Library/RevolutionAutomation/runtime.json
- Player：`rev.py player launch --directory <会话目录>`，凭据写在 <会话目录>/runtime.json

为什么不经过编辑器文件桥：文件桥只能到编辑器，到不了独立 Player；HTTP 两边都能到。
协议约束（与 Unity 侧 RevRuntimeAutomationHost 一致）：
- POST /call 只表示接单，结果必须 GET /jobs/<id> 读最终 success。
- 超时或中断只 DELETE 同一个 id，绝不换新 id 重发：否则 GM 可能被执行两次。
"""
import json
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from .errors import RevCancelled, RevError

MAX_RESPONSE = 24 * 1024 * 1024


def endpoint_file(project=None, session=None):
    if session:
        path = Path(session).resolve()
        return path if path.suffix == ".json" else path / "runtime.json"
    if project is None:
        raise RevError("需要 --project（编辑器 Play）或 --session（独立 Player 会话目录）")
    return Path(project).resolve() / "Library" / "RevolutionAutomation" / "runtime.json"


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise RevError("运行时服务不允许重定向")


class GameClient:
    def __init__(self, path, timeout=120):
        self.path, self.timeout = Path(path), timeout
        if not self.path.is_file():
            raise RevError(f"运行时服务未开启：找不到 {self.path}。编辑器先 editor play 再 runtime start；Player 先 player launch。")
        try:
            self.endpoint = json.loads(self.path.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as exc:
            raise RevError(f"凭据文件无法读取：{exc}") from None
        url = urllib.parse.urlsplit(str(self.endpoint.get("url", "")))
        # 只允许本机：token 绝不经过代理或发往别的主机。
        if url.scheme != "http" or url.hostname not in ("127.0.0.1", "localhost", "::1") or url.username or url.password \
                or url.path not in ("", "/") or url.query or url.fragment:
            raise RevError("运行时地址必须是本机 HTTP；跨设备请用端口转发")
        if self.endpoint.get("protocol") != 1 or not self.endpoint.get("token") or not self.endpoint.get("sessionId"):
            raise RevError("凭据文件格式不对，可能是旧版本或已损坏")
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())

    def request(self, method, path, data=None):
        body = json.dumps(data, ensure_ascii=False, allow_nan=False).encode("utf-8") if data is not None else None
        req = urllib.request.Request(self.endpoint["url"].rstrip("/") + path, body, method=method, headers={
            "Authorization": "Bearer " + self.endpoint["token"], "X-Rev-Session": self.endpoint["sessionId"],
            "Content-Type": "application/json"})
        try:
            with self.opener.open(req, timeout=min(5, self.timeout)) as response:
                raw = response.read(MAX_RESPONSE + 1)
        except urllib.error.HTTPError as exc:
            detail = exc.read(4096).decode("utf-8", errors="replace")
            if exc.code == 409:
                raise RevError("运行时会话已更换（游戏重启或服务重开过），请重新读取状态后再操作") from None
            raise RevError(f"运行时 HTTP {exc.code}：{detail}") from None
        except (urllib.error.URLError, ConnectionError, TimeoutError) as exc:
            raise RevError(f"连不上运行时服务（游戏可能已退出 Play）：{exc}") from None
        if len(raw) > MAX_RESPONSE:
            raise RevError("运行时返回超过 24 MiB")
        return json.loads(raw)

    def health(self):
        return self.request("GET", "/health")

    def call(self, action, payload=None, confirm=False):
        request_id = uuid.uuid4().hex
        accepted = self.request("POST", "/call", dict(id=request_id, action=action, payload=payload or {},
                                                      confirm=bool(confirm), timeoutSeconds=min(self.timeout, 3600)))
        if not accepted.get("success"):
            raise RevError(accepted.get("message") or accepted.get("error") or "运行时拒绝接单")
        started = time.monotonic()
        polls = 0
        try:
            while True:
                result = self.request("GET", "/jobs/" + request_id)
                if not result.get("pending"):
                    if result.get("id") != request_id or result.get("sessionId") != self.endpoint["sessionId"]:
                        raise RevError("运行时返回的身份与请求不符")
                    return result
                if time.monotonic() - started >= self.timeout:
                    raise TimeoutError(f"运行时动作超时，结果未知，不要重放；工单 {request_id}")
                polls += 1
                # 大多数 GM 一帧内完成：先等 5ms，再 20ms，长作业退到 50ms。
                time.sleep(.005 if polls == 1 else .02 if time.monotonic() - started < .3 else .05)
        except (RevCancelled, TimeoutError, KeyboardInterrupt):
            try:
                self.request("DELETE", "/jobs/" + request_id)
            except (OSError, RevError):
                pass
            raise


def gm_line(name, values):
    """拼出 RevGM 能解析的一行。参数一律加引号，含空格的参数也安全；解析器不支持的字符直接拒绝。"""
    if not isinstance(name, str) or not name.strip() or any(c in name for c in ' "\r\n\0'):
        raise RevError("GM 名称不能为空，也不能含空格、引号或换行")
    for value in values:
        if any(c in value for c in '"\r\n\0'):
            raise RevError("GM 参数不能含双引号、换行或 NUL")
    return name + "".join(' "' + value + '"' for value in values)


def run(operation, project, session, timeout, confirm, name=None, values=(), payload=None, query=None):
    client = GameClient(endpoint_file(project, session), timeout)
    if operation == "status":
        return dict(success=True, data=client.health(), endpointFile=str(client.path))
    if operation in ("actions", "gms"):
        result = client.call("runtime.capabilities")
        if not result.get("success"):
            return result
        data = result["data"]
        key = "actions" if operation == "actions" else "commands"
        items = data.get(key) or []
        if query:
            needle = query.lower()
            items = [item for item in items if needle in (item.get("id") or item.get("name") or "").lower()
                     or needle in (item.get("description") or "").lower()]
        if operation == "gms" and not data.get("gmEnabled"):
            return dict(success=True, gmEnabled=False, total=0, items=[], message="本局 RevGM 未启用（构建未带 GM 或被关闭）")
        return dict(success=True, total=len(items), items=items, **({"gmEnabled": True} if operation == "gms" else {}))
    if operation == "gm":
        return client.call("gm.exec", dict(command=gm_line(name, list(values))), confirm)
    if operation == "call":
        if not isinstance(payload, dict):
            raise RevError("参数必须是 JSON 对象")
        return client.call(name, payload, confirm)
    raise RevError("未知 game 操作：" + operation)
