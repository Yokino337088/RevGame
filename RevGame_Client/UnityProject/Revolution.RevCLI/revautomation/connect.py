"""关机重启后的一键恢复：环境体检 → 打开 Unity → 等桥就绪 → 起常驻服务 → 核对能力。

为什么单独成模块：重启后 AI 面对的是一堆互相依赖的状态（Python、Unity 路径、残留锁文件、
残留端点、编译是否完成、桥和 CLI 版本是否一致）。把判断顺序固定在这里，AI 只需要跑一条命令，
失败时拿到的是"哪一步、为什么、下一步做什么"，而不是一个孤零零的连接错误。

`inspect` 只读，不启动任何东西；`connect` 才会拉起 Unity 和常驻服务。
"""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys

from . import daemon
from .client import RevClient, find_unity, read_json, remember_editor, stale_lock
from .errors import RevError

ROOT = Path(__file__).resolve().parents[1]
MINIMUM_PYTHON = (3, 10)


def _step(name, ok, detail, fix=None):
    item = dict(step=name, ok=bool(ok), detail=detail)
    if fix:
        item["fix"] = fix
    return item


def _toolchain():
    """IL2CPP 打包依赖 VS C++ 工具链。Unity 按系统 ANSI 解码 vswhere 输出，中文安装路径会变成乱码。"""
    if os.name != "nt":
        return None
    vswhere = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Microsoft Visual Studio/Installer/vswhere.exe"
    if not vswhere.is_file():
        return dict(ok=None, detail="未找到 vswhere，未安装 Visual Studio；只影响 IL2CPP 打包")
    try:
        output = subprocess.run([str(vswhere), "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
                                capture_output=True, timeout=15).stdout.decode("utf-8", "replace")
    except (OSError, subprocess.SubprocessError) as exc:
        return dict(ok=None, detail=f"vswhere 无法运行：{exc}")
    paths = [line.strip() for line in output.splitlines() if line.strip()]
    if not paths:
        return dict(ok=False, detail="没有带 C++ 工具的 Visual Studio；Mono 打包不受影响，IL2CPP 会失败")
    ascii_paths = [path for path in paths if path.isascii()]
    if ascii_paths:
        return dict(ok=True, detail=f"C++ 工具链：{ascii_paths[0]}")
    return dict(ok=False, detail=f"VS 路径含非 ASCII 字符，Unity 会解码失败：{paths[0]}；Mono 打包不受影响，IL2CPP 会失败")


def bridge_installed(project: Path) -> bool:
    known = project / "Assets/Revolution/Editor/RevAutomation/RevAutomationBridge.cs"
    return known.is_file() or any((project / "Assets").rglob("RevAutomationBridge.cs"))


def inspect(project, unity=None, timeout=120.0) -> dict:
    """只读体检。不启动 Unity，不起服务，不清理任何东西。"""
    project = Path(project).resolve()
    steps = []
    version = sys.version_info[:3]
    steps.append(_step("python", version >= MINIMUM_PYTHON, "Python %d.%d.%d（%s）" % (*version, sys.executable),
                       None if version >= MINIMUM_PYTHON else "安装 Python 3.10+，或设置 REV_PYTHON"))
    valid = (project / "Assets").is_dir() and (project / "ProjectSettings").is_dir()
    steps.append(_step("project", valid, str(project), None if valid else "用 --project 指向 Unity 工程根目录"))
    if not valid:
        return dict(success=False, project=str(project), steps=steps, state="invalid-project")
    installed = bridge_installed(project)
    steps.append(_step("bridge-code", installed, "工程内含 RevAutomation 桥接代码" if installed else "工程没有 RevAutomation 桥接代码",
                       None if installed else "先把 Assets/Revolution 装进工程，只复制 Python 不会生效"))
    version_file = project / "ProjectSettings/ProjectVersion.txt"
    steps.append(_step("project-version", True, "存在" if version_file.is_file() else "缺失（该文件不入库，首次打开 Unity 后生成）"))
    client = RevClient(project, "live", unity, timeout)
    editor_path = None
    try:
        editor_path = str(find_unity(project, unity))
        steps.append(_step("unity-editor", True, editor_path))
    except RevError as exc:
        steps.append(_step("unity-editor", False, str(exc), "用 --unity <Unity.exe> 或环境变量 UNITY_EDITOR 指定；成功连接一次后会记住"))
    lock = stale_lock(project)
    # 残留锁优先于心跳：Unity 崩溃后 endpoint.json 的心跳在 15 秒内还算新鲜，单看它会把死进程判成在线。
    endpoint = None if lock is True else client.endpoint()
    if endpoint:
        state = "online"
        steps.append(_step("editor", True, "在线：PID %s，%s" % (endpoint.get("pid"), "Play 中" if endpoint.get("playing") else "编辑态")))
    elif lock is True:
        state = "stale-lock"
        steps.append(_step("editor", False, "发现上次异常退出残留的 Temp/UnityLockfile，没有 Unity 持有它", "connect 启动时 Unity 会自行接管；不要手动删除工程文件"))
    elif client._editor_open():
        state = "busy"
        steps.append(_step("editor", False, "Unity 已打开但桥接没有心跳：可能在导入、编译报错或卡在弹窗", "看 Unity 窗口；编译错误用 editor compilation，修好后重跑 connect"))
    else:
        state = "offline"
        steps.append(_step("editor", False, "Unity 没有打开", "运行 connect 自动打开"))
    service = daemon.status(project)
    steps.append(_step("daemon", bool(service.get("running")), "常驻服务运行中，PID %s" % service.get("pid") if service.get("running") else "常驻服务未运行"))
    toolchain = _toolchain()
    if toolchain is not None:
        steps.append(dict(step="il2cpp-toolchain", ok=toolchain["ok"], detail=toolchain["detail"], optional=True))
    blocking = [item for item in steps if item["ok"] is False and not item.get("optional") and item["step"] not in ("editor", "daemon")]
    return dict(success=not blocking, project=str(project), state=state, editor=editor_path, steps=steps)


def _capability_gap(client) -> dict:
    """比对 CLI 清单和 Unity 实际注册：桥比 CLI 旧时，AI 要知道哪些动作用不了。"""
    response = client.call("editor.capabilities")
    live = {item["id"] for item in (response.get("data") or {}).get("actions", [])}
    known = {item["id"] for item in read_json(ROOT / "commands.json")["actions"]}
    python_side = {"editor.launch", "editor.wait", "scenario.validate"}
    return dict(liveActions=len(live), missingInUnity=sorted(known - live - python_side), unknownToCli=sorted(live - known))


def connect(project, unity=None, timeout=600.0, idle_timeout=1800.0, start_daemon=True, launch=True) -> dict:
    """重启后的唯一入口：幂等，已经连好时只做核对，可以放心重复执行。"""
    report = inspect(project, unity, timeout)
    if not report["success"]:
        return report
    project = Path(project).resolve()
    client = RevClient(project, "live", unity, timeout)
    steps = report["steps"]
    if report["state"] != "online":
        if not launch:
            return dict(report, success=False, message="Unity 不在线，且指定了不自动启动。")
        if report["state"] == "busy":
            steps.append(_step("launch", True, "Unity 已打开，等待桥接就绪"))
            client.wait_ready()
        else:
            launched = client.launch()
            steps.append(_step("launch", True, "已启动 Unity 并等到桥接就绪" if not launched.get("alreadyOpen") else "Unity 已在运行"))
    else:
        client.wait_ready()
    status = client.call("editor.status").get("data") or {}
    if status.get("editorPath"):
        remember_editor(project, status["editorPath"], status.get("unityVersion"))
    if status.get("playing"):
        steps.append(_step("play-state", True, "Unity 正在 Play：编辑类动作要先 editor stop"))
    gap = _capability_gap(client)
    steps.append(_step("capabilities", not gap["missingInUnity"], "Unity 注册 %d 个动作" % gap["liveActions"] + (
        "，缺 %d 个：%s" % (len(gap["missingInUnity"]), ", ".join(gap["missingInUnity"][:12])) if gap["missingInUnity"] else ""),
        "工程里的 Assets/Revolution 比 CLI 旧，需要更新后再用这些动作" if gap["missingInUnity"] else None))
    service = None
    if start_daemon:
        service = daemon.start(project, idle_timeout)
        steps.append(_step("daemon-start", True, "常驻服务就绪，PID %s" % service.get("pid")))
    return dict(success=True, project=str(project), state="connected", editor=status.get("editorPath"), unityVersion=status.get("unityVersion"),
                playing=bool(status.get("playing")), capabilityGap=gap, daemon=service, steps=steps,
                next="编辑器操作用 rev.py；Play 中调 GM 先 runtime start 再 game gm；RevMCP 可选，由客户端自动启动。")
