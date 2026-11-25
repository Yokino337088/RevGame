"""CLI 共用的错误类型。

单独成文件的原因：快速转发路径要在 Python 启动后立刻连接常驻服务，不能为了拿一个异常类
就把整个 client（subprocess、uuid、datetime…）导入进来，那是白花的毫秒。
"""


class RevError(RuntimeError):
    pass


class RevCancelled(RevError):
    pass


class RevCompilationError(RevError):
    """在线编辑器确认编译失败，可附带本进程完成的结构化诊断。"""

    def __init__(self, diagnostics):
        """保留实际编译记录；旧桥或记录未完成时 diagnostics 为空。"""
        super().__init__("Unity 脚本编译失败。用 editor compilation 和 editor console --severity error 定位，修复后重新编译。")
        self.diagnostics = diagnostics
