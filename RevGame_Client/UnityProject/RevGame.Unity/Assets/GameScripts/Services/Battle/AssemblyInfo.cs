using System.Runtime.CompilerServices;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// 启动程序集内的开发自动化共用远征测试夹具及强化租约。
[assembly: InternalsVisibleTo("RevGame.Bootstrap")]
#endif

#if UNITY_EDITOR
// 编辑器下只允许业务流程程序集从隔离首页设置远征测试种子。
[assembly: InternalsVisibleTo("RevGame.Runtime")]
#endif
