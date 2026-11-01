"""常驻服务端：一个工程一个进程，绑定 127.0.0.1，随机端口 + 随机 token。

设计要点（都是为了不把本机自动化接口变成别人的入口）：
- 只监听回环地址；校验 Host（防 DNS rebinding）；带 Origin 的请求一律拒绝（浏览器页面不能驱动它）；
- 所有接口都要 Bearer token，token 只写入工程 Library 下被忽略的文件，不打印到输出里；
- 写 Unity 的调用串行执行（一把锁）：文件桥本来就是单工单，并发只会让失败更难定位；
- 请求体 ≤ 1MiB；空闲超时自动退出，避免遗忘在后台。
"""
from __future__ import annotations

import hmac
import io
import json
import os
from contextlib import redirect_stderr, redirect_stdout
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import secrets
import threading
import time

from .client import RevClient, RevError, atomic_json
from .daemon import ENDPOINT_NAME, HEADERS_NAME, MAX_BODY, PROTOCOL, daemon_dir, read_endpoint, status
from .daemon_link import STALE_STATUS, code_stamp


class _BadRequest(RevError):
    pass


RUN_QUEUE_SECONDS = 10.0


def default_call(project, request):
    """/call 的默认执行器：校验参数 → 走和命令行完全相同的 invoke_action。"""
    from . import cli
    payload = request["arguments"]
    cli.validate_action(request["action"], payload)
    client = RevClient(project, "live", None, request["timeout"], True)
    return cli.invoke_action(client, request["action"], payload, request["confirm"], progress=None)


def default_run(project, argv, cwd):
    """/run 的默认执行器：在服务进程里执行一条完整命令行，返回 (退出码, stdout, stderr)。

    调用方串行（持有锁），所以临时切换 cwd 与重定向标准流是安全的。
    """
    from . import cli
    from . import daemon as admin
    out, err = io.StringIO(), io.StringIO()
    previous = os.getcwd()
    code = 2
    try:
        os.chdir(cwd)
        with redirect_stdout(out), redirect_stderr(err):
            try:
                parsed = cli.parser().parse_args(argv)
                if Path(parsed.project).resolve() != Path(project).resolve():
                    print(json.dumps(dict(success=False, error="ProjectMismatch",
                                          message="常驻服务绑定的是另一个工程；请对该工程单独启动。"), ensure_ascii=False), file=err)
                    code = 2
                elif not admin.should_forward(parsed):
                    print(json.dumps(dict(success=False, error="NotForwardable",
                                          message="该命令必须在调用方进程执行（本地专属、batch、拉起编辑器或 --no-daemon）。"), ensure_ascii=False), file=err)
                    code = 2
                else:
                    code = cli.main(["--no-daemon", *argv])
            except SystemExit as exc:
                code = exc.code if isinstance(exc.code, int) else 2
            except Exception as exc:  # noqa: BLE001 - 执行中途的意外：动作可能已经生效，只能报结果未知
                print(json.dumps(dict(success=False, error="ResultUnknown", message=f"执行中出现意外错误，动作可能已生效，结果未知；不要重放，请读回确认：{type(exc).__name__}: {exc}"),
                                 ensure_ascii=False), file=err)
                code = 1
    finally:
        os.chdir(previous)
    return code, out.getvalue(), err.getvalue()


