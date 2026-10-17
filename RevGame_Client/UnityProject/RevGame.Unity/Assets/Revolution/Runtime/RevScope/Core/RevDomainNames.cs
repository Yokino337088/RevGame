// ============================================================
// RevDomainNames.cs —— 预置的"域名"常量（防止手写字符串拼错）
//
// 位置：Runtime\RevScope\Core\
//
// 【域是什么】一段"时间"的名字：大厅（Lobby）、战斗（Battle）、新手教程……
//   进入这段时间叫 Enter，结束这段时间叫 Exit；结束时，这段时间里没人管的东西（计时器 / 事件监听 /
//   每帧回调 / 对象池 / 资源 / 界面 / 业务单元）由框架一次性收干净。
//
// 【为什么域名是 string 而不是 enum】
//   不同项目的"阶段"千差万别（有的有"副本"、有的有"公会战"）。写成 enum 就得改框架才能加一个阶段。
//   所以域名是字符串 —— 但**手写字符串会拼错**（"Batle" 编译不报错，只会在运行时变成"另一个域"），
//   所以框架预置最常用的三个常量，你自己的阶段也请照这个样子在业务层集中定义：
//
//       public static class GameDomain
//       {
//           public const string Dungeon = "Dungeon";
//           public const string GuildWar = "GuildWar";
//       }
//
//   （和事件系统的 GameEventId 是同一个约定：集中定义静态常量，拼错编译不过。）
// ============================================================
namespace Revolution
{
    /// <summary>预置的域名常量。自己的阶段请在业务层仿照它集中定义。</summary>
    public static class RevDomain
    {
        /// <summary>进程级：整个游戏只进一次，退出游戏才关（放"从启动活到退出"的东西）。</summary>
        public const string Global = "Global";

        /// <summary>大厅：登录后到进战斗之前。</summary>
        public const string Lobby = "Lobby";

        /// <summary>战斗：一局游戏开始到结束。</summary>
        public const string Battle = "Battle";
    }
}
