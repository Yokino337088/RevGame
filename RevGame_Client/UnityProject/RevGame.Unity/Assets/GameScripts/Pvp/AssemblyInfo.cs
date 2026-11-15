using System.Runtime.CompilerServices;

// 对局界面和启动诊断共用 PvP 的分析与会话状态，避免将内部实现改成公开 API。
[assembly: InternalsVisibleTo("RevGame.Runtime")]
[assembly: InternalsVisibleTo("RevGame.Bootstrap")]