class RevDaemon:
    def __init__(self, project, idle_timeout=1800.0, port=0, call_executor=None, run_executor=None):
        self.project = Path(project).resolve()
        if not (self.project / "Assets").is_dir() or not (self.project / "ProjectSettings").is_dir():
            raise RevError(f"不是 Unity 工程：{self.project}")
        if idle_timeout < 0 or not 0 <= port < 65536:
            raise RevError("idle-timeout 必须 >= 0，port 必须在 0..65535。")
        self.idle_timeout = float(idle_timeout)
        self.token = secrets.token_urlsafe(32)
        self.lock = threading.Lock()
        self.started = time.time()
        self.last_activity = time.monotonic()
        self.calls = 0
        self.current = None
        self.call_executor = call_executor or default_call
        self.run_executor = run_executor or default_run
        self.stamp = code_stamp()
        self.httpd = ThreadingHTTPServer(("127.0.0.1", port), _build_handler(self))
        self.httpd.daemon_threads = True
        self.port = self.httpd.server_address[1]
        self._closed = threading.Event()

    # --- 生命周期 ---------------------------------------------------------
    def publish(self):
        root = daemon_dir(self.project)
        root.mkdir(parents=True, exist_ok=True)
        atomic_json(root / ENDPOINT_NAME, dict(protocol=PROTOCOL, pid=os.getpid(), port=self.port, token=self.token,
                                              project=str(self.project), startedUnix=self.started))
        header = root / HEADERS_NAME
        temp = header.with_name(header.name + ".tmp")
        # curl 的 `-H @文件` 直接读这个头文件，调用方不需要自己解析 JSON 取 token。
        temp.write_text("Authorization: Bearer " + self.token + "\n", encoding="ascii")
        os.replace(temp, header)

    def unpublish(self):
        current = read_endpoint(self.project)
        if current is not None and current.get("token") != self.token:
            return  # 已经被新服务接管，别删别人的文件
        root = daemon_dir(self.project)
        for name in (ENDPOINT_NAME, HEADERS_NAME):
            try:
                (root / name).unlink()
            except OSError:
                pass

    def serve_forever(self):
        self.publish()
        watchdog = None
        if self.idle_timeout > 0:
            watchdog = threading.Thread(target=self._watch_idle, daemon=True)
            watchdog.start()
        try:
            self.httpd.serve_forever(poll_interval=.2)
        finally:
            self._closed.set()
            self.unpublish()
            self.httpd.server_close()

    def shutdown(self):
        threading.Thread(target=self.httpd.shutdown, daemon=True).start()

    def _watch_idle(self):
        while not self._closed.wait(1.0):
            if self.current is None and time.monotonic() - self.last_activity > self.idle_timeout:
                self.shutdown()
                return

    # --- 执行 -------------------------------------------------------------
    def health(self):
        busy = self.current
        return dict(success=True, pid=os.getpid(), project=str(self.project), port=self.port,
                    uptimeSeconds=round(time.time() - self.started, 1), calls=self.calls,
                    busy=busy is not None, current=busy[0] if busy else None,
                    idleSeconds=round(time.monotonic() - self.last_activity, 1), idleTimeoutSeconds=self.idle_timeout,
                    stale=code_stamp() != self.stamp)

    def execute(self, label, wait_seconds, work):
        """串行执行一次调用，返回 (HTTP状态, JSON)。"""
        if code_stamp() != self.stamp:
            # 代码或清单已更新：拒绝执行并退出，调用方转本地执行，下次 connect 会起新服务。
            self.shutdown()
            return STALE_STATUS, dict(success=False, error="DaemonStale", message="CLI 已更新，常驻服务退出；请重新 connect。")
        if not self.lock.acquire(timeout=max(0.1, wait_seconds)):
            return 503, dict(success=False, error="Busy", message="常驻服务正在执行另一条命令（%s），本次没有执行；等它结束后重试，或加 --no-daemon。" % (self.current and self.current[0]))
        self.current = (label, time.time())
        self.last_activity = time.monotonic()
        try:
            return 200, work()
        except TimeoutError as exc:
            return 504, dict(success=False, error="TimeoutError", message=str(exc))
        except (RevError, ValueError, OSError) as exc:
            return 400, dict(success=False, error=type(exc).__name__, message=str(exc))
        except Exception as exc:  # noqa: BLE001 - 服务不能因为一次异常退出
            return 500, dict(success=False, error="InternalError", message=f"{type(exc).__name__}: {exc}")
        finally:
            self.calls += 1
            self.current = None
            self.last_activity = time.monotonic()
            self.lock.release()


def _number(value, name):
    if type(value) not in (int, float) or not 0 < value < 604800:
        raise _BadRequest(f"{name} 必须是 0..604800 之间的数字。")
    return float(value)


