"""Atomic file RPC. Never start a second Unity process on an open project."""
from __future__ import annotations

import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

from .errors import RevCancelled, RevError  # noqa: F401  保持 `from revautomation.client import RevError` 可用

_COMMANDS_CACHE = {}


def command_specs(path: Path):
    """读取 commands.json 的动作清单，按文件修改时间缓存。

    每次 call 都重新解析一百多条 JSON 只省不了多少时间，但在高频短操作里会白白多花毫秒；
    缓存键带 mtime_ns，所以改了清单立刻生效，不会拿旧规则去校验新动作。
    """
    stamp = path.stat().st_mtime_ns
    hit = _COMMANDS_CACHE.get(path)
    if hit is not None and hit[0] == stamp:
        return hit[1]
    specs = read_json(path)["actions"]
    _COMMANDS_CACHE[path] = (stamp, specs)
    return specs


def read_json(path: Path):
    def reject_constant(value):
        raise ValueError("JSON 不接受非有限数：" + value)
    return json.loads(path.read_text(encoding="utf-8-sig"), parse_constant=reject_constant)


def read_json_settled(path: Path, attempts: int = 60, delay: float = .005):
    """等文件落定后再读，避免与写入方的原子替换撞车。

    Windows 上写入方用 temp + os.replace（Unity 侧是 File.Replace）发布结果；
    这期间读方打开同名文件会短暂拿到 PermissionError。2026-10-20 的 boss-ai 作业
    就是因为同一竞争被直接判失败。这里只重试“读”，绝不重发动作——读取失败不代表动作没执行。
    """
    for attempt in range(attempts):
        try:
            return read_json(path)
        except (PermissionError, FileNotFoundError):
            if attempt == attempts - 1:
                raise
            time.sleep(delay)
    raise RevError(f"无法读取结果文件：{path}")


def atomic_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    try:
        for attempt in range(8):
            try:
                os.replace(temp, path)
                break
            except PermissionError:
                # Windows 上短暂的读取句柄可能阻止替换；只重试文件发布，绝不重执行动作。
                if attempt == 7:
                    raise
                time.sleep(.01 * (attempt + 1))
    finally:
        temp.unlink(missing_ok=True)


def find_unity(project: Path, explicit=None):
    candidate = explicit or os.environ.get("UNITY_EDITOR")
    if candidate:
        path = Path(candidate).expanduser().resolve()
        if not path.is_file():
            raise RevError(f"Unity Editor 不存在：{path}")
        return path
    version = project_version(project)
    remembered = read_remembered_editor(project)
    if remembered and (not version or remembered.get("version") in (None, version)):
        path = Path(remembered.get("path", ""))
        if path.is_file():
            return path
    installed = hub_editors()
    matches = sorted({path for found, path in installed if found == version}) if version else sorted({path for _, path in installed})
    if len(matches) == 1:
        return matches[0]
    detail = f"工程版本 {version}" if version else "工程缺少 ProjectVersion.txt"
    known = sorted({found for found, _ in installed if found})
    raise RevError(f"无法确定 Unity 编辑器（{detail}，已知安装：{', '.join(known) or '无'}）。请用 --unity <Unity.exe> 或 UNITY_EDITOR 指定，避免猜错版本。")


def project_version(project: Path):
    version_file = Path(project) / "ProjectSettings/ProjectVersion.txt"
    if version_file.exists():
        for line in version_file.read_text(encoding="utf-8").splitlines():
            if line.startswith("m_EditorVersion:"):
                return line.split(":", 1)[1].strip()
    return None


