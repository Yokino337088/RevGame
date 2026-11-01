"""Explicit real stdio benchmark. Read-only; never opens or stops Unity."""
import argparse
import json
from pathlib import Path
import queue
import statistics
import subprocess
import sys
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not 3 <= args.samples <= 100:
        parser.error("samples must be 3..100")
    root = Path(__file__).resolve().parents[1]
    process = subprocess.Popen([sys.executable, str(root / "rev_mcp.py"), "--project", str(args.project),
                                "--reports", str(args.output.parent / "requests"), "serve"],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    replies = queue.Queue()
    reader = threading.Thread(target=lambda: [replies.put(line) for line in iter(process.stdout.readline, b"")], daemon=True)
    reader.start()
    ident = 0

    def request(method, params):
        nonlocal ident
        ident += 1
        payload = dict(jsonrpc="2.0", id=ident, method=method, params=params)
        started = time.perf_counter()
        process.stdin.write(json.dumps(payload).encode("utf-8") + b"\n")
        process.stdin.flush()
        response = json.loads(replies.get(timeout=130))
        elapsed = (time.perf_counter() - started) * 1000
        if response.get("id") != ident or "error" in response:
            raise RuntimeError("MCP protocol or identity failure")
        return response["result"], elapsed

    try:
        request("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="RevMCP.Benchmark", version="1")))
        process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        tools, discovery_ms = request("tools/list", {})
        times = []
        for _ in range(args.samples):
            result, elapsed = request("tools/call", dict(name="rev_call", arguments=dict(target="editor", action="editor.status")))
            if result.get("isError") or not result["structuredContent"].get("success"):
                raise RuntimeError("editor.status failed; no performance pass")
            times.append(elapsed)
        ordered = sorted(times)
        data = dict(success=True, transport="real MCP stdio -> Unity file bridge", action="editor.status", samples=args.samples,
                    discoveryMs=discovery_ms, coldMs=times[0], meanMs=statistics.mean(times), medianMs=statistics.median(times),
                    p95Ms=ordered[(95 * len(ordered) + 99) // 100 - 1], samplesMs=times,
                    scope="Read-only editor calls. No gameplay, GPU, Player or device performance claim.")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({k: v for k, v in data.items() if k != "samplesMs"}, ensure_ascii=False))
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