def _build_handler(daemon):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "RevDaemon/1"

        def log_message(self, *_):
            pass

        def _send(self, status_code, value, extra=None):
            raw = json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8")
            self.send_response(status_code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(raw)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            for key, header in (extra or {}).items():
                self.send_header(key, header)
            self.end_headers()
            self.wfile.write(raw)
            self.close_connection = True

        def _allowed(self):
            host = self.headers.get("Host", "")
            if host not in (f"127.0.0.1:{daemon.port}", f"localhost:{daemon.port}") or self.headers.get("Origin") is not None:
                self._send(403, dict(success=False, error="Forbidden", message="主机或来源不被接受。"))
                return False
            supplied = self.headers.get("Authorization", "").encode("utf-8")
            if not hmac.compare_digest(supplied, ("Bearer " + daemon.token).encode("utf-8")):
                self._send(401, dict(success=False, error="Unauthorized", message="缺少或错误的 token；读取 cli-daemon.headers。"))
                return False
            return True

        def _body(self):
            try:
                length = int(self.headers.get("Content-Length", "0"))
            except ValueError as exc:
                raise _BadRequest("Content-Length 无效。") from exc
            if not 0 <= length <= MAX_BODY:
                raise _BadRequest(f"请求体必须在 0..{MAX_BODY} 字节。")
            raw = self.rfile.read(length) if length else b"{}"
            try:
                value = json.loads(raw.decode("utf-8"), parse_constant=lambda name: (_ for _ in ()).throw(ValueError(name)))
            except (UnicodeDecodeError, ValueError) as exc:
                raise _BadRequest("请求体必须是有限数字组成的 UTF-8 JSON 对象。") from exc
            if not isinstance(value, dict):
                raise _BadRequest("请求体必须是 JSON 对象。")
            return value

        def do_GET(self):
            if not self._allowed():
                return
            if self.path == "/health":
                self._send(200, daemon.health())
            else:
                self._send(404, dict(success=False, error="NotFound", message="未知路径。"))

        def do_POST(self):
            if not self._allowed():
                return
            try:
                body = self._body()
                if self.path == "/call":
                    self._call(body)
                elif self.path == "/run":
                    self._run(body)
                elif self.path == "/shutdown":
                    self._send(200, dict(success=True, message="常驻服务即将退出。"))
                    daemon.shutdown()
                else:
                    self._send(404, dict(success=False, error="NotFound", message="未知路径。"))
            except _BadRequest as exc:
                self._send(400, dict(success=False, error="BadRequest", message=str(exc)))

        def _call(self, body):
            unknown = set(body) - {"action", "arguments", "confirm", "timeout"}
            if unknown:
                raise _BadRequest("未知字段：" + ", ".join(sorted(unknown)))
            action = body.get("action")
            arguments = body.get("arguments", {})
            confirm = body.get("confirm", False)
            if not isinstance(action, str) or not action.strip():
                raise _BadRequest("action 必须是非空字符串。")
            if not isinstance(arguments, dict):
                raise _BadRequest("arguments 必须是对象。")
            if type(confirm) is not bool:
                raise _BadRequest("confirm 必须是布尔值。")
            timeout = _number(body.get("timeout", 120.0), "timeout")
            request = dict(action=action, arguments=arguments, confirm=confirm, timeout=timeout)
            code, value = daemon.execute("call " + action, timeout, lambda: daemon.call_executor(daemon.project, request))
            self._send(code, value)

        def _run(self, body):
            unknown = set(body) - {"argv", "cwd"}
            argv, cwd = body.get("argv"), body.get("cwd")
            if unknown or not isinstance(argv, list) or not all(isinstance(item, str) for item in argv):
                raise _BadRequest("argv 必须是字符串数组，且不能有未知字段。")
            if not isinstance(cwd, str) or not Path(cwd).is_dir():
                raise _BadRequest("cwd 必须是存在的目录。")

            def work():
                code, out, err = daemon.run_executor(daemon.project, argv, cwd)
                return dict(exitCode=code, stdout=out, stderr=err)

            # 排队只等一小会儿：必须远短于客户端的读超时。否则客户端先放弃，只能报"结果未知"，
            # 而这条命令其实还没开始执行。现在超时就明确回 Busy，调用方知道可以安全重试。
            code, value = daemon.execute("run", RUN_QUEUE_SECONDS, work)
            self._send(code, value)

    return Handler


def serve(project, idle_timeout=1800.0, port=0):
    project = Path(project).resolve()
    existing = status(project)
    if existing.get("running"):
        raise RevError("该工程已有常驻服务在运行（PID %s）；先执行 daemon stop。" % existing.get("pid"))
    daemon = RevDaemon(project, idle_timeout, port)
    daemon.serve_forever()
    return dict(success=True, message="常驻服务已退出。", calls=daemon.calls)