def hub_editors():
    """返回 [(版本, Unity 可执行文件)]。

    只搜默认目录不够：Unity 可以装在任意盘，Hub 会把位置记在 editors-v2.json 与 secondaryInstallPath.json 里。
    """
    found = []
    appdata = os.environ.get("APPDATA")
    hub = Path(appdata) / "UnityHub" if appdata else Path.home() / ".config/UnityHub"
    for name in ("editors-v2.json", "editors.json"):
        try:
            value = read_json(hub / name)
        except (OSError, ValueError):
            continue
        rows = value.get("data") if isinstance(value, dict) and isinstance(value.get("data"), list) else list(value.values()) if isinstance(value, dict) else []
        for row in rows:
            if not isinstance(row, dict):
                continue
            locations = row.get("location")
            for location in locations if isinstance(locations, list) else [locations]:
                if isinstance(location, str) and Path(location).is_file():
                    found.append((row.get("version"), Path(location)))
    roots = [Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity/Hub/Editor", Path("/Applications/Unity/Hub/Editor"), Path.home() / "Unity/Hub/Editor"]
    try:
        secondary = read_json(hub / "secondaryInstallPath.json")
        if isinstance(secondary, str) and secondary:
            roots.append(Path(secondary))
    except (OSError, ValueError):
        pass
    for root in roots:
        if root.is_dir():
            for relative in ("Editor/Unity.exe", "Unity.app/Contents/MacOS/Unity", "Editor/Unity"):
                found.extend((path.parts[-(len(Path(relative).parts) + 1)], path) for path in root.glob("*/" + relative))
    return found


def _editor_memo(project: Path) -> Path:
    return Path(project) / "Library/RevolutionAutomation/editor-path.json"


def read_remembered_editor(project: Path):
    try:
        value = read_json(_editor_memo(project))
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def remember_editor(project: Path, path, version=None):
    """记住这次真实连上的编辑器。Library 不入库、重启不丢，下次 connect 不用再猜。"""
    memo = _editor_memo(project)
    memo.parent.mkdir(parents=True, exist_ok=True)
    atomic_json(memo, dict(path=str(Path(path)), version=version))


def stale_lock(project: Path):
    """判断 Temp/UnityLockfile：True=残留（没人持有），False=真有 Unity 占用，None=没有锁文件或无法判断。

    断电或强杀后 Unity 来不及删除这个文件，只看"文件是否存在"就会把重启后的工程误判成"已打开"。
    Windows 上 Unity 独占打开它，所以能否打开才是真实依据。
    """
    path = Path(project) / "Temp/UnityLockfile"
    if not path.exists() or os.name != "nt":
        return None
    try:
        handle = os.open(path, os.O_RDWR)
    except PermissionError:
        return False
    except OSError:
        return None
    os.close(handle)
    return True


class RevClient:
    def __init__(self, project: Path, mode="auto", unity=None, timeout=120.0, graphics=True):
        self.project = Path(project).resolve()
        if mode not in ("auto", "live", "batch") or not 0 < timeout < 604800:
            raise RevError("mode 必须为 auto/live/batch，timeout 必须在 0..604800 秒之间。")
        if not (self.project / "Assets").is_dir() or not (self.project / "ProjectSettings").is_dir():
            raise RevError(f"不是 Unity 工程：{self.project}")
        self.root = self.project / "Library/RevolutionAutomation"
        self.mode, self.unity, self.timeout, self.graphics = mode, unity, timeout, graphics

    def endpoint(self):
        try:
            value = read_json_settled(self.root / "endpoint.json", attempts=3, delay=.002)
            if not isinstance(value, dict) or Path(value.get("project", "")).resolve() != self.project:
                return None
            # Long builds block Unity's main thread; a stale heartbeat is not permission
            # to launch another editor. PID and UnityLockfile are checked below too.
            fresh = isinstance(value, dict) and value.get("protocol") == 1 and -5 <= time.time() - value["heartbeatUnix"] < 15
            # 心跳新鲜不代表 Unity 还活着：被强杀或崩溃后的 15 秒内心跳仍算新鲜，launch 会把死进程当成"已打开"
            # 然后一直等到超时。锁没人持有就一定没有 Unity 在用这个工程，比心跳可靠。
            return value if fresh and stale_lock(self.project) is not True else None
        except (OSError, ValueError, KeyError, TypeError):
            return None

    def call(self, action, payload=None, confirm=False, progress=None, cancel=None):
        if cancel is not None and cancel.is_set():
            raise RevCancelled("请求在提交前已取消，未执行任何动作")
        if action == "editor.wait":
            return self.wait_ready()
        request_id = uuid.uuid4().hex
        mode = self.mode
        endpoint = self.ready_endpoint(cancel)
        if mode == "auto":
            mode = "live" if endpoint else "batch"
        if mode == "live" and not endpoint:
            raise RevError("没有在线 Unity 桥接。先打开工程等待编译；长时间打包时请等待当前操作完成。")
        if endpoint and endpoint.get("batchMode"):
            raise RevError("当前桥接属于正在运行的 batch Unity，请等待其退出。")
        specs = command_specs(Path(__file__).resolve().parents[1] / "commands.json")
        spec = next((item for item in specs if item["id"] == action), {})
        if mode == "batch" and (spec.get("environment") in ("live", "play") or action.startswith("gm.")):
            raise RevError("此操作需要图形编辑器。先执行 editor launch，再执行 editor play。")
        if mode == "batch" and (endpoint or self._editor_open()):
            raise RevError("工程已被 Unity 占用：请使用 --mode live，或先关闭该工程。")
        request = dict(protocol=1, id=request_id, action=action, payloadJson=json.dumps(payload or {}, ensure_ascii=False, allow_nan=False),
                       confirm=confirm, sessionId=endpoint["sessionId"] if endpoint else "", timeoutSeconds=self.timeout,
                       deadlineUtc=(dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=self.timeout)).isoformat())
        work = self.root / "jobs" / request_id
        work.mkdir(parents=True, exist_ok=False)
        request_path, response_path = work / "request.json", work / "response.json"
        process = None
        atomic_json(request_path, request)
        try:
            if cancel is not None and cancel.is_set():
                raise RevCancelled("请求发布前已取消，未提交到编辑器")
            if mode == "live":
                atomic_json(self.root / "inbox" / (request_id + ".json"), request)
            else:
                editor = find_unity(self.project, self.unity)
                args = [str(editor), "-batchmode", "-projectPath", str(self.project), "-executeMethod",
                        "Revolution.Editor.RevAutomationBatch.Execute", "-revAutomationRequest", str(request_path),
                        "-logFile", str(work / "unity.log")]
                if not self.graphics:
                    args.append("-nographics")
                process = subprocess.Popen(args, cwd=self.project, shell=False)
            end, last_progress = time.monotonic() + self.timeout, None
            # 起步 5ms、稳定后 20ms、长操作退到 50ms：本地文件桥通常几十毫秒就出结果，
            # 固定 80ms 轮询会让每个短操作都白等一轮。
            started, cadence = time.monotonic(), .005
            while not response_path.exists():
                if cancel is not None and cancel.is_set():
                    raise RevCancelled(f"请求取消：{request_id}，请检查工单已发生的副作用：{work}")
                if process is not None and process.poll() is not None:
                    if response_path.exists():
                        break
                    raise RevError(f"Unity 提前退出（{process.returncode}），请看 {work / 'unity.log'}")
                if time.monotonic() >= end:
                    raise TimeoutError(f"等待超时；请求 {request_id}，日志目录 {work}")
                if progress and (work / "progress.json").exists():
                    try:
                        update = read_json(work / "progress.json")
                        if update != last_progress:
                            progress(update)
                            last_progress = update
                    except (OSError, ValueError):
                        pass
                time.sleep(cadence)
                if cadence < .02:
                    cadence = .02
                elif time.monotonic() - started > .3:
                    cadence = .05
            response = read_json_settled(response_path)
            if response.get("id") != request_id or response.get("protocol") != 1:
                raise RevError("Unity 返回了不匹配的协议或请求 ID。")
            if process is not None:
                try:
                    process.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    raise RevError(f"操作已返回，但 batch Unity 未退出。PID={process.pid}，请看 {work / 'unity.log'}")
                if process.returncode != (0 if response.get("success") else 1):
                    raise RevError(f"Unity 退出码与响应不一致：{process.returncode}，{work}")
            response["jobDirectory"] = str(work)
            if response.get("dataJson"):
                response["data"] = json.loads(response.pop("dataJson"))
            return response
        except (TimeoutError, KeyboardInterrupt, RevCancelled):
            (work / "cancel").touch()
            # Only a child editor started by this command may be terminated. Never kill
            # the user's already-open Unity. In live mode a blocked GM needs manual diagnosis.
            if process is not None and process.poll() is None:
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.terminate()
            raise

    def batch(self, steps, stop_on_error=True, confirm=False):
        """一次请求执行多个动作，省掉"每步一次往返"。

        批量减少编辑器 tick 往返；完整 MCP 链路还需避免重复发现与无关证据生成。
        本方法不缓存业务结果，也不把跨帧操作压成同帧执行。
        steps 形如 [{"action": "editor.status"}, {"action": "ab.config"}]，
        每步可单独带 payloadJson 与 confirm；默认遇错即停，避免后续步骤依赖错误状态。
        """
        if not isinstance(steps, list) or not steps:
            raise RevError("batch 需要非空步骤数组")
        return self.call("batch.call", {"steps": steps, "stopOnError": stop_on_error}, confirm=confirm)

    def launch(self):
        """Open a graphical editor and wait for compilation/bridge; never terminate it."""
        endpoint = self.endpoint()
        if endpoint and not endpoint.get("batchMode"):
            return dict(self.wait_ready(), alreadyOpen=True)
        if self._editor_open():
            raise RevError("工程已打开但桥接未就绪。请先处理 Unity 编译错误，或等待当前操作完成。")
        editor = find_unity(self.project, self.unity)
        self.root.mkdir(parents=True, exist_ok=True)
        log = self.root / ("launch-" + uuid.uuid4().hex + ".log")
        process = subprocess.Popen([str(editor), "-projectPath", str(self.project), "-logFile", str(log)], cwd=self.project, shell=False)
        end = time.monotonic() + self.timeout
        while time.monotonic() < end:
            endpoint = self.endpoint()
            if endpoint and not endpoint.get("batchMode"):
                remaining = end - time.monotonic()
                original = self.timeout
                self.timeout = max(.01, remaining)
                try:
                    return dict(self.wait_ready(), logPath=str(log))
                finally:
                    self.timeout = original
            if process.poll() is not None:
                raise RevError(f"Unity 启动退出（{process.returncode}），请看 {log}")
            time.sleep(.25)
        raise TimeoutError(f"Unity 桥接尚未就绪；编辑器保留运行，PID={process.pid}，日志 {log}")

    def wait_ready(self):
        """Compilation/import barrier, also across the bridge's domain reload."""
        if self.mode == "batch":
            raise RevError("editor.wait 需要在线编辑器。batch 请求由子编辑器自行完成编译。")
        end, stable, session = time.monotonic() + self.timeout, None, None
        first_heartbeat = None
        gone_since = None
        while time.monotonic() < end:
            endpoint = self.endpoint()
            if endpoint is None and not self._editor_open():
                # 没有心跳、也没有 Unity 占着工程：它已经退出了，等多久都不会好。留几秒宽限给刚启动的编辑器。
                gone_since = gone_since or time.monotonic()
                if time.monotonic() - gone_since > 5:
                    raise RevError("Unity 没有在运行（工程未被打开或已退出）；用 connect 重新打开。")
            else:
                gone_since = None
            ready = endpoint and not any(endpoint.get(k) for k in ("batchMode", "compiling", "updating", "activeRequest"))
            if ready:
                ready_key = (endpoint.get("sessionId"), endpoint.get("domainId"))
                if stable is None or session != ready_key:
                    stable, session = time.monotonic(), ready_key
                    first_heartbeat = endpoint.get("heartbeatUnix")
                # 至少跨过两个心跳，避免 refresh 回执早于实际开始编译。
                if time.monotonic() - stable >= 2 and endpoint.get("heartbeatUnix", 0) > first_heartbeat:
                    if endpoint.get("compilationFailed"):
                        raise RevError(f"Unity 脚本编译失败；读取 editor.console/editor.compilation 或 {self.root / 'compilation.json'}")
                    return dict(success=True, endpoint=endpoint, message="资源导入与脚本编译已稳定就绪")
            else:
                stable = None
            time.sleep(.1)
        raise TimeoutError(f"等待 Unity 就绪超时；请检查 {self.root / 'endpoint.json'} 与 editor.console")

    def job(self, request_id, cancel=False):
        if len(request_id) != 32 or any(c not in "0123456789abcdefABCDEF" for c in request_id):
            raise RevError("请求 ID 必须是 32 位十六进制。")
        work = self.root / "jobs" / request_id
        if not (work / "request.json").is_file():
            raise RevError(f"请求不存在：{request_id}")
        if cancel and not (work / "response.json").exists():
            (work / "cancel").touch()
        result = dict(success=True, jobDirectory=str(work), cancelRequested=(work / "cancel").exists())
        for name in ("response", "progress", "scenario-result"):
            if (work / (name + ".json")).exists():
                result[name] = read_json(work / (name + ".json"))
        return result

    def ready_endpoint(self, cancel=None):
        """长构建回执可能先于下一次心跳；只等待发现，不重发任何业务请求。"""
        endpoint = self.endpoint()
        if endpoint or self.mode == "batch" or not self._editor_open():
            return endpoint
        deadline = time.monotonic() + min(self.timeout, 15)
        while time.monotonic() < deadline:
            if cancel is not None and cancel.is_set():
                raise RevCancelled("等待编辑器恢复心跳时取消，未提交新动作")
            time.sleep(.1)
            endpoint = self.endpoint()
            if endpoint:
                return endpoint
        return None

    def _editor_open(self):
        if os.name == "nt":
            # Windows 上独占锁是唯一可靠依据：重启后 endpoint.json 里的 PID 可能已被别的进程复用。
            return stale_lock(self.project) is False
        if (self.project / "Temp/UnityLockfile").exists():
            return True
        # A persisted endpoint can survive a crash. Check that PID before treating it as live.
        try:
            pid = int(read_json(self.root / "endpoint.json")["pid"])
            if os.name == "nt":
                import ctypes
                from ctypes import wintypes
                kernel = ctypes.WinDLL("kernel32", use_last_error=True)
                kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
                kernel.OpenProcess.restype = wintypes.HANDLE
                kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
                handle = kernel.OpenProcess(0x1000, False, pid)
                if handle:
                    kernel.CloseHandle(handle)
                    return True
            else:
                try:
                    os.kill(pid, 0)
                except PermissionError:
                    return True  # EPERM：进程存在，只是属于别的用户
                return True
        except (OSError, ValueError, KeyError):
            pass
        return False
