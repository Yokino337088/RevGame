// ============================================================
// GameDomain.cs
// 用业务自己的域名划分大厅和战斗，避免为每个小游戏去修改框架内置枚举。
// ============================================================
namespace RevGame.Core
{
    /// <summary>
    /// 业务域常量（Revolution 的 <c>RevScope</c> / <c>[RevLogic]</c> 用）。
    ///
    /// 为什么要有：
    /// 一局战斗会开一堆东西 —— 计时器、事件监听、对象池、服务（都是"域内资源"）。
    /// 用域（Scope）把它们圈起来，退出时**一次收干净**，就不会出现"上一局的计时器还在跑、
    /// 上一局的监听还在收事件"这种典型泄漏。域名用 const string 集中声明，
    /// **不要**去改框架的 RevDomain 枚举。
    /// </summary>
    public static class GameDomain
    {
        /// <summary>一局战斗（准备 → 战斗 → 结算，跨多个回合）。</summary>
        public const string Battle = "RevGame.Battle";
        /// <summary>局外主界面与章节选择；离开时由流程统一关闭当前大厅域。</summary>
        public const string Lobby = "RevGame.Lobby";
    }
}
