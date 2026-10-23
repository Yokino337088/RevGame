# -*- coding: utf-8 -*-
"""
用 CLI 执行 GM 命令（含中文命令名）。

为什么不用 PowerShell 直接调 rev.cmd：
    PowerShell 把中文参数传给原生进程时按 GBK 转码，'RevGame/快照' 会变成 'RevGame/蹇収'，
    GM 表里找不到这条命令。Python subprocess 走 Unicode 参数，不会失真。

用法：
    python tools/_gm.py "RevGame/快照"
    python tools/_gm.py "RevGame/Boss技能 3"
"""
import subprocess
import sys

REV = r'g:\RevRoot\MyRevGame\RevGame_Client\UnityProject\Revolution.Automation\rev.py'
PROJECT = r'g:\RevRoot\MyRevGame\RevGame_Client\UnityProject\RevGame.Unity'

command = sys.argv[1] if len(sys.argv) > 1 else 'RevGame/快照'

args = [
    sys.executable, REV,
    '--mode', 'live',
    '--project', PROJECT,
    '--timeout', '120',
    'gm', 'exec', command,
]

proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace')
print('exit code:', proc.returncode)
print(proc.stdout)
if proc.stderr.strip():
    print('--- stderr ---')
    print(proc.stderr)
sys.exit(proc.returncode)
