#!/usr/bin/env python3
"""Revolution automation CLI; Python 3.10+, standard library only."""
import sys

if __name__ == "__main__":
    # 必须在导入任何模块之前检查：系统默认的 python 可能是旧版本，直接跑只会得到一串看不懂的语法错误。
    if sys.version_info < (3, 10):
        sys.stderr.write('{"success": false, "error": "PythonTooOld", "message": "RevCLI 需要 Python 3.10+，当前是 %d.%d（%s）。运行 py -3 -c \\"import sys;print(sys.executable)\\" 找到新版解释器后用它执行。"}\n'
                         % (sys.version_info[0], sys.version_info[1], sys.executable.replace("\\", "/")))
        raise SystemExit(2)
    # Windows 重定向输出也统一 UTF-8，避免 Codex/CI 读取中文结果时乱码。
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    # 先试快速转发：常驻服务在跑时不必导入 argparse 与整个 cli，省下约 200ms；不适合转发则原样走完整 CLI。
    from revautomation import fastpath
    code = fastpath.try_run(sys.argv[1:])
    if code is not None:
        raise SystemExit(code)
    from revautomation.cli import main
    raise SystemExit(main())
