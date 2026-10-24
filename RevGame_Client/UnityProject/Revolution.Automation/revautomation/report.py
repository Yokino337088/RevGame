"""Export actual measured values; never synthesize missing performance samples."""
import csv
import html
import json
from pathlib import Path
import shutil

from .client import read_json, atomic_json
from .media import collect_media

# 证据报告与《进度同步》那批文档同一套视觉语言（hero / 卡片 / 表格 / 图墙）。
# 刻意内联，不引用外部 CSS：报告要能单文件发给别人看。
EVIDENCE_CSS = """
:root{--accent:#2F6FED;--accent-soft:#EAF1FE;--page:#F4F6F9;--panel:#fff;--border:#E3E7ED;
--text:#1F2329;--text-sub:#6B7280;--radius:10px}
*{box-sizing:border-box}html{scroll-behavior:smooth}
body{margin:0;background:var(--page);color:var(--text);font:16px/1.75 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif}
.wrap{max-width:1080px;margin:0 auto;padding:0 20px}
header.hero{background:linear-gradient(135deg,#24406f,#2F6FED 62%,#4b8bf5);color:#fff;padding:46px 0 40px}
header.hero .kicker{display:inline-block;font-size:12px;letter-spacing:.18em;text-transform:uppercase;
background:rgba(255,255,255,.16);padding:4px 10px;border-radius:999px;margin-bottom:14px}
header.hero h1{margin:0 0 10px;font-size:30px;line-height:1.3}
header.hero p{margin:0;opacity:.92}
main{padding:26px 20px 60px;max-width:1080px;margin:0 auto}
section{background:var(--panel);border:1px solid var(--border);border-radius:var(--radius);
padding:22px 24px;margin-bottom:18px}
section>h2{margin:0 0 14px;font-size:21px;color:#14355f}
section.toc ol{columns:3;column-gap:26px;margin:0;padding-left:20px}
section.toc a{color:var(--accent);text-decoration:none}
section.toc p{color:var(--text-sub);font-size:14px;margin:12px 0 0}
.table-wrap{overflow:auto}
table{width:100%;border-collapse:collapse;font-size:15px}
th,td{text-align:left;padding:9px 12px;border-bottom:1px solid var(--border);overflow-wrap:anywhere}
th{width:28%;background:#f7f9fc;font-weight:600}
tbody tr:nth-child(even) td{background:#fafbfd}
code{background:#f1f3f7;border-radius:5px;padding:1px 5px;font-size:13px}
.gallery{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px}
figure{margin:0;border:1px solid var(--border);border-radius:8px;overflow:hidden;background:#fafbfd}
figure img{display:block;width:100%;height:auto}
figcaption{padding:8px 10px;font-size:13px;color:var(--text-sub);overflow-wrap:anywhere}
.anim{max-width:100%;border:1px solid var(--border);border-radius:8px}
.callout{background:var(--accent-soft);border-left:4px solid var(--accent);border-radius:8px;padding:12px 16px;margin:14px 0}
.callout p{margin:0}
footer{color:var(--text-sub);font-size:13px;text-align:center;padding:20px}
@media(max-width:640px){section.toc ol{columns:1}header.hero h1{font-size:24px}}
"""


def export_evidence(directory, title="Revolution 自动化测试报告", subtitle="证据与截图",
                    summary=None, notes=None, limit=None):
    """把某个结果目录里的图片/动图整理成一份可单独发送的 HTML 报告。

    为什么必须带图：测试结论只写“通过”没有说服力——读者要看得到当时的画面。
    动图（GIF）用来表达"随时间变化"的效果，单帧截图看不出来。
    """
    directory = Path(directory)
    images, animations = collect_media(directory, limit)
    rows = []
    for key, value in (summary or {}).items():
        rows.append("<tr><th>%s</th><td>%s</td></tr>" % (html.escape(str(key)), html.escape(str(value))))
    overview = [
        ("报告目录", str(directory)),
        ("截图数量", str(len(images))),
        ("动图数量", str(len(animations))),
    ]
    overview_rows = "".join("<tr><th>%s</th><td>%s</td></tr>" % (html.escape(k), html.escape(v)) for k, v in overview)

    def figure(path):
        return ('<figure><img src="%s" alt="%s" loading="lazy"><figcaption>%s</figcaption></figure>'
                % (html.escape(path.name), html.escape(path.name), html.escape(path.name)))

    gallery = "".join(figure(p) for p in images) or "<p>本次没有截图。若测试涉及画面，应补截关键状态。</p>"
    anims = "".join('<figure><img class="anim" src="%s" alt="%s"><figcaption>%s</figcaption></figure>'
                    % (html.escape(p.name), html.escape(p.name), html.escape(p.name))
                    for p in animations) or "<p>本次没有动图。动态效果（技能、位移、粒子、UI 过渡）应补抓 GIF。</p>"
    note_html = "".join("<p>%s</p>" % html.escape(str(n)) for n in (notes or []))

    doc = """<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>%s</title><style>%s</style></head><body>
<header class="hero"><div class="wrap"><span class="kicker">REVOLUTION · AUTOMATION EVIDENCE</span>
<h1>%s</h1><p>%s</p></div></header>
<main>
<section class="toc"><h2>目录</h2><ol>
<li><a href="#s1">概览</a></li><li><a href="#s2">指标</a></li>
<li><a href="#s3">截图</a></li><li><a href="#s4">动图</a></li><li><a href="#s5">说明</a></li>
</ol><p>由 Revolution.Automation 生成；图片与动图均来自本次运行目录，未做美化。</p></section>
<section id="s1"><h2>1. 概览</h2><div class="table-wrap"><table><tbody>%s</tbody></table></div></section>
<section id="s2"><h2>2. 指标</h2><div class="table-wrap"><table><tbody>%s</tbody></table></div></section>
<section id="s3"><h2>3. 截图</h2><div class="gallery">%s</div></section>
<section id="s4"><h2>4. 动图</h2><p class="callout"><p>GIF 用于观察动态效果；单帧截图无法表达连招、位移与粒子节奏。</p></p>%s</section>
<section id="s5"><h2>5. 说明</h2>%s</section>
</main><footer>Revolution · 自动化证据报告</footer></body></html>""" % (
        html.escape(title), EVIDENCE_CSS, html.escape(title), html.escape(subtitle),
        overview_rows or "<tr><td>（无）</td></tr>",
        "".join(rows) or "<tr><td>（本次未提供指标）</td></tr>",
        gallery, anims, note_html or "<p>（无补充说明）</p>")

    target = directory / "report.html"
    target.write_text(doc, encoding="utf-8")
    return dict(html=str(target), images=len(images), animations=len(animations))


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
