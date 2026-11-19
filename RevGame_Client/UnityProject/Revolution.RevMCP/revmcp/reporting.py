"""RevMCP 原始工单与正式报告的落盘入口。所有调用共用同一项目边界。"""

from pathlib import Path
import struct
import zlib

from revautomation.storage import check_revmcp_output_path
from . import workflow


# 从绑定工程沿父目录查找现有文档。命中已有测试报告目录时优先使用它。
DOCUMENT_NAMES = ("文档", "Docs", "docs", "Documentation", "documentation", "Revolution.Document")
REPORT_NAMES = ("AI自动化测试报告", "自动化测试报告", "automation-reports", "test-reports")


def workspace_for(directory):
    """识别工程所属工作区，支持独立仓库、嵌套 Unity 工程和 Git worktree。"""
    directory = Path(directory).resolve()
    parents = (directory, *directory.parents)
    return next((item for item in parents if (item / ".git").exists()),
                next((item for item in parents if (item / "AGENTS.md").is_file()), directory))


def machine_directory(workspace, explicit=None):
    """默认只在工作区忽略目录留工单。即使显式配置也拒绝工具原 results 路径。"""
    requested = Path(explicit if explicit is not None else workspace / ".codex/work/revmcp").absolute()
    # 保护当前工具副本的旧位置，包括外部部署时被重命名的目录。
    if requested.is_relative_to(Path(__file__).resolve().parents[1] / "results"):
        raise ValueError("RevMCP 工具目录的 results 已停用")
    return check_revmcp_output_path(requested)


def document_directory(hub, requested=None):
    """先处理用户指定的项目内路径，再找现有文档。无法确定时仅留原始工单。"""
    if not hub.editor:
        if requested is not None:
            raise ValueError("写正式报告前须绑定目标 Unity 工程")
        return None
    project = hub.editor.project

    def checked(directory):
        """在执行计划前预检目标，解析别名后仍须在工作区，且避开工单和缓存。"""
        path = check_revmcp_output_path(directory)
        reserved = (hub.reports, hub.workspace / ".codex/work", hub.workspace / ".git",
                    project / "Library", project / "Temp")
        if not path.is_relative_to(hub.workspace) or any(path.is_relative_to(root.resolve()) for root in reserved):
            raise ValueError("reportDirectory 须位于绑定项目的文档目录，且与工单和缓存分开")
        if path.exists() and not path.is_dir():
            raise ValueError("reportDirectory 指向了文件")
        return path

    if requested is not None:
        if not isinstance(requested, str) or not requested.strip():
            raise ValueError("reportDirectory 须为非空目录路径")
        return checked(hub.workspace / requested)

    first_document = None
    for owner in (project, *project.parents):
        if not owner.is_relative_to(hub.workspace):
            break
        for name in DOCUMENT_NAMES:
            docs = owner / name
            if not docs.is_dir():
                continue
            try:
                docs = checked(docs)
                first_document = first_document or docs
                for report_name in REPORT_NAMES:
                    child = docs / report_name
                    if child.is_dir():
                        return checked(child)
            except ValueError:
                # 自动发现不跟随指向工作区外或缓存内的文档链接。
                continue
    return first_document


def screenshot_paths(value):
    """按步骤遇到截图的顺序读取来源，正式报告仅保留本次用例的画面。"""
    if isinstance(value, dict):
        if value.get("mimeType") == "image/png" and isinstance(value.get("path"), str):
            path = Path(value["path"]).resolve()
            if path.is_file():
                yield path
        for item in value.values():
            yield from screenshot_paths(item)
    elif isinstance(value, list):
        for item in value:
            yield from screenshot_paths(item)


def representative(items, limit):
    """保留前后画面及均匀抽取的中间帧，避免长计划把原图逐张复制进文档。"""
    if len(items) <= limit:
        return items
    return [items[index * (len(items) - 1) // (limit - 1)] for index in range(limit)]


def write_evidence(result, destination, report_path):
    """同一运行只写一份 HTML，最多三张缩放截图和一份动图。原始回执独立保留。"""
    from revautomation.media import write_gif, write_png_preview
    from revautomation.report import export_evidence

    directory = check_revmcp_output_path(destination / Path(report_path).stem)
    directory.mkdir(parents=True, exist_ok=False)
    media = directory / "画面"
    shots = list(dict.fromkeys(screenshot_paths(result)))
    notes = ["结果覆盖本次已执行步骤、等待、断言及清理。功能覆盖和缺陷位置以验收说明为准。"]
    if shots:
        media.mkdir()
        for index, source in enumerate(representative(shots, 3), 1):
            try:
                write_png_preview(source, media / f"{index:02d}.png")
            except (OSError, ValueError, struct.error, zlib.error, IndexError):
                notes.append(f"第 {index} 张截图无法生成预览，原图位置见工单。")
        if len(shots) > 1:
            try:
                write_gif(representative(shots, 12), media / "实际连拍.gif", delay_ms=120, max_width=520)
            except (OSError, ValueError, struct.error, zlib.error, IndexError):
                notes.append("画面尺寸不一致或存在不可读取的帧，保留能打开的静帧。")
    title = f"{result.get('name') or '计划'} 自动化测试"
    summary = {"结论": "通过" if result.get("success") else "失败",
               "执行步骤": len(result.get("steps", [])), "原始截图": len(shots),
               "清理步骤": len(result.get("cleanup", []))}
    return export_evidence(directory, title, "RevMCP 本轮执行证据", summary, notes,
                           media_directory=media)["html"]


def run_with_reports(hub, plan, cancel=None):
    """共用 CLI 与 MCP 的交付路径。业务结果和文件写入错误分别返回。"""
    if not isinstance(plan, dict):
        raise ValueError("计划必须是对象")
    plan = dict(plan)
    destination = document_directory(hub, plan.pop("reportDirectory", None))
    result = workflow.run(hub, plan, cancel)
    report_path = hub.save(result)
    result["reportPath"] = report_path
    if destination is None:
        result["evidenceError"] = "未找到项目文档目录，已保存原始工单。可用 plan.reportDirectory 指定项目内的目录。"
    else:
        try:
            # 再次校验已选路径，避免计划运行期间变更目录链接后写到工作区外。
            destination = document_directory(hub, str(destination))
            result["evidencePath"] = write_evidence(result, destination, report_path)
        except (OSError, ValueError) as error:
            result["evidenceError"] = f"业务作业已保存，HTML 写入失败：{error}"
    return result
