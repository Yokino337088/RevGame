"""Export actual measured values; never synthesize missing performance samples."""
import csv
import html
import json
import re
from datetime import date
from pathlib import Path
import shutil

from .client import read_json, atomic_json
from .media import collect_media

# 视觉沿用项目《战斗性能优化规则》那套工程手册风（绿金 / 纸色 / 章节卡片 / 表格 / 图墙），
# 不另发明一套。刻意内联，不引用外部 CSS：报告要能单文件发给别人看。
REPORT_CSS = """
:root{--ink:#25352f;--muted:#65736b;--green:#326956;--gold:#a76e2d;--line:#dce3da;--paper:#f6f7f2}
*{box-sizing:border-box}html{scroll-behavior:smooth}
body{margin:0;background:var(--paper);color:var(--ink);font:16px/1.85 "Microsoft YaHei","PingFang SC",sans-serif}
.topbar{height:70px;border-bottom:1px solid var(--line);padding:0 5vw;display:flex;align-items:center;justify-content:space-between;background:#fbfcf8}
.brand{font-weight:800;letter-spacing:2px}
.brand i{font-style:normal;background:var(--green);color:#fff;display:inline-grid;place-items:center;width:32px;height:32px;border-radius:10px;margin-right:10px}
.tag{font:12px/1.6 Consolas,monospace;letter-spacing:1px;color:var(--muted)}
.hero{background:linear-gradient(135deg,#fefdf5,#e6efe1);border-bottom:1px solid var(--line);padding:44px 5vw 34px}
.wrap{max-width:1120px;margin:0 auto}
.eyebrow{color:var(--green);font:12px/1.5 Consolas,monospace;letter-spacing:2px}
.hero h1{font-size:clamp(26px,3vw,40px);line-height:1.3;margin:14px 0 10px;letter-spacing:-1px}
.hero p{margin:0;color:var(--muted)}
.stamp{font:12px Consolas,monospace;margin-top:16px;color:#7b867b}
main{max-width:1120px;margin:0 auto;padding:26px 5vw 60px}
.chapter{background:#fffefa;border:1px solid var(--line);border-radius:16px;margin-bottom:20px;padding:26px 28px}
.chapter h2{font-size:21px;margin:0 0 16px;letter-spacing:-.3px}
.table-scroll{overflow:auto;margin:16px -4px;border:1px solid var(--line);border-radius:10px}
table{border-collapse:collapse;width:100%;font-size:13px;line-height:1.7}
th{text-align:left;background:#edf2e9;font-weight:700;color:#375441}
td,th{padding:10px 12px;border-bottom:1px solid #e4e9e0;vertical-align:top;overflow-wrap:anywhere}
tr:last-child td{border-bottom:0}td:first-child{font-weight:600;min-width:96px}
.gallery{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:14px}
figure{margin:0;border:1px solid var(--line);border-radius:12px;overflow:hidden;background:#fff}
figure img{display:block;width:100%;height:auto}
figcaption{padding:8px 11px;font-size:12px;color:var(--muted);border-top:1px solid var(--line);overflow-wrap:anywhere}
.legend{padding:12px 16px;background:#f8eedb;border-left:3px solid #b08b4e;border-radius:8px;font-size:13px;color:#705936;margin-bottom:18px}
code{font:13px/1.65 Consolas,monospace;background:#eef1e9;border:1px solid #e1e5dc;border-radius:4px;padding:1px 4px;overflow-wrap:anywhere}
footer{border-top:1px solid var(--line);padding:22px 5vw;color:var(--muted);font-size:13px;display:flex;justify-content:space-between;gap:20px;flex-wrap:wrap}
@media(max-width:640px){.hero{padding:30px 20px}main{padding:18px 16px 40px}.chapter{padding:20px}}
"""


def report_file_name(title, day=None):
    """报告文件名 = 中文标题 + 日期。

    为什么不叫 report.html：报告是要发给别人看的交付物，文件名本身就是索引；
    一个目录里躺三个 report.html，事后分不清哪份是哪次。
    """
    day = day or date.today()
    safe = re.sub(r'[\\/:*?"<>|\r\n\t]+', "-", str(title)).strip(" .-_")
    return "%s_%s.html" % (safe or "自动化测试报告", day.isoformat())


