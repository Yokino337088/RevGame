"""CLI 共用的错误类型。

单独成文件的原因：快速转发路径要在 Python 启动后立刻连接常驻服务，不能为了拿一个异常类
就把整个 client（subprocess、uuid、datetime…）导入进来，那是白花的毫秒。
"""


class RevError(RuntimeError):
    pass


class RevCancelled(RevError):
    pass
