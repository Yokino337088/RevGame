"""给 Unity 活动窗口写旁路日志；写日志失败绝不重放或改变业务调用结果。

每个 Python 进程使用独立 JSONL 流，避免多个 Agent 争写一个文件。每行是完整
UTF-8 JSON；Unity 增量读取并按 id 合并开始/结束事件。日志不走 MCP stdout。
"""
from contextlib import contextmanager
import datetime as dt
import json
from pathlib import Path
import re
import threading
import time
import uuid


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
    """一个 Hub 一个写入器。每段最多 2 MiB，每进程保留最近 4 段；窗口最多显示 500 项。"""
    def __init__(self, directory, source="Agent"):
        self.directory = Path(directory)
        self.source = source
        self.session = uuid.uuid4().hex
        self.context = threading.local()
        self.lock = threading.Lock()
        self.segment = 0
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
        row = dict(id=uuid.uuid4().hex, source=self.source, target=target, action=action,
                   argumentsJson=safe_text(json.dumps(safe_value(arguments or {}), ensure_ascii=False), 8192),
                   **getattr(self.context, "values", {}))
        self.write(dict(row, state="Running"))
        return row, time.monotonic()

    def finish(self, ticket, response=None, error=None):
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
        """旁路写入失败只记 last_error；不能让已执行的命令因日志失败再执行一次。"""
        try:
            row = dict(row, utc=dt.datetime.now(dt.timezone.utc).isoformat(), schema=1)
            for key in ("source", "action", "plan", "phase", "step", "message", "error", "intent", "description"):
                if key in row:
                    row[key] = safe_text(row[key], 512)
            payload = json.dumps(row, ensure_ascii=False, allow_nan=False, separators=(",", ":")) + "\n"
            with self.lock:
                self.directory.mkdir(parents=True, exist_ok=True)
                path = self.directory / f"agent-{self.session}-{self.segment:04d}.jsonl"
                if path.exists() and path.stat().st_size >= 2 * 1024 * 1024:
                    self.segment += 1
                    path = self.directory / f"agent-{self.session}-{self.segment:04d}.jsonl"
                with path.open("a", encoding="utf-8", newline="\n") as stream:
                    stream.write(payload)
                old = self.directory / f"agent-{self.session}-{self.segment - 4:04d}.jsonl"
                old.unlink(missing_ok=True)
                self.last_error = ""
        except (OSError, ValueError, TypeError, RecursionError) as exc:
            self.last_error = str(exc)
