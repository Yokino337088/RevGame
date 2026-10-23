"""RevGame业务流程CLI：复用Revolution.Automation，不创建第二套Unity通信桥。"""
from __future__ import annotations

import argparse
import base64
import html
import json
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
AUTOMATION = ROOT / "UnityProject/Revolution.Automation"
PROJECT = ROOT / "UnityProject/RevGame.Unity"
sys.path.insert(0, str(AUTOMATION))
from revautomation.client import RevClient, atomic_json, read_json
from revautomation.cli import validate_scenario
from revautomation.report import export_report


def scenario_for(plan: dict) -> dict:
    """框架负责轮询/Unity错误门禁/报告；业务执行器负责真实输入与业务断言。"""
    encoded = base64.b64encode(json.dumps(plan, ensure_ascii=False).encode("utf-8")).decode("ascii")
    return {
        "name": plan["name"], "repeat": 1, "warmup": 0,
        "stopOnFailure": True, "failOnUnityError": True,
        "setup": [{"kind": "gm", "command": "RevGame/自动化开始 " + encoded}],
        "steps": [{"kind": "until", "command": "RevGame/自动化检查", "expected": "passed",
                   "timeoutSeconds": plan["timeoutSeconds"] + 10, "intervalSeconds": 0.25}],
        "cleanup": [{"kind": "gm", "command": "RevGame/自动化取消"}],
        "teardown": [{"kind": "until", "command": "RevGame/自动化收尾", "expected": "clean",
                      "timeoutSeconds": 90, "intervalSeconds": 0.25}],
        "thresholds": {"maxFrameP95Ms": 0, "maxFrameMs": 0, "maxMonoUsedBytes": 0},
    }


def checked(client: RevClient, action: str, payload=None) -> dict:
    """同时检查桥层与GM层，不能只看Python进程退出码。失败原文交给报告保留。"""
    response = client.call(action, payload)
    if not response.get("success"):
        raise RuntimeError(json.dumps(response, ensure_ascii=False))
    return response


