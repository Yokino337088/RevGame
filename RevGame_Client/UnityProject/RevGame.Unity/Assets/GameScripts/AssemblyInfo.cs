#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Runtime.CompilerServices;

// 启动程序集在开发态接入流程状态与诊断入口，正式包无需访问这些成员。
[assembly: InternalsVisibleTo("RevGame.Bootstrap")]
#endif
