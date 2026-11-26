"""只在调用者要求导出时生成 RevMCP 报告，日常计划直接返回执行结果。"""

import datetime as dt
from pathlib import Path
import struct
import uuid
import zlib

from revautomation.storage import check_revmcp_output_path
from . import benchmark, workflow


# 从绑定工程沿父目录查找现有文档。命中已有测试报告目录时优先使用它。
DOCUMENT_NAMES = ("文档", "Docs", "docs", "Documentation", "documentation", "Revolution.Document")
REPORT_NAMES = ("AI自动化测试报告", "自动化测试报告", "automation-reports", "test-reports")


def workspace_for(directory):
    """识别工程所属工作区，支持独立仓库、嵌套 Unity 工程和 Git worktree。"""
    directory = Path(directory).resolve()
    parents = (directory, *directory.parents)
    return next((item for item in parents if (item / ".git").exists()),
                next((item for item in parents if (item / "AGENTS.md").is_file()), directory))


def check_output_path(path):
    """拦截历史 results 目录，兼顾工具副本改名及目录别名。"""
    requested = Path(path).absolute()
    resolved = check_revmcp_output_path(requested)
    former_results = Path(__file__).resolve().parents[1] / "results"
    if requested.is_relative_to(former_results) or resolved.is_relative_to(former_results.resolve()):
        raise ValueError("RevMCP 工具目录的 results 已停用")
    return resolved


def document_directory(hub, requested=None):
    """优先采用用户指定的项目内路径，省略时查找项目已有文档目录。"""
    if not hub.editor:
        if requested is not None:
            raise ValueError("写正式报告前须绑定目标 Unity 工程")
        return None
    project = hub.editor.project

    def checked(directory):
        """解析别名后核对项目边界，并避开机器缓存。"""
        path = check_output_path(directory)
        reserved = (hub.workspace / ".codex/work", hub.workspace / ".git",
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


def screenshots(value):
    """按步骤列出已经领取的 PNG，只在生成预览时解码选中的帧。"""
    if isinstance(value, dict):
        if value.get("mimeType") == "image/png" and (value.get("base64") or isinstance(value.get("path"), str)):
            yield value
        for item in value.values():
            yield from screenshots(item)
    elif isinstance(value, list):
        for item in value:
            yield from screenshots(item)


def representative(items, limit):
    """保留前后画面及均匀抽取的中间帧，避免长计划把原图逐张复制进文档。"""
    if len(items) <= limit:
        return items
    return [items[index * (len(items) - 1) // (limit - 1)] for index in range(limit)]


def write_evidence(result, destination):
    """为本次导出分配独立目录，写一份 HTML 和必要的缩放画面。"""
    from revautomation.media import capture_source, write_gif, write_png_preview
    from revautomation.report import export_evidence

    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8]
    directory = check_output_path(destination / stamp)
    directory.mkdir(parents=True, exist_ok=False)
    media = directory / "画面"
    # 同一结果可能被计划摘要重复引用，按截图 ID 或旧路径去重。
    shots = list({shot.get("captureId") or shot.get("path") or id(shot): shot for shot in screenshots(result)}.values())
    notes = ["结果覆盖本次已执行步骤、等待、断言及清理。功能覆盖和缺陷位置以验收说明为准。"]
    if shots:
        media.mkdir()
        for index, source in enumerate(representative(shots, 3), 1):
            try:
                write_png_preview(capture_source(source), media / f"{index:02d}.png")
            except (OSError, ValueError, struct.error, zlib.error, IndexError):
                notes.append(f"第 {index} 张截图无法生成预览，可查看本轮截图调用响应。")
        if len(shots) > 1:
            try:
                write_gif((capture_source(shot) for shot in representative(shots, 12)), media / "实际连拍.gif", delay_ms=120, max_width=520)
            except (OSError, ValueError, struct.error, zlib.error, IndexError):
                notes.append("画面尺寸不一致或存在不可读取的帧，保留能打开的静帧。")
    title = f"{result.get('name') or '计划'} 自动化测试"
    summary = {"结论": "通过" if result.get("success") else "失败",
               "执行步骤": len(result.get("steps", [])), "原始截图": len(shots),
               "清理步骤": len(result.get("cleanup", []))}
    return export_evidence(directory, title, "RevMCP 本轮执行证据", summary, notes,
                           media_directory=media)["html"]


def run_with_reports(hub, plan, cancel=None):
    """只消费计划中的交付选项，断言和清理始终使用原本的业务计划。"""
    if not isinstance(plan, dict):
        raise ValueError("计划必须是对象")
    plan = dict(plan)
    benchmark_spec = plan.pop("benchmark", None)
    has_directory = "reportDirectory" in plan
    requested = plan.pop("reportDirectory", None)
    export = plan.pop("report", has_directory)
    if type(export) is not bool:
        raise ValueError("report 须为布尔值")
    if has_directory and (not export or not isinstance(requested, str) or not requested.strip()):
        raise ValueError("reportDirectory 须为非空目录路径，且与 report: true 配合或单独使用")
    # 未要求交付报告时，直接走业务计划，连文档目录也无需查找。
    if not export:
        return workflow.run(hub, plan, cancel) if benchmark_spec is None else benchmark.run(hub, plan, benchmark_spec, cancel)

    destination = document_directory(hub, requested)
    result = workflow.run(hub, plan, cancel) if benchmark_spec is None else benchmark.run(hub, plan, benchmark_spec, cancel)
    if destination is None:
        result["evidenceError"] = "未找到项目文档目录。可用 plan.reportDirectory 指定项目内的目录。"
    else:
        try:
            # 再次校验已选路径，避免计划运行期间变更目录链接后写到工作区外。
            destination = document_directory(hub, str(destination))
            result["evidencePath"] = write_evidence(result, destination)
        except (OSError, ValueError) as error:
            result["evidenceError"] = f"HTML 写入失败：{error}"
    return result