def export_evidence(directory, title="自动化测试报告", subtitle="证据与截图",
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
    anims = "".join(figure(p) for p in animations) or "<p>本次没有动图。动态效果（技能、位移、粒子、UI 过渡）应补抓 GIF。</p>"
    note_html = "".join("<p>%s</p>" % html.escape(str(n)) for n in (notes or []))

    doc = """<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>%s</title><style>%s</style></head><body>
<header class="topbar"><div class="brand"><i>R</i>REVOLUTION <span class="tag">AUTOMATION EVIDENCE</span></div>
<span class="tag">%s</span></header>
<div class="hero"><div class="wrap"><div class="eyebrow">REVOLUTION / 自动化证据 / %s</div>
<h1>%s</h1><p>%s</p><div class="stamp">由 Revolution.RevCLI 生成 · 图片与动图来自本次运行目录，未做美化</div></div></div>
<main>
<div class="legend">适用范围：本次自动化运行。结论只由断言决定；图片只能证明当时画面，不能替代数值断言。</div>
<section id="s1" class="chapter"><h2>1. 概览</h2><div class="table-scroll"><table><tbody>%s</tbody></table></div></section>
<section id="s2" class="chapter"><h2>2. 指标</h2><div class="table-scroll"><table><tbody>%s</tbody></table></div></section>
<section id="s3" class="chapter"><h2>3. 截图</h2><div class="gallery">%s</div></section>
<section id="s4" class="chapter"><h2>4. 动图</h2><p>GIF 用来观察动态效果；单帧截图表达不了连招、位移与粒子节奏。</p><div class="gallery">%s</div></section>
<section id="s5" class="chapter"><h2>5. 说明</h2>%s</section>
</main><footer><span>Revolution · 自动化证据报告</span><span>%s</span></footer></body></html>""" % (
        html.escape(title), REPORT_CSS, date.today().isoformat(), date.today().isoformat(),
        html.escape(title), html.escape(subtitle),
        overview_rows or "<tr><td>（无）</td></tr>",
        "".join(rows) or "<tr><td>（本次未提供指标）</td></tr>",
        gallery, anims, note_html or "<p>（无补充说明）</p>", html.escape(directory.name))

    target = directory / report_file_name(title)
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
    title = "Revolution 自动化运行报告"
    text = f"""<!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>{title}</title><style>{REPORT_CSS}</style></head><body>
<header class="topbar"><div class="brand"><i>R</i>REVOLUTION <span class="tag">AUTOMATION RUN</span></div>
<span class="tag">{date.today().isoformat()}</span></header>
<div class="hero"><div class="wrap"><div class="eyebrow">REVOLUTION / 运行汇总 / {date.today().isoformat()}</div>
<h1>{title}</h1><p>逐轮结果与汇总指标，数值口径见下方说明。</p></div></div>
<main><div class="legend">帧耗时来自 Unity unscaledDeltaTime，含编辑器、采集与自动化开销，不是独立 CPU / GPU 时间。
P95 为直方图近似上界；没有帧样本时数值 0 表示未测得，不能解释成零耗时；预热轮不计入汇总。
内存峰值为每 30 帧及轮末的采样峰值，含编辑器与采集开销，捕获不到采样之间的瞬时峰值。</div>
<section class="chapter"><h2>汇总</h2><div class="table-scroll"><table><tbody>{body}</tbody></table></div></section>
<section class="chapter"><h2>原始数据</h2><p><a href="iterations.csv">逐轮 CSV</a> · <a href="summary.json">原始汇总 JSON</a></p></section>
</main><footer><span>Revolution · 自动化运行报告</span><span>{html.escape(directory.name)}</span></footer></body></html>"""
    (directory / report_file_name(title)).write_text(text, encoding="utf-8")
    return dict(html=str(directory / report_file_name(title)), csv=str(directory / "iterations.csv"), json=str(directory / "summary.json"))
