#!/usr/bin/env python3
"""RevMCP 启动入口。Python 3.10+，仅使用标准库，无需安装 pip 依赖。

serve 给 Agent 讲 MCP 协议；discover/call/run 给人和 CI 提供同一套 CLI。
Unity Editor 能力复用旁边的 Revolution.RevCLI，因此必须保留两个目录。
"""
from pathlib import Path
import argparse
import json
import sys


def main():
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description="Revolution 统一 MCP：Editor、游戏运行时、GM、显式注册 CLI/Python")
    parser.add_argument("--project", type=Path, help="Unity 工程目录；Editor 后端只连接已打开的工程")
    parser.add_argument("--runtime-endpoint", type=Path, help="开发版游戏产生的 runtime.json 凭据文件")
    parser.add_argument("--cli-root", "--automation-root", dest="cli_root", type=Path, default=root.parent / "Revolution.RevCLI",
                        help="RevCLI 目录（含 commands.json）；--automation-root 为改名前的旧名，仍可用")
    parser.add_argument("--external-manifest", type=Path, help="显式注册的外部工具清单")
    parser.add_argument("--reports", type=Path, default=root / "results")
    parser.add_argument("--timeout", type=float, default=120)
    sub = parser.add_subparsers(dest="operation", required=True)
    sub.add_parser("serve", help="MCP stdio 服务；stdout 仅输出 JSON-RPC")
    sub.add_parser("discover", help="读取实际注册能力")
    call = sub.add_parser("call", help="调用真实能力")
    call.add_argument("target", choices=("editor", "game", "gm", "cli"))
    call.add_argument("action")
    call.add_argument("--json", type=Path, help="参数 JSON 文件，不是 JSON 字符串")
    call.add_argument("--confirm", action="store_true")
    run = sub.add_parser("run", help="运行带断言和 finally 的测试计划")
    run.add_argument("plan", type=Path)
    args = parser.parse_args()
    if not 0 < args.timeout <= 3600:
        parser.error("--timeout must be 0..3600")
    if not (args.cli_root / "revautomation/client.py").is_file():
        parser.error("Revolution.RevCLI not found; set --cli-root")
    sys.path.insert(0, str(args.cli_root.resolve()))
    from revautomation.client import read_json
    from revmcp.backends import Hub
    from revmcp.server import Server
    from revmcp.workflow import run as run_plan
    hub = Hub(args.project, args.runtime_endpoint, args.cli_root, args.external_manifest, args.reports, args.timeout)
    if args.operation == "serve":
        Server(hub).serve()
        return 0
    try:
        if args.operation == "discover":
            result = hub.discover()
        elif args.operation == "call":
            result = hub.call(args.target, args.action, read_json(args.json) if args.json else {}, args.confirm)
        else:
            result = run_plan(hub, read_json(args.plan))
        result["reportPath"] = hub.save(result)
    except (OSError, ValueError, RuntimeError, TimeoutError, KeyboardInterrupt) as exc:
        result = dict(success=False, error=type(exc).__name__, message=str(exc))
        result["reportPath"] = hub.save(result)
    # Reconfigure only CLI output; MCP writes explicit UTF-8 bytes itself.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))
    return 0 if result.get("success") else 1


if __name__ == "__main__":
    raise SystemExit(main())
