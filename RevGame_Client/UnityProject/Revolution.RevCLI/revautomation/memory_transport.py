"""Editor 本机 HTTP 内存请求：一次 POST，重载只重新连接 GET，绝不重发业务变更。"""
import json
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request

from .errors import RevError, RevCancelled


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise RevError("Editor HTTP 不接受重定向")


class EditorHttpError(RevError):
    def __init__(self, status, detail):
        super().__init__("Editor HTTP %s: %s" % (status, detail))
        self.status = status


def credentials(client):
    from .client import read_json_settled
    info = read_json_settled(client.root / "editor-http.json", attempts=3)
    url = urllib.parse.urlsplit(info.get("url", ""))
    if info.get("protocol") != 1 or Path(info.get("project", "")).resolve() != client.project or not info.get("token") \
            or url.scheme != "http" or url.hostname != "127.0.0.1" or url.path not in ("", "/") or url.query or url.fragment or url.username:
        raise RevError("Editor HTTP 凭据身份或本机地址无效")
    return info


def request(info, method, path, data=None, timeout=5):
    raw = json.dumps(data, ensure_ascii=False, allow_nan=False).encode("utf-8") if data is not None else None
    call = urllib.request.Request(info["url"] + path, raw, method=method, headers={
        "Authorization": "Bearer " + info["token"], "X-Rev-Session": info["sessionId"], "Content-Type": "application/json"})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(call, timeout=timeout) as response:
            body = response.read(24 * 1024 * 1024 + 1)
        if len(body) > 24 * 1024 * 1024:
            raise RevError("Editor HTTP 响应超过 24 MiB")
        return json.loads(body)
    except urllib.error.HTTPError as exc:
        raise EditorHttpError(exc.code, exc.read(2048).decode("utf-8", errors="replace")) from None


def ready(client, session, deadline, cancel):
    # 重载期间凭据或 listener 可能还未就绪。只轮询 GET，确认同一 Editor 会话后再提交。
    while time.monotonic() < deadline:
        if cancel is not None and cancel.is_set():
            raise RevCancelled("等待 Editor HTTP 时取消，未提交动作")
        try:
            info = credentials(client)
            if info["sessionId"] != session:
                raise RevError("Editor 会话已变化，请重新发起请求")
            request(info, "GET", "/health")
            return info
        except OSError:
            time.sleep(.1)
    raise TimeoutError("等待 Editor HTTP 就绪超时，动作未提交")


def receipt_path(client, ident):
    return client.root / "detached-jobs" / (ident + ".json")


def decode_result(client, ident, response):
    if response.get("id") != ident:
        raise RevError("Editor HTTP 响应身份不匹配")
    # 过期回执只有工单身份，没有完整的业务协议字段。
    if not response.get("pending") and response.get("error") != "ResultExpired" and response.get("protocol") != 1:
        raise RevError("Editor HTTP 响应协议不匹配")
    if response.get("dataJson"):
        response["data"] = json.loads(response.pop("dataJson"))
    response["transport"] = "http-memory"
    work = client.root / "jobs" / ident
    response["jobDirectory"] = str(work) if work.is_dir() else None
    return response


def call(client, payload, cancel=None, *, detach=False):
    ident = payload["id"]
    deadline = time.monotonic() + client.timeout
    info = ready(client, payload["sessionId"], deadline, cancel)
    if cancel is not None and cancel.is_set():
        raise RevCancelled("请求提交前已取消")
    receipt = None
    if detach:
        from .client import atomic_json
        # 先保存身份，哪怕 POST 回执断线也能查询原工单；不保存凭据、正文和动作结果。
        receipt = receipt_path(client, ident)
        atomic_json(receipt, {key: payload[key] for key in ("id", "sessionId", "action", "deadlineUtc")})
    # POST 出错时业务状态可能未知；不转文件桥、不再次 POST，以免动作执行两遍。
    try:
        accepted = request(info, "POST", "/call", payload)
    except OSError as exc:
        raise RevError(f"内存请求提交结果未知：{ident}；未切换通道或重发，请先检查日志。{exc}") from None
    if not accepted.get("success") or accepted.get("id") != ident:
        raise RevError(f"Editor HTTP 提交回执不匹配：{ident}；请查询原工单。")
    if detach:
        return dict(success=True, accepted=True, pending=True, id=ident, transport="http-memory", receipt=str(receipt))
    try:
        while True:
            if cancel is not None and cancel.is_set():
                raise RevCancelled("请求取消：" + ident)
            if time.monotonic() >= deadline:
                raise TimeoutError("内存工单超时：" + ident + "；未自动重放")
            try:
                current = credentials(client)
                if current["sessionId"] != info["sessionId"]:
                    raise RevError("Editor 已重新启动，原内存结果不可恢复；未自动重放：" + ident)
                info = current
                response = request(info, "GET", "/jobs/" + ident)
                if not response.get("pending"):
                    if response.get("error") == "ResultExpired":
                        raise RevError(response.get("message"))
                    response = decode_result(client, ident, response)
                    try:
                        request(info, "POST", "/jobs/" + ident + "/ack", {})
                    except (OSError, RevError):
                        pass  # 已收到结果；确认失败由服务保留上限回收，不改变业务成功结果。
                    return response
            except OSError:
                # Unity 重载短暂关闭 listener：只等待同一个请求的 GET，不能重新提交。
                pass
            time.sleep(.02)
    except (RevCancelled, TimeoutError, KeyboardInterrupt):
        try:
            request(info, "DELETE", "/jobs/" + ident)
        except (OSError, RevError):
            pass
        raise


def detached_job(client, ident, cancel=False, wait=False):
    from .client import read_json
    receipt = read_json(receipt_path(client, ident))
    if receipt.get("id") != ident or not isinstance(receipt.get("sessionId"), str):
        raise RevError("后台工单回执身份无效：" + ident)
    deadline = time.monotonic() + client.timeout
    path = "/jobs/" + ident
    while True:
        try:
            info = credentials(client)
            if info["sessionId"] != receipt["sessionId"]:
                raise RevError("Editor 会话已更换，无法在新会话查询旧工单：" + ident)
            remaining = max(.05, min(5, deadline - time.monotonic()))
            result = decode_result(client, ident, request(info, "GET", path, timeout=remaining))
            result["action"] = receipt.get("action")
            if cancel and result.get("pending"):
                cancelled = request(info, "DELETE", path, timeout=remaining)
                return dict(success=bool(cancelled.get("success")), id=ident, cancelRequested=bool(cancelled.get("success")),
                            pending=True, transport="http-memory")
            if not wait or not result.get("pending"):
                return result
        except EditorHttpError as exc:
            if exc.status == 404:
                return dict(success=False, error="JobNotFound", id=ident, transport="http-memory",
                            message="编辑器没有保留该工单。请核对原回执或业务状态。")
            raise
        except OSError as exc:
            if cancel:
                raise RevError(f"取消回执未知：{ident}；请先查询原工单。{exc}") from None
            if not wait:
                raise
            # 临时断线只重读当前会话；等待超时让任务继续运行。
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return dict(success=False, error="WaitTimeout", id=ident, pending=True, transport="http-memory",
                        message="等待期限已到。可继续查询原工单。")
        time.sleep(min(.2, remaining))
