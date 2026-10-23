"""已打开Unity的项目专用入口。有限动作，绝不运行传入C#或任意菜单。"""
import argparse
import json
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('action', choices=['prepare', 'build-pc', 'verify-rules', 'verify-play', 'check-result'])
args = parser.parse_args()
root = Path(__file__).resolve().parents[1] / 'RevGame_Client/UnityProject/RevGame.Unity/Library/RevGameCombatUpgrade'
root.mkdir(parents=True, exist_ok=True)
request, response = root / 'request.txt', root / 'result.json'
if request.exists():
    raise SystemExit('已有未处理工单，请先检查编辑器状态')
response.unlink(missing_ok=True)
request.write_text(args.action, encoding='utf-8')
deadline = time.monotonic() + (360 if args.action == 'build-pc' else 90)
while time.monotonic() < deadline:
    if response.exists():
        try:
            result = json.loads(response.read_text(encoding='utf-8-sig'))
        except (json.JSONDecodeError, PermissionError):
            time.sleep(.2)
            continue
        print(json.dumps(result, ensure_ascii=False, indent=2))
        raise SystemExit(0 if result['success'] else 1)
    time.sleep(.25)
raise SystemExit('工单超时；请求保留供排查，不能因此宣称执行成功')
