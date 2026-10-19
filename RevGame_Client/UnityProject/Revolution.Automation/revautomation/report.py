"""Export actual measured values; never synthesize missing performance samples."""
import csv
import html
import json
from pathlib import Path
import shutil

from .client import read_json, atomic_json


def export_report(result: Path, directory: Path):
    data = read_json(result)
    directory.mkdir(parents=True, exist_ok=True)
    atomic_json(directory / "summary.json", data)
    rows_path = result.parent / "iterations.jsonl"
    fields = ["iteration", "warmup", "success", "durationMs", "frames", "frameP95Ms", "frameMaxMs",
              "monoUsedBytes", "allocatedMemoryBytes", "gc0", "gc1", "gc2", "message"]
    with (directory / "iterations.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        if rows_path.exists():
            with rows_path.open(encoding="utf-8-sig") as rows:
                for line in rows:
                    row = json.loads(line)
                    # CSV 经常用 Excel 打开；错误消息来自业务，不能让它被当成公式执行。
                    for key, value in row.items():
                        if isinstance(value, str) and value.lstrip().startswith(("=", "+", "-", "@")):
                            row[key] = "'" + value
                    writer.writerow(row)
            if rows_path.resolve() != (directory / "iterations.jsonl").resolve():
                shutil.copyfile(rows_path, directory / "iterations.jsonl")
    body = "".join(f"<tr><th>{html.escape(str(key))}</th><td>{html.escape(str(value))}</td></tr>" for key, value in data.items())
    text = f"""<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Revolution 自动化运行报告</title><style>body{{max-width:1100px;margin:40px auto;padding:0 20px;font:16px/1.8 system-ui;color:#203047}}
table{{width:100%;border-collapse:collapse}}th,td{{text-align:left;padding:10px;border-bottom:1px solid #ddd;overflow-wrap:anywhere}}th{{width:35%}}
h1{{color:#244e92}}aside{{background:#edf3ff;padding:16px}}</style><h1>Revolution 自动化运行报告</h1>
<aside>帧耗时来自 Unity unscaledDeltaTime，含编辑器、采集和自动化开销；不是独立 CPU/GPU 时间。
P95 为直方图近似上界。没有帧样本时数值 0 表示未测得，不能解释成零耗时。预热轮不计入正式汇总。
内存峰值为每 30 帧及轮末的采样峰值，含编辑器与采集开销，无法捕获采样间短暂峰值。</aside>
<p><a href="iterations.csv">逐轮 CSV</a> · <a href="summary.json">原始汇总 JSON</a></p><table>{body}</table></html>"""
    (directory / "report.html").write_text(text, encoding="utf-8")
    return dict(html=str(directory / "report.html"), csv=str(directory / "iterations.csv"), json=str(directory / "summary.json"))
