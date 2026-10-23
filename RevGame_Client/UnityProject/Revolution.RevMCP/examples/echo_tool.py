"""最小外部 Python 接口：JSON stdin -> JSON stdout。

把真实 CLI 包在这个形状的适配器里即可注册给 Agent。stdout 只打印机器结果；
日志写 stderr；业务失败既返回 success=false，也用非零退出码。
"""
import json
import sys

request = json.load(sys.stdin)
print(json.dumps(dict(success=True, text=request["text"]), ensure_ascii=False))
