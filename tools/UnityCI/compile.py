"""用 Unity 批处理编译 RevGame，并把编译器原始坐标映射到仓库文件。"""

import argparse
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath


# 支持 Unity/Bee 输出的相对路径、绝对路径及可选列号。
COMPILER_ERROR = re.compile(
    r"^(?:(?P<path>.+?)\((?P<line>[1-9]\d*)(?:,\s*(?P<col>[1-9]\d*))?\):\s*)?"
    r"error (?P<code>CS\d+):\s*(?P<message>.+)$"
)
COMPILER_FAILURE = re.compile(
    r"Scripts have compiler errors\.?|Compilation failed\.?|"
    r"Aborting batchmode due to failure:\s*(?:Compilation failed\.?)?",
    re.IGNORECASE,
)


def source_path(reported, project, repo):
    """只给检出中的源码发文件注解，缓存包和外部路径保留原始日志坐标。"""
    path = Path(reported.strip('"').replace("\\", "/"))
    path = (project / path).resolve()
    if path.is_relative_to(repo) and path.is_file():
        relative = path.relative_to(repo).as_posix()
        if "Library" not in PurePosixPath(relative).parts:
            return relative
    return None


def escape(value, *, property_value=False):
    """转义 GitHub workflow 命令的换行、分隔符和百分号。"""
    value = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return value.replace(",", "%2C").replace(":", "%3A") if property_value else value


def report(log, project, repo, exit_code, github):
    """输出每个不同的编译错误，并要求 Unity 正常完成本次批处理。"""
    if not log.is_file():
        message = f"Unity 未写出本次编译日志，退出码 {exit_code}。"
        print(f"::error title=Unity 编译未完成::{message}" if github else message, file=sys.stderr)
        return 1

    lines = log.read_text(encoding="utf-8-sig", errors="replace").splitlines()
    errors = set()
    for line in lines:
        match = COMPILER_ERROR.match(line.strip())
        if not match:
            continue
        reported, number, column, code, message = match.groups()
        if match[0] in errors:
            continue
        errors.add(match[0])
        print(f"Unity: {match[0]}")
        if github:
            path = source_path(reported, project, repo) if reported else None
            fields = f"title={code}"
            if path:
                fields += f",file={escape(path, property_value=True)},line={number}"
                if column:
                    fields += f",col={column}"
            print(f"::error {fields}::{escape(message if path else match[0])}")

    failed = any(COMPILER_FAILURE.fullmatch(line.strip()) for line in lines)
    finished = any("Exiting batchmode successfully now!" in line for line in lines)
    if exit_code != 0 or errors or failed or not finished:
        message = f"Unity 编译失败或未正常结束。退出码 {exit_code}，C# 诊断 {len(errors)} 条。"
        if github and not errors:
            print(f"::error title=Unity 编译未完成::{message}")
        else:
            print(message, file=sys.stderr)
        if not errors:
            # 许可证、包解析或启动失败没有源码坐标，保留最后的实际 Unity 日志。
            for line in lines[-80:]:
                print(f"Unity: {line}")
        return 1

    print("Unity Editor 批处理编译完成，C# 编译错误 0 条。")
    return 0


def main():
    """显式指定编辑器、工程与本次日志，避免读取已有编辑器实例的旧结果。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--github", action="store_true", help="输出 GitHub Actions 注解。")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    repo = Path(__file__).resolve().parents[2]
    project = args.project.resolve()
    log = args.log.resolve()
    if not project.is_relative_to(repo) or not (project / "ProjectSettings/ProjectSettings.asset").is_file():
        parser.error("--project 应指向脚本所在仓库中的 Unity 工程。")
    if not args.editor.is_file():
        parser.error("--editor 应指向已安装的 Unity.exe。")
    if log.exists():
        parser.error("--log 已存在，请为本次编译使用新的日志路径。")
    log.parent.mkdir(parents=True, exist_ok=True)
    editor = args.editor.resolve()

    command = [str(editor), "-batchmode", "-nographics", "-quit", "-buildTarget", "Win64",
               "-projectPath", str(project), "-logFile", str(log)]
    try:
        result = subprocess.run(command, cwd=project, timeout=1500, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    except subprocess.TimeoutExpired:
        print("Unity 批处理在 25 分钟内未完成。", file=sys.stderr)
        return report(log, project, repo, 124, args.github)
    if result.returncode and not log.is_file():
        for line in result.stdout.decode("utf-8", errors="replace").splitlines()[-20:]:
            print(f"Unity: {line}")
    return report(log, project, repo, result.returncode, args.github)


if __name__ == "__main__":
    raise SystemExit(main())
