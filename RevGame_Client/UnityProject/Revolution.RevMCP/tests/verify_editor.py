"""Run an explicitly supplied Editor plan over real MCP stdio; no GM/Play prerequisite."""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    process = subprocess.Popen([sys.executable, str(root / "rev_mcp.py"), "--project", str(args.project),
                                "--reports", str(args.output.parent / "editor-requests"), "--compact-tools", "serve"],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    replies = queue.Queue()
    reader = threading.Thread(target=lambda: [replies.put(line) for line in iter(process.stdout.readline, b"")], daemon=True)
    reader.start()
    ident = 0

    def request(method, params):
        nonlocal ident
        ident += 1
        process.stdin.write(json.dumps(dict(jsonrpc="2.0", id=ident, method=method, params=params), ensure_ascii=False).encode("utf-8") + b"\n")
        process.stdin.flush()
        response = json.loads(replies.get(timeout=180))
        if response.get("id") != ident or "error" in response:
            raise RuntimeError("MCP identity/protocol failure")
        return response["result"]

    try:
        request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="RevMCP.EditorVerification", version="1")))
        process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        tools = request("tools/list", {})["tools"]
        if len(tools) != 3:
            raise RuntimeError("Compact tool list must contain three stable tools")
        plan = json.loads(args.plan.read_text(encoding="utf-8-sig"))
        result = request("tools/call", dict(name="rev_run", arguments=dict(plan=plan)))
        data = result["structuredContent"]
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(dict(success=data.get("success") and not result.get("isError"),
                              steps=len(data.get("steps", [])), cleanup=len(data.get("cleanup", [])),
                              compactTools=len(tools)), ensure_ascii=False))
        if result.get("isError") or not data.get("success"):
            raise RuntimeError("Editor plan failed; inspect complete output, including cleanup")
    finally:
        process.stdin.close()
        process.wait(timeout=15)
        error = process.stderr.read().decode("utf-8", errors="replace")
        process.stdout.close()
        process.stderr.close()
        if process.returncode:
            raise RuntimeError(error)


if __name__ == "__main__":
    main()
