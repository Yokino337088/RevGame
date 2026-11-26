"""Unity Editor 本机调用，优先走内存，兼容旧 Editor 和批处理的临时文件桥。"""
from __future__ import annotations

import datetime as dt
import json
import os
import re
from pathlib import Path
import subprocess
import time
import uuid

from .errors import RevCancelled, RevError  # noqa: F401  保持 `from revautomation.client import RevError` 可用
from .errors import RevCompilationError

_COMMANDS_CACHE = {}
# 文件桥控制文件和 batch 日志由调用方回收。动作专用文件另按身份清理。
_FILE_JOB_FILES = ("request.json", "response.json", "response.json.tmp",
                   "progress.json", "progress.json.tmp", "cancel", "target-started", "unity.log")
# 旧 scenario 逐轮落盘来限制内存占用。完成后先做显式导出，再清理这些中间文件。
_SCENARIO_FILES = ("scenario-result.json", "scenario-result.json.tmp", "iterations.jsonl", "unity-errors.jsonl",
                   "progress.json", "progress.json.tmp")
# 文件桥只检查 cancel 是否存在。此内容让终态查询知道原调用已经交出清理权。
_RELEASED_JOB = "client-released"


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
    """原子发布 JSON，临时写入或替换失败时回收本次创建的临时文件。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temp.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
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


def unlink_job_file(path):
    """直接删除已消费的临时文件，短暂遇到 Windows 占用时重试删除。"""
    for attempt in range(8):
        try:
            path.unlink(missing_ok=True)
            return
        except PermissionError:
            if attempt == 7:
                raise
            time.sleep(.01 * (attempt + 1))


def read_job_release(path):
    """只有原调用主动交出清理权后，旁路查询才能回收终态文件。"""
    try:
        return path.read_bytes() == _RELEASED_JOB.encode("ascii")
    except OSError:
        return False


def clean_job_files(work, names):
    """只清理本任务已知的文件。保留仍有其他输出的目录，并回报清理失败。"""
    from .storage import linked
    try:
        if not work.is_dir():
            return None
        if linked(work) or linked(work.parent) or work.resolve() != work.parent.resolve() / work.name:
            raise ValueError("临时工单目录指向其他位置")
        for name in names:
            unlink_job_file(work / name)
        if not any(work.iterdir()):
            work.rmdir()
    except FileNotFoundError:
        return None  # 重复收尾时目录已清理，也属于成功。
    except (OSError, ValueError) as error:
        return str(error)
    return None


def consume_scenario(response, work, report_dir=None):
    """在删除 scenario 中间文件前同步导出用户要求的报告，结果字段继续随响应返回。"""
    source = work / "scenario-result.json"
    data = response.get("data")
    try:
        if report_dir is not None:
            from .report import export_report
            if not source.is_file() or source.resolve() != work.parent.resolve() / work.name / source.name:
                raise ValueError("scenario 没有产出完整结果，可查看本次业务响应")
            response["reports"] = export_report(source, report_dir)
    except (OSError, ValueError) as error:
        response["reportError"] = str(error)
    finally:
        # 场景已结束，Unity 不会再追加逐轮数据。避免返回已删除文件的路径。
        cleanup_error = clean_job_files(work, _SCENARIO_FILES)
        if cleanup_error:
            response["jobCleanupError"] = cleanup_error
        elif isinstance(data, dict):
            data.pop("resultPath", None)
            data.pop("unityLogPath", None)
        if not work.is_dir():
            response.pop("jobDirectory", None)
    return response


def consume_editor_image(response, work, action):
    """领取工具生成的临时 PNG 并删除文件，调用者只收到本次图像。"""
    from .media import encode_capture, MAX_PNG_BYTES
    from .storage import linked

    image = response.get("data")
    if not isinstance(image, dict) or not isinstance(image.get("path"), str):
        return
    source = Path(image["path"])
    if action == "scene.capture":
        name = source.name
        if name != "scene-capture.png" and not re.fullmatch(r"scene-capture-[0-9a-f]{32}\.png", name):
            response.update(success=False, error="CaptureReadFailed", message="场景截图文件名不属于本次工单")
            return
        expected = work / name
    else:
        capture = image.get("captureId")
        if not isinstance(capture, str) or not re.fullmatch(r"[0-9a-f]{32}", capture):
            return  # 兼容仍返回旧式截图路径的 Player。
        expected = work.parent.parent / "screenshots" / ("rev-shot-" + capture + ".png")
    try:
        if source != expected or not source.is_file() or any(linked(path) for path in (source, source.parent, source.parent.parent)):
            raise ValueError("临时截图位置不属于本次工单或已过期")
    except (OSError, ValueError) as error:
        response.update(success=False, error="CaptureReadFailed", message=str(error))
        return
    try:
        with source.open("rb") as stream:
            encode_capture(image, stream.read(MAX_PNG_BYTES + 1))
    except (OSError, ValueError) as error:
        response.update(success=False, error="CaptureReadFailed", message=str(error))
    finally:
        try:
            unlink_job_file(source)
        except OSError as error:
            response["jobCleanupError"] = str(error)
        else:
            image.pop("path", None)


def batch_log_tail(path):
    """失败时读出 batch 日志末尾 8 KiB，随后照常删除临时日志。"""
    try:
        with path.open("rb") as stream:
            stream.seek(max(0, path.stat().st_size - 8192))
            return stream.read(8192).decode("utf-8", errors="replace")
    except OSError:
        return ""


def consume_editor_result(response, work, action, report_dir=None):
    """领取终态后清理动作专用的临时文件，跨域重载期间由 Unity 继续使用。"""
    if action == "scenario.run":
        return consume_scenario(response, work, report_dir)
    if action in ("scene.capture", "runtime.screenshot", "runtime.call") and response.get("success"):
        consume_editor_image(response, work, action)
    data = response.get("data")
    names, field, scratch = (), None, work
    if action == "batch.call" and isinstance(data, dict):
        # 同一批可能有多张图，每一张先交付给调用方，再永久删除对应临时文件。
        for step in data.get("results", []):
            if step.get("action") == "scene.capture" and step.get("dataJson"):
                image_result = {"data": json.loads(step["dataJson"]), "success": step.get("success")}
                consume_editor_image(image_result, work, "scene.capture")
                step["dataJson"] = json.dumps(image_result["data"], ensure_ascii=False)
                error = image_result.get("jobCleanupError") or image_result.get("message")
                if error:
                    step["captureError"] = error
    if action == "build.target":
        names = ("target-started",)
    elif action == "build.player":
        names, field = ("build-result.json", "build-result.json.tmp"), "reportPath"
    elif action in ("test.run", "test.result") and isinstance(data, dict) and data.get("finished") is True:
        test_id = data.get("id")
        if isinstance(test_id, str) and re.fullmatch(r"[a-f0-9]{32}", test_id):
            scratch = work.parent / test_id
            names, field = ("tests-started", "tests-result.json", "tests-result.json.tmp"), "resultPath"
    if names or action in ("scene.capture", "batch.call"):
        cleanup_error = clean_job_files(scratch, names)
        if cleanup_error:
            response["jobCleanupError"] = cleanup_error
        elif field and isinstance(data, dict):
            data.pop(field, None)
    if not work.is_dir():
        response.pop("jobDirectory", None)
    return response


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
    """把单次动作提交到指定工程，承担文件桥临时文件和后台回执的消费边界。"""
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

    def call(self, action, payload=None, confirm=False, progress=None, cancel=None, *, detach=False, report_dir=None):
        """返回终态，或在显式 detach 时返回接受回执。scenario 可在清理前同步导出。"""
        if report_dir is not None:
            from .storage import check_revmcp_output_path
            report_dir = check_revmcp_output_path(report_dir)
            if action != "scenario.run" or detach or report_dir.is_relative_to((self.root / "jobs").resolve()):
                raise RevError("报告目录仅供同步 scenario.run 使用，且必须在临时工单目录之外。")
        if cancel is not None and cancel.is_set():
            raise RevCancelled("请求在提交前已取消，未执行任何动作")
        if action == "editor.wait":
            if detach:
                raise RevError("editor.wait 是 CLI 本地等待，不能提交后台工单。")
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
        if detach and (mode != "live" or not (endpoint.get("memoryRequired") or endpoint.get("memoryUrl"))):
            raise RevError("--detach 需要带本机内存桥的在线 Editor。")
        if mode == "batch":
            specs = command_specs(Path(__file__).resolve().parents[1] / "commands.json")
            spec = next((item for item in specs if item["id"] == action), {})
            if spec.get("environment") in ("live", "play") or action.startswith("gm."):
                raise RevError("此操作需要图形编辑器。先执行 editor launch，再执行 editor play。")
        if mode == "batch" and (endpoint or self._editor_open()):
            raise RevError("工程已被 Unity 占用：请使用 --mode live，或先关闭该工程。")
        request = dict(protocol=1, id=request_id, action=action, payloadJson=json.dumps(payload or {}, ensure_ascii=False, allow_nan=False),
                       confirm=confirm, sessionId=endpoint["sessionId"] if endpoint else "", timeoutSeconds=self.timeout,
                       deadlineUtc=(dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=self.timeout)).isoformat())
        if mode == "live" and (endpoint.get("memoryRequired") or endpoint.get("memoryUrl")):
            from .memory_transport import call as memory_call
            response = memory_call(self, request, cancel, detach=detach)
            return response if detach else consume_editor_result(response, self.root / "jobs" / request_id, action, report_dir)
        work = self.root / "jobs" / request_id
        work.mkdir(parents=True, exist_ok=False)
        request_path, response_path = work / "request.json", work / "response.json"
        process, response = None, None
        submitted, completed = False, False
        try:
            atomic_json(request_path, request)
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
            submitted = True
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
                    detail = batch_log_tail(work / "unity.log")
                    raise RevError(f"Unity 提前退出（{process.returncode}）。\n{detail}")
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
                completed = True
                if process.returncode != (0 if response.get("success") else 1):
                    detail = batch_log_tail(work / "unity.log")
                    raise RevError(f"Unity 退出码与响应不一致：{process.returncode}。\n{detail}")
            completed = True
            response["jobDirectory"] = str(work)
            if response.get("dataJson"):
                response["data"] = json.loads(response.pop("dataJson"))
            if process is not None and not response.get("success"):
                response["unityLogTail"] = batch_log_tail(work / "unity.log")
            return consume_editor_result(response, work, action, report_dir)
        except (TimeoutError, KeyboardInterrupt, RevCancelled):
            # 已发布且尚未完成的请求仍可能由 Unity 使用，写取消信号后留给终态查询收尾。
            if submitted and not completed:
                (work / "cancel").write_text(_RELEASED_JOB, encoding="ascii")
            # Only a child editor started by this command may be terminated. Never kill
            # the user's already-open Unity. In live mode a blocked GM needs manual diagnosis.
            if process is not None and process.poll() is None:
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.terminate()
            raise
        finally:
            # 无消费者或已确认结束时才能删除控制文件，避免等到一半破坏正在运行的动作。
            if not submitted or completed or (process is not None and process.poll() is not None):
                cleanup_error = clean_job_files(work, _FILE_JOB_FILES)
                if response is not None and completed:
                    if not work.is_dir():
                        response.pop("jobDirectory", None)
                    if cleanup_error:
                        response["jobCleanupError"] = cleanup_error

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
        """等导入与脚本域重载稳定。编译失败时保留当前进程的真实诊断。"""
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
                        raise RevCompilationError(self._failed_compilation(endpoint))
                    return dict(success=True, endpoint=endpoint, message="资源导入与脚本编译已稳定就绪")
            else:
                stable = None
            time.sleep(.1)
        raise TimeoutError(f"等待 Unity 就绪超时；请检查 {self.root / 'endpoint.json'} 与 editor.console")

    def _failed_compilation(self, endpoint):
        """仅交付当前编辑器会话完成的失败快照，避免上次启动的错误混入本次结果。"""
        session_id = endpoint.get("sessionId")
        if not session_id:
            return None
        try:
            result = read_json_settled(self.root / "compilation.json")
        except (OSError, ValueError):
            return None
        if (isinstance(result, dict) and result.get("sessionId") == session_id
                and result.get("finished") is True and result.get("compiling") is False
                and result.get("failed") is True and isinstance(result.get("messages"), list)):
            return result
        return None

    def job(self, request_id, cancel=False, wait=False):
        """查询或取消后台请求。领取终态时回收自己负责的临时文件。"""
        if len(request_id) != 32 or any(c not in "0123456789abcdefABCDEF" for c in request_id):
            raise RevError("请求 ID 必须是 32 位十六进制。")
        from .memory_transport import detached_job, receipt_path
        if receipt_path(self, request_id).is_file():
            result = detached_job(self, request_id, cancel, wait)
            if result.get("pending"):
                return result
            return consume_editor_result(result, self.root / "jobs" / request_id, result.get("action"))
        work = self.root / "jobs" / request_id
        if not (work / "request.json").is_file():
            raise RevError(f"请求不存在：{request_id}")
        if cancel and not (work / "response.json").exists():
            (work / "cancel").touch()
        if wait:
            deadline = time.monotonic() + self.timeout
            while not (work / "response.json").is_file():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return dict(success=False, error="WaitTimeout", id=request_id, pending=True, jobDirectory=str(work))
                time.sleep(min(.2, remaining))
        result = dict(success=True, jobDirectory=str(work), cancelRequested=(work / "cancel").exists())
        for name in ("response", "progress", "scenario-result"):
            if (work / (name + ".json")).exists():
                result[name] = read_json_settled(work / (name + ".json"))
        # 正常同步调用负责清理自己的文件。仅在调用方已放弃等待时由查询方接手，
        # 避免观察同一个请求的客户端抢先删掉同步调用还未读到的响应。
        response = result.get("response")
        cancel_file = work / "cancel"
        if (response and response.get("id") == request_id and response.get("protocol") == 1
                and result["cancelRequested"] and read_job_release(cancel_file)):
            action = read_json_settled(work / "request.json").get("action")
            if response.get("dataJson"):
                response["data"] = json.loads(response.pop("dataJson"))
            consume_editor_result(response, work, action)
            names = _FILE_JOB_FILES + _SCENARIO_FILES if "scenario-result" in result else _FILE_JOB_FILES
            cleanup_error = clean_job_files(work, names)
            if cleanup_error:
                result["jobCleanupError"] = cleanup_error
            else:
                if not work.is_dir():
                    result.pop("jobDirectory", None)
                if "scenario-result" in result:
                    result["scenario-result"].pop("resultPath", None)
                    result["scenario-result"].pop("unityLogPath", None)
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
