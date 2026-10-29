"""显式运行的真实 MCP/Unity 验证，不属于无环境依赖的 unittest。

先运行 RevAutomationDemo，再传 --project 或 --runtime-endpoint。本程序通过
stdio 握手、发现、完整测试计划验证游戏，不以直接调用 Python 函数代替 MCP。
"""
import argparse
import base64
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path)
    parser.add_argument("--runtime-endpoint", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--plan", type=Path, help="可选自定义计划；默认仍验证教学场景 UI/GM")
    args = parser.parse_args()
    if not args.project and not args.runtime_endpoint:
        parser.error("Choose a real Unity instance")
    command = [sys.executable, str(root / "rev_mcp.py"), "--reports", str(args.output.parent / "mcp-reports"), "--timeout", "30"]
    if args.project:
        command += ["--project", str(args.project)]
    if args.runtime_endpoint:
        command += ["--runtime-endpoint", str(args.runtime_endpoint)]
    process = subprocess.Popen(command + ["serve"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    output = queue.Queue()
    reader = threading.Thread(target=lambda: [output.put(line) for line in iter(process.stdout.readline, b"")], daemon=True); reader.start()
    ident = 0
    def request(method, params=None):
        nonlocal ident
        ident += 1
        process.stdin.write(json.dumps(dict(jsonrpc="2.0", id=ident, method=method, params=params or {}), ensure_ascii=False).encode("utf-8") + b"\n"); process.stdin.flush()
        response = json.loads(output.get(timeout=90))
        if response.get("id") != ident or "error" in response:
            raise RuntimeError(response)
        return response["result"]
    try:
        initialized = request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="RevMCP.UnityVerification", version="1")))
        process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); process.stdin.flush()
        tools = request("tools/list")["tools"]
        if not any(t["name"].startswith("gm_") for t in tools):
            raise RuntimeError("No actual registered GM tools")
        plan = json.loads((args.plan or (root / "examples/ui-gm.plan.json")).read_text(encoding="utf-8"))
        result = request("tools/call", dict(name="rev_run", arguments=dict(plan=plan)))
        images = [c for c in result["content"] if c["type"] == "image"]
        for i, image in enumerate(images):
            png = base64.b64decode(image["data"], validate=True)
            if not png.startswith(b"\x89PNG\r\n\x1a\n"):
                raise RuntimeError("Invalid PNG evidence")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            (args.output.parent / (args.output.stem + f"-{i}.png")).write_bytes(png)
        evidence = dict(success=not result.get("isError") and bool(images), protocolVersion=initialized["protocolVersion"],
            toolCount=len(tools), gmToolCount=sum(t["name"].startswith("gm_") for t in tools), imageCount=len(images), result=result["structuredContent"])
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({k: v for k, v in evidence.items() if k != "result"}, ensure_ascii=False))
        if not evidence["success"]:
            raise RuntimeError("MCP plan or screenshot failed; inspect " + str(args.output))
    finally:
        process.stdin.close(); process.wait(timeout=15)
        error = process.stderr.read().decode("utf-8", errors="replace")
        process.stdout.close(); process.stderr.close()
        if process.returncode:
            raise RuntimeError(error)


if __name__ == "__main__":
    main()
