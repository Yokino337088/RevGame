# -*- coding: utf-8 -*-
"""
用 Revolution.Automation CLI 导表。

为什么不用 PowerShell 直接调 rev.cmd：
    配置表目录是中文路径（'配置表'），PowerShell 把参数传给原生进程时会按 GBK 转码，
    中文会变成乱码导致路径找不到。Python 的 subprocess 走 Unicode 参数，不会失真。

用法：
    python tools/_run_excel_export.py plan     # 只预览变更
    python tools/_run_excel_export.py export   # 全量导出（代码 + 数据）
    python tools/_run_excel_export.py read     # 只读取并校验
"""
import subprocess
import sys

REV = r'RevGame_Client\UnityProject\Revolution.Automation\rev.py'
PROJECT = r'RevGame_Client\UnityProject\RevGame.Unity'
SOURCES = r'RevGame_Client\配置表'

action = sys.argv[1] if len(sys.argv) > 1 else 'plan'

args = [
    sys.executable, REV,
    '--mode', 'live',
    '--project', PROJECT,
    '--timeout', '300',
    'excel', action,
    '--source', SOURCES,
]

if action == 'export':
    # 新增了列（BulletSpeed），必须全量导出才会重新生成结构与容器代码；
    # --force 用于跳过"移除旧表"的交互确认。
    args.append('--force')

print('running:', ' '.join(args), flush=True)
proc = subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace')
print('exit code:', proc.returncode)
print('--- stdout ---')
print(proc.stdout)
if proc.stderr.strip():
    print('--- stderr ---')
    print(proc.stderr)
sys.exit(proc.returncode)
