"""通用 Python 编排模板：先实现 business.scenario.json 中的业务 GM 再执行。"""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.client import RevClient, read_json
from revautomation.cli import validate_scenario


def main():
    """启动示例场景执行循环用例，仅在显式传入报告目录时保留报告。"""
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scene", required=True, help="Assets/.../*.unity")
    parser.add_argument("--unity", help="Unity.exe；已开编辑器时可不填")
    parser.add_argument("--repeat", type=int, default=10000)
    parser.add_argument("--report-dir", type=Path, help="需要报告时指定项目文档目录")
    args = parser.parse_args()
    automation = Path(__file__).resolve().parents[1]
    spec = read_json(automation / "examples/business.scenario.json")
    spec["repeat"] = args.repeat
    validate_scenario(spec)
    client = RevClient(automation.parent / "RevolutionFrameWork_Unity", unity=args.unity, timeout=86400)
    client.launch()
    start = client.call("editor.play", {"scene": args.scene})
    if not start["success"]:
        raise RuntimeError(start)
    try:
        response = client.call("scenario.run", spec, progress=lambda value: print(value, file=sys.stderr),
                               report_dir=args.report_dir)
        print(response)
        return 0 if response["success"] and not response.get("reportError") else 1
    finally:
        # 收尾也有明确期限，不能沿用压测的一整天超时。
        client.timeout = 120
        stopped = client.call("editor.stop")
        if not stopped["success"]:
            print(stopped, file=sys.stderr)


if __name__ == "__main__":
    raise SystemExit(main())