def write_readable(report: dict, output: Path):
    """将业务逐步证据导出为浅色HTML；所有输入转义，避免计划名/错误文本成为HTML代码。"""
    rows = "".join(
        "<tr>" + "".join(f"<td>{html.escape(str(r.get(k, '')))}</td>" for k in
                       ("chapter", "round", "winner", "battleSeconds", "playerAttacks", "enemyAttacks", "damageEvents", "enemiesAtStart", "kingCaptured")) + "</tr>"
        for r in report.get("rounds", [])
    )
    evidence = "".join(f"<li><b>{html.escape(e['step'] or '')}</b> · {e['atSeconds']:.2f}s<br>"
                       f"{html.escape(e['detail'])}<small>章{e['chapter']} / 轮{e['round']} / {e['phase']}</small></li>"
                       for e in report.get("evidence", []))
    text = f"""<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>{html.escape(report['name'])}</title><style>
body{{margin:0;background:#f4f6fb;color:#20314c;font:16px/1.7 system-ui,'Microsoft YaHei',sans-serif}}
main{{max-width:1100px;margin:auto;padding:48px 24px}} h1{{font-size:36px}}section,li{{background:white;padding:22px;margin:16px 0;border-radius:16px}}
table{{border-collapse:collapse;width:100%}}td,th{{padding:10px;text-align:center;border-bottom:1px solid #e4e9f3}}
.scroll{{overflow:auto}}small{{display:block;color:#64748b}}ol{{padding:0;list-style:none}}pre{{white-space:pre-wrap;overflow-wrap:anywhere}}
</style><main><small>RevGame / 真实输入业务回归</small><h1>{html.escape(report['name'])}</h1>
<section><b>{report['state']}</b> · 步骤 {report['completedSteps']}/{report['totalSteps']} · 真实耗时 {report['elapsedSeconds']:.2f}s
<p>指针点击 {report['pointerClicks']} · 验证落牌 {report['cardsPlayed']} · GM动作 {report['gmActions']} · Unity错误 {report['unityErrors']}</p>
<p>测试强化：{html.escape(json.dumps(report.get('boost'), ensure_ascii=False))}；强化已清除：{report.get('boostReleased', False)}。</p>
<p>四项配置均为额外百分比：+250%表示原值×3.5，0表示不加成。生命调整保留伤势比例；游戏原有准备增益照常运行。此用例不证明普通难度或Boss全部技能。</p>
<pre>{html.escape(report.get('error', ''))}</pre></section><section class="scroll"><table>
<tr><th>章</th><th>轮</th><th>胜方</th><th>模拟秒</th><th>我方攻击</th><th>敌方攻击</th><th>伤害事件</th><th>开战敌人数</th><th>擒王</th></tr>{rows}</table></section>
<h2>逐步证据</h2><ol>{evidence}</ol></main></html>"""
    (output / "business-report.html").write_text(text, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("plan", type=Path, help="项目业务计划JSON，不是框架scenario")
    parser.add_argument("--attach", action="store_true", help="明确接管当前Play（不会停止它）；默认只接受未运行的编辑器")
    parser.add_argument("--validate-only", action="store_true", help="仅离线生成并校验框架scenario，不能当作Play通过")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    plan = read_json(args.plan)
    spec = scenario_for(plan)
    validate_scenario(spec)
    output = (args.output or Path(__file__).parent / "results" / time.strftime("%Y%m%d-%H%M%S")).resolve()
    output.mkdir(parents=True, exist_ok=True)
    atomic_json(output / "plan.json", plan)
    atomic_json(output / "scenario.json", spec)
    if args.validate_only:
        print("仅格式校验通过，未执行Unity业务：" + str(output))
        return 0
    client = RevClient(PROJECT, mode="live", timeout=plan["timeoutSeconds"] + 150)
    started_play = False
    try:
        status = checked(client, "editor.status")["data"]
        atomic_json(output / "initial-editor.json", status)
        if status["compiling"] or status["updating"] or status["paused"]:
            raise RuntimeError("编辑器编译/刷新中或已暂停，请等待其就绪")
        if status["playing"] and not args.attach:
            raise RuntimeError("正在Play；保护现有运行，若要接管请明确传--attach")
        if not status["playing"]:
            checked(client, "editor.play", {"scene": "Assets/Scenes/StartScene.unity"})
            started_play = True
        registered = checked(client, "gm.list")
        atomic_json(output / "gm-list.json", registered)
        names = {c["name"] for c in registered["data"]["commands"]}
        if "RevGame/自动化开始" not in names:
            raise RuntimeError("运行期未注册自动化GM，请检查编译与启动接线")
        # Windows 下持续打开 progress.json 可能与编辑器的原子替换竞争读锁。
        # 此处只等待最终 response；详细进度由作业目录保留，避免观察动作中断长章测试。
        response = client.call("scenario.run", spec)
        atomic_json(output / "response.json", response)
        job_result = Path(response["jobDirectory"]) / "scenario-result.json"
        if job_result.exists():
            result = read_json(job_result)
            atomic_json(output / "scenario-result.json", result)
            export_report(job_result, output / "framework")
        else:
            result = {}
        # 即使框架场景失败，也要尽量保留业务失败位置；不能用finally取消覆盖首个错误。
        business_response = checked(client, "gm.exec", {"command": "RevGame/自动化报告"})
        business = json.loads(business_response["data"]["message"])
        atomic_json(output / "business-report.json", business)
        write_readable(business, output)
        success = (response.get("success") is True and result.get("success") is True
                   and result.get("completed") == result.get("requested") == 1
                   and result.get("failedIterations") == 0 and not result.get("errors")
                   and not result.get("unityErrors") and business["state"] == "passed"
                   and business["unityErrors"] == 0 and business["completedSteps"] == business["totalSteps"]
                   and business.get("boostReleased") is True)
        print(json.dumps({"success": success, "report": str(output), "businessState": business["state"],
                          "rounds": len(business["rounds"]), "error": business["error"]}, ensure_ascii=False), flush=True)
        return 0 if success else 1
    finally:
        # 仅停止本CLI自己启动的Play；--attach保留调用前状态。Unity进程始终保留。
        if started_play:
            client.timeout = 90
            stopped = client.call("editor.stop")
            atomic_json(output / "editor-stop.json", stopped)
            if not stopped.get("success"):
                raise RuntimeError("测试结束但停止Play失败，请查看editor-stop.json")


if __name__ == "__main__":
    raise SystemExit(main())
