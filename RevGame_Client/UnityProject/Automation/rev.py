#!/usr/bin/env python3
"""Revolution automation CLI; Python 3.10+, standard library only."""
import sys
from revautomation.cli import main

if __name__ == "__main__":
    # Windows 重定向输出也统一 UTF-8，避免 Codex/CI 读取中文结果时乱码。
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    raise SystemExit(main())
