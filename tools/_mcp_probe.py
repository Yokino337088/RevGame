# -*- coding: utf-8 -*-
"""
校验两份 MCP 配置，并按配置里的命令实际拉起 RevMCP 走一次 MCP 握手。

为什么值得单独跑：
    配置文件写错（JSON 逗号、TOML 引号、路径反斜杠）时，客户端只会静默显示"连不上"，
    很难定位。这里直接解析配置 + 用配置里的 command/args 拉起进程，两步都能立刻看到原因。
"""
import json
import subprocess
import sys
import tomllib
from pathlib import Path

CODEBUDDY = Path.home() / '.codebuddy' / 'mcp.json'
CODEX = Path.home() / '.codex' / 'config.toml'

print('=== 1. 校验 CodeBuddy mcp.json ===')
cb = json.loads(CODEBUDDY.read_text(encoding='utf-8'))
print('  servers:', list(cb.get('mcpServers', {}).keys()))

print()
print('=== 2. 校验 Codex config.toml ===')
with open(CODEX, 'rb') as f:
    codex = tomllib.load(f)
print('  mcp_servers:', list(codex.get('mcp_servers', {}).keys()))

server = cb['mcpServers']['revolution']
command = server['command']
args = server['args']

print()
print('=== 3. 按配置拉起 RevMCP 并握手 ===')
print('  command:', command)
print('  args   :', ' '.join(args[-2:]))

proc = subprocess.Popen(
    [command] + args,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    encoding='utf-8',
    errors='replace',
    bufsize=1,
)

requests = [
    {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "probe", "version": "1.0"},
    }},
    {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
    {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
]

for req in requests:
    proc.stdin.write(json.dumps(req) + '\n')
    proc.stdin.flush()

import time
deadline = time.time() + 30
got = {}
while time.time() < deadline and len(got) < 2:
    line = proc.stdout.readline()
    if not line:
        break
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
    except json.JSONDecodeError:
        continue
    rid = msg.get('id')
    if rid in (1, 2):
        got[rid] = msg

proc.terminate()

init = got.get(1, {})
if 'result' in init:
    info = init['result'].get('serverInfo', {})
    print('  initialize OK ->', info.get('name'), info.get('version'))
else:
    print('  initialize 失败:', json.dumps(init, ensure_ascii=False)[:300])

tools = got.get(2, {}).get('result', {}).get('tools', [])
print('  tools/list ->', len(tools), '个工具')
for t in tools[:8]:
    print('    -', t.get('name'))

err = proc.stderr.read()
if err.strip():
    print()
    print('  stderr:', err.strip()[:500])

sys.exit(0 if got.get(1, {}).get('result') and tools else 1)
