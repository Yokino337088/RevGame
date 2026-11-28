using System.Runtime.CompilerServices;

// 玩法服务可修改共享棋盘、棋子与手牌模型，表现层沿用项目内部查询。
[assembly: InternalsVisibleTo("RevGame.Campaign")]
[assembly: InternalsVisibleTo("RevGame.Endless")]
[assembly: InternalsVisibleTo("RevGame.Runtime")]
#if UNITY_EDITOR
// 编辑器性能入口须在同一盘面对比串行与并行评分。播放器继续通过正常走位入口选择执行方式。
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
#endif
