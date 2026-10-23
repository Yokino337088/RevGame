#!/usr/bin/env python3
"""Validate and run a Revolution.Automation scenario in an already-running Unity Play session.

The script does not start/stop Unity or invent GM commands. Use the skill workflow to inspect
editor state and choose a real scenario/GM contract first.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


def invoke(cli: Path, project: Path, args: list[str], timeout: float) -> tuple[int, str, str]:
    command = [sys.executable, str(cli), "--project", str(project), *args]
    completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    return completed.returncode, completed.stdout, completed.stderr


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--automation", required=True, type=Path, help="folder containing rev.py")
    parser.add_argument("--project", required=True, type=Path, help="Unity project root")
    parser.add_argument("--scenario", required=True, type=Path, help="scenario JSON path")
    parser.add_argument("--report-dir", required=True, type=Path, help="scenario report directory")
    parser.add_argument("--timeout", type=float, default=600, help="per CLI request timeout in seconds")
    args = parser.parse_args()

    automation = args.automation.resolve()
    project = args.project.resolve()
    scenario = args.scenario.resolve()
    report_dir = args.report_dir.resolve()
    cli = automation / "rev.py"
    for path, label in ((cli, "Automation CLI"), (project, "Unity project"), (scenario, "scenario")):
        if not path.exists():
            print(json.dumps({"success": False, "error": f"{label} not found", "path": str(path)}, ensure_ascii=False))
            return 2

    code, out, err = invoke(cli, project, ["scenario", "validate", str(scenario)], args.timeout)
    print("VALIDATE_STDOUT:")
    print(out.rstrip())
    if err.strip():
        print("VALIDATE_STDERR:")
        print(err.rstrip(), file=sys.stderr)
    if code != 0:
        return code

    # Scenario actions require the already-running, unpaused graphical editor. Enforce live mode.
    code, out, err = invoke(cli, project, ["--mode", "live", "--timeout", str(args.timeout),
                                           "scenario", "run", str(scenario), "--report-dir", str(report_dir)],
                            args.timeout + 30)
    print("RUN_STDOUT:")
    print(out.rstrip())
    if err.strip():
        print("RUN_STDERR:")
        print(err.rstrip(), file=sys.stderr)
    # 保留原始输出，避免只截取终端末尾后丢失失败原因与作业路径。
    report_dir.mkdir(parents=True, exist_ok=True)
    (report_dir / "cli-stdout.json").write_text(out, encoding="utf-8")
    (report_dir / "cli-stderr.log").write_text(err, encoding="utf-8")
    try:
        response = json.loads(out)
    except (ValueError, TypeError):
        print("CLI 未返回可解析 JSON，不能判定通过。", file=sys.stderr)
        return code or 2
    result = response.get("data") or {}
    passed = (code == 0 and response.get("success") is True and result.get("success") is True
              and result.get("requested", 0) > 0 and result.get("completed") == result.get("requested")
              and result.get("failedIterations") == 0 and result.get("unityErrors") == 0
              and not result.get("errors") and not result.get("cancelled"))
    return 0 if passed else (code or 1)


if __name__ == "__main__":
    raise SystemExit(main())
