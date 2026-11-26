"""只在用户打开当前工程的实时活动窗口时上报诊断。事件临时文件由 Unity 消费和回收。"""
from contextlib import contextmanager
import datetime as dt
import json
import re
import threading
import time
import uuid

from revautomation.errors import RevError


_SECRET = re.compile(r"token|password|passwd|secret|credential|authorization|api.?key", re.I)
_INLINE_SECRET = re.compile(r'(?i)(Bearer\s+)[^\s"\\]+|((?:token|password|secret|api[_-]?key)\s*[=:]\s*)[^\s,;"\\]+')


def safe_text(value, limit=4096):
    """保留调试必需内容，隐藏常见凭据；自由文本仍请避免传入业务隐私。"""
    text = _INLINE_SECRET.sub(lambda m: (m.group(1) or m.group(2)) + "[已隐藏]", str(value or ""))
    return text if len(text) <= limit else text[:limit] + "…[已截断]"


def safe_value(value, depth=0):
    if depth > 12:
        return "[嵌套已截断]"
    if isinstance(value, dict):
        return {str(k): "[已隐藏]" if _SECRET.search(str(k)) else
                "[图像数据省略]" if str(k).lower() == "base64" else safe_value(v, depth + 1)
                for k, v in list(value.items())[:48]}
    if isinstance(value, (list, tuple)):
        result = [safe_value(v, depth + 1) for v in value[:24]]
        if len(value) > 24:
            result.append(f"…另有 {len(value) - 24} 项")
        return result
    if isinstance(value, str):
        # runtime.call 的 argumentsJson 等字段是 JSON 字符串，仍要递归脱敏。
        if value.lstrip().startswith(("{", "[")):
            try:
                return safe_value(json.loads(value), depth + 1)
            except (ValueError, RecursionError):
                pass
        return safe_text(value)
    return value


class Activity:
    """随当前工程会话发布活动元数据，缺少订阅时调用和收尾均无文件副作用。"""

    def __init__(self, publish=None, enabled=None):
        """绑定可选的发布方和采集状态查询。单独的 Player 和外部运行入口默认跳过上报。"""
        self.publish = publish
        self.enabled = enabled
        self.context = threading.local()
        self.last_error = ""

    @contextmanager
    def scope(self, **values):
        """计划名/步骤/清理阶段只影响当前线程，退出时还原，避免后一次调用被误归类。"""
        previous = getattr(self.context, "values", {})
        self.context.values = dict(previous, **values)
        try:
            yield
        finally:
            self.context.values = previous

    def start(self, target, action, arguments=None):
        """实时窗口订阅后才构造摘要，普通自动化不分配日志 JSON。"""
        if self.publish is None or (self.enabled is not None and not self.enabled()):
            return None
        row = dict(id=uuid.uuid4().hex, source="Agent", target=target, action=action,
                   argumentsJson=safe_text(json.dumps(safe_value(arguments or {}), ensure_ascii=False), 8192),
                   **getattr(self.context, "values", {}))
        self.write(dict(row, state="Running"))
        return row, time.monotonic()

    def finish(self, ticket, response=None, error=None):
        """上报一次完成或失败，保留开始时的身份和步骤上下文。"""
        if ticket is None:
            return
        row, started = ticket
        response = response or {}
        success = not error and bool(response.get("success"))
        cancelled = error and type(error).__name__ in ("RevCancelled", "KeyboardInterrupt", "TimeoutError")
        result = response
        # 活动查询的结果本身包含日志，不能又把整份账本复制进账本，否则递归膨胀。
        if row.get("action") == "editor.activity":
            result = {"success": response.get("success"), "total": response.get("data", {}).get("total")}
        self.write(dict(row, state="Cancelled" if cancelled else "Succeeded" if success else "Failed",
                        elapsedMs=(time.monotonic() - started) * 1000,
                        message=safe_text(str(error) if error else response.get("message", "调用完成"), 512),
                        error=type(error).__name__ if error else response.get("error", ""),
                        jobId=response.get("id", ""),
                        resultJson=safe_text(json.dumps(safe_value(result), ensure_ascii=False, default=str), 8192)))

    def write(self, row):
        """上报失败只记 last_error，业务结果和原工单不受影响。"""
        try:
            row = dict(row, utc=dt.datetime.now(dt.timezone.utc).isoformat(), schema=1)
            for key in ("source", "action", "plan", "phase", "step", "message", "error", "intent", "description"):
                if key in row:
                    row[key] = safe_text(row[key], 512)
            self.publish(row)
            self.last_error = ""
        except (OSError, RevError, ValueError, TypeError, RecursionError) as exc:
            self.last_error = str(exc)
