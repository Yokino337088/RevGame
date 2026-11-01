"""CLI 各种调用方式的真实耗时对比（同一工程、同一只读动作 editor.status）。

每个样本都是一次完整的"调用方视角"墙钟时间：包含进程启动、连接、Unity 往返和取回结果。
全部样本原样输出，不筛选长尾。只读，不会修改工程。需要先 `daemon start`。
"""
import argparse
import json
import statistics
import subprocess
import sys
import time
from pathlib import Path

CLI_DIR = Path(__file__).resolve().parents[1]
REV_PY = CLI_DIR / "rev.py"
REV_CMD = CLI_DIR / "rev.cmd"


def run_once(command, check, label):
    started = time.perf_counter()
    process = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    elapsed = (time.perf_counter() - started) * 1000
    if not check(process):
        raise SystemExit(f"{label} 调用失败：exit={process.returncode} out={process.stdout[:200]!r} err={process.stderr[:200]!r}")
    return round(elapsed, 1)


def summarize(label, times):
    ordered = sorted(times)
    return dict(label=label, samples=times, medianMs=round(statistics.median(times), 1), meanMs=round(statistics.fmean(times), 1),
                p95Ms=ordered[min(len(ordered) - 1, int(len(ordered) * .95))], minMs=ordered[0], maxMs=ordered[-1])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    project = args.project.resolve()
    python = sys.executable
    ok_json = lambda p: p.returncode == 0 and '"success": true' in p.stdout
    ok_curl = lambda p: p.returncode == 0 and '"success":true' in p.stdout

    root = project / "Library/RevolutionAutomation"
    endpoint = json.loads((root / "cli-daemon.json").read_text(encoding="utf-8-sig"))
    body = root / "bench-call.json"
    body.write_text('{"action":"editor.status","arguments":{}}', encoding="ascii")
    base = [python, str(REV_PY), "--project", str(project), "--mode", "live"]
    py_launcher = ["py", "-3.13", str(REV_PY), "--project", str(project), "--mode", "live"]

    variants = [
        ("A 无常驻：python.exe rev.py --no-daemon", base + ["--no-daemon", "call", "editor.status"], ok_json),
        ("B 常驻+快速路径：python.exe rev.py", base + ["call", "editor.status"], ok_json),
        ("C 常驻+快速路径：rev.cmd（缓存解释器）", ["cmd", "/c", str(REV_CMD), "--project", str(project), "--mode", "live", "call", "editor.status"], ok_json),
        ("D 对照：py -3.13 启动器 + 快速路径", py_launcher + ["call", "editor.status"], ok_json),
        ("E 直连：curl.exe → /call（不起 Python）", [
            "curl.exe", "-s", "-H", "@" + str(root / "cli-daemon.headers"), "-H", "Content-Type: application/json",
            "--data-binary", "@" + str(body), f"http://127.0.0.1:{endpoint['port']}/call"], ok_curl),
    ]
    for label, command, check in variants:  # 热身：让磁盘缓存与服务端路径都先走一遍
        run_once(command, check, label)
    samples = {label: [] for label, _, _ in variants}
    for _ in range(args.samples):  # 轮流交替：同一时刻的机器状态对所有方案一致
        for label, command, check in variants:
            samples[label].append(run_once(command, check, label))
    body.unlink(missing_ok=True)

    results = [summarize(label, samples[label]) for label, _, _ in variants]
    report = dict(project=str(project), python=python, interleaved=True, results=results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    for item in results:
        print(f"{item['label']}\n    median {item['medianMs']}ms  mean {item['meanMs']}ms  p95 {item['p95Ms']}ms  min {item['minMs']}ms  max {item['maxMs']}ms")


if __name__ == "__main__":
    main()
