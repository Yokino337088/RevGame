using System.Runtime.CompilerServices;

// 玩法服务可修改共享棋盘、棋子与手牌模型，表现层沿用项目内部查询。
[assembly: InternalsVisibleTo("RevGame.Campaign")]
[assembly: InternalsVisibleTo("RevGame.Endless")]
[assembly: InternalsVisibleTo("RevGame.Runtime")]
