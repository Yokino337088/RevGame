"""Measure MCP stdin/stdout token cost vs CLI for the same real editor call.

Only reads editor.status; never mutates the project.
"""
import argparse
import json
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

import tiktoken

ENC = tiktoken.get_encoding("o200k_base")


def tokens(text: str) -> int:
    return len(ENC.encode(text))


def run_server(root: Path, project: Path, compact: bool, calls: int) -> dict:
    argv = [sys.executable, str(root / "rev_mcp.py"), "--project", str(project), "serve"]
    if compact:
        argv.insert(-1, "--compact-tools")
    proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    replies = queue.Queue()
    threading.Thread(target=lambda: [replies.put(line) for line in iter(proc.stdout.readline, b"")], daemon=True).start()
    ident = 0
    out = {"mode": "compact" if compact else "full"}

    def request(method, params):
        nonlocal ident
        ident += 1
        payload = json.dumps({"jsonrpc": "2.0", "id": ident, "method": method, "params": params}, ensure_ascii=False)
        proc.stdin.write(payload.encode("utf-8") + b"\n")
        proc.stdin.flush()
        raw = replies.get(timeout=180)
        return json.loads(raw), raw.decode("utf-8").strip()

    try:
        _, _ = request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "token-audit", "version": "1"}})
        proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        proc.stdin.flush()
        _, raw_tools = request("tools/list", {})
        parsed = json.loads(raw_tools)
        out["tools"] = len(parsed["result"]["tools"])
        out["toolsListTokens"] = tokens(raw_tools)

        result, raw_call = request("tools/call", {"name": "rev_call",
                                                 "arguments": {"target": "editor", "action": "editor.status", "arguments": {}}})
        out["callTokens"] = tokens(raw_call)
        out["callBytes"] = len(raw_call.encode("utf-8"))
        structured = result["result"].get("structuredContent")
        out["callOk"] = bool(structured)

        if compact:
            disc, raw_disc = request("tools/call", {"name": "rev_discover",
                                                    "arguments": {"target": "editor", "query": "status"}})
            out["discoverTokens"] = tokens(raw_disc)
            out["discoverItems"] = len((disc["result"].get("structuredContent") or {}).get("actions", []))
    finally:
        proc.stdin.close()
        proc.wait(timeout=20)
        proc.stdout.close()
        proc.stderr.close()
    return out


def run_cli(root: Path, project: Path) -> dict:
    cli = root.parent / "Revolution.RevCLI" / "rev.py"
    started = time.time()
    proc = subprocess.run([sys.executable, str(cli), "--project", str(project), "--mode", "live",
                           "call", "editor.status"], capture_output=True, text=True, encoding="utf-8")
    elapsed = time.time() - started
    return {"exitCode": proc.returncode, "stdoutTokens": tokens(proc.stdout),
            "stdoutBytes": len(proc.stdout.encode("utf-8")), "seconds": round(elapsed, 3)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", required=True, type=Path)
    ap.add_argument("--calls", type=int, default=1)
    ap.add_argument("--output", required=True, type=Path)
    args = ap.parse_args()
    root = Path(__file__).resolve().parents[3] / "Revolution.RevMCP"
    report = {"project": str(args.project)}

    manifest = Path(__file__).resolve().parents[2] / "Revolution.RevCLI" / "commands.json"
    text = manifest.read_text(encoding="utf-8-sig")
    report["cliManifest"] = {"tokens": tokens(text), "bytes": len(text.encode("utf-8")),
                             "actions": len(json.loads(text)["actions"])}

    report["mcpFull"] = run_server(root, args.project, False, args.calls)
    report["mcpCompact"] = run_server(root, args.project, True, args.calls)
    report["cliCall"] = run_cli(root, args.project)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
