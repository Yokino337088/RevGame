// ============================================================
// Events.cs
// 事件名和参数契约集中维护；发出方不依赖界面，监听方按 owner 管理订阅生存期。
// ============================================================
namespace RevGame.Core
{
    /// <summary>
    /// 全局事件名常量。
    ///
    /// 铁律（Revolution 规范）：**事件名必须是字符串常量，且集中在一个类里**；
    /// 同一个事件名只能有一种签名。写在业务代码里手打的字符串（"OnHpChanged"）迟早拼错、
    /// 而且改了没人知道，所以统一放这里，谁用谁引用常量。
    /// </summary>
    public static class Events
    {
        /// <summary>string：吞噬/数量上限等规则事实提示，UI只能播放动画。</summary>
        public const string BoardRuleNotice = "RevGame.Board.RuleNotice";
        /// <summary>HelperShotInfo：帮手已扣积分并提交伤害后的攻击快照。</summary>
        public const string HelperShot = "RevGame.Helper.Shot";
        /// <summary>无参数：准备期整批手牌重抽完成，UI 应播放翻牌后换正面。</summary>
        public const string HandRerolled = "RevGame.Hand.Rerolled";
        /// <summary>唯一签名 PieceAttackInfo：每次真实攻击的起点与棋种快照。</summary>
        public const string PieceAttacked = "RevGame.Piece.Attacked";
        /// <summary>普通棋子攻击范围的只读表现快照，唯一参数签名为PiecePatternInfo。</summary>
        public const string PiecePattern = "RevGame.Piece.Pattern";
        /// <summary>无参数：本轮按配置抽取的准备增益（含保底万箭齐发）已全部生效；HUD读取 PreparationBuffs 播报。</summary>
        public const string PreparationBuffsGranted = "RevGame.Preparation.BuffsGranted";
        // ---------------- 流程 ----------------
        /// <summary>一个回合进入"准备"阶段。参数：回合号（int）。</summary>
        public const string PhaseEnterPrepare = "RevGame.Phase.Prepare";
        /// <summary>一个回合进入"战斗"阶段。参数：回合号（int）。</summary>
        public const string PhaseEnterBattle = "RevGame.Phase.Battle";
        /// <summary>一个回合进入"结算"阶段。参数：本回合胜方（GameSide）。</summary>
        public const string PhaseEnterSettle = "RevGame.Phase.Settle";
        /// <summary>整章通关 / 整局结束。参数：是否通关（bool）。</summary>
        public const string GameFinished = "RevGame.Game.Finished";
        /// <summary>唯一签名BossDefeatedInfo：真实Boss终局胜利，优先于结算UI广播。</summary>
        public const string BossDefeated = "RevGame.Boss.Defeated";
        /// <summary>无参数：Boss入场完成或取消，准备面板/棋盘恢复可见；不改变准备时间和模型。</summary>
        public const string BossIntroFinished = "RevGame.Boss.IntroFinished";
        /// <summary>唯一签名BossDefeatedInfo：击败演出、积分弹幕和章节剧情全部结束后开放结算。</summary>
        public const string BossVictoryPresentationFinished = "RevGame.Boss.VictoryPresentationFinished";
        // ---------------- 棋盘 / 棋子 ----------------
        /// <summary>有棋子落下。参数：棋子的整数 Id。</summary>
        public const string PiecePlaced = "RevGame.Piece.Placed";
        /// <summary>敌方战术落子已提交。唯一签名 (int棋子Id, string战术意图)；HUD只播报，不反向生成棋子。</summary>
        /// <summary>真实伤害结算后的积分到账，签名固定为ScoreGrantedInfo。</summary>
        public const string ScoreGranted = "RevGame.Score.Granted";
        public const string EnemyDeployed = "RevGame.Enemy.Deployed";
        /// <summary>有棋子移动。参数：棋子 Id。</summary>
        public const string PieceMoved = "RevGame.Piece.Moved";
        /// <summary>参数int棋子Id：自动整队已提交，视图播放专用集结动画；不产生第二次移动。</summary>
        public const string PieceFormationMoved = "RevGame.Piece.FormationMoved";
        /// <summary>有棋子死亡（被吃掉）。参数：棋子 Id。</summary>
        public const string PieceDied = "RevGame.Piece.Died";
        /// <summary>棋子血量变化。参数：棋子 Id。</summary>
        /// <summary>Boss控制状态变化，签名Action&lt;int&gt;，参数为本局棋子Id。</summary>
        public const string BossStatusChanged = "RevGame.Piece.BossStatus";
        public const string PieceHpChanged = "RevGame.Piece.HpChanged";
        /// <summary>棋块的围棋状态刷新完毕（气数/叫吃/锁死/连断都算完了）。</summary>
        public const string GoStateRefreshed = "RevGame.Board.GoStateRefreshed";
        /// <summary>棋子原位升星（双牌/准备增益）。参数：棋子 Id（升星后的那个）。</summary>
        public const string PieceMerged = "RevGame.Piece.Merged";
        // ---------------- 战斗表现 ----------------
        /// <summary>一次伤害结算完成。参数：受击棋子 Id。</summary>
        public const string DamageResolved = "RevGame.Battle.DamageResolved";
        /// <summary>完整的单次受击快照，参数 BattleHitInfo；供弹道和飘字使用，不从下一帧血量反推。</summary>
        public const string BattleHit = "RevGame.Battle.Hit";
        /// <summary>战斗结束（限时到 / 一方全灭 / Boss 被擒）。参数：胜方（GameSide）。</summary>
        public const string BattleEnded = "RevGame.Battle.Ended";
        /// <summary>战斗事件（随机"天降帮手"）触发。参数：事件 Id（string）。</summary>
        public const string BattleEventTriggered = "RevGame.Battle.EventTriggered";
        // ---------------- UI ----------------
        /// <summary>手牌变化（抽牌/打牌）。无参数，UI 自己去拉最新手牌。</summary>
        public const string HandChanged = "RevGame.UI.HandChanged";
        /// <summary>选牌或落子失败提示变化。无参数，界面重新读取服务。</summary>
        public const string SelectionChanged = "RevGame.UI.SelectionChanged";
        /// <summary>奖励候选已生成，界面读取三项选项。无参数。</summary>
        public const string PhaseEnterReward = "RevGame.Phase.Reward";
        /// <summary>整局强化/解锁发生变化，局外保存和界面刷新共用。无参数。</summary>
        public const string ProgressChanged = "RevGame.Progress.Changed";
        /// <summary>局外/战斗切换完毕，参数为 GameScreen。表现层在新域开放后装配面板。</summary>
        public const string ScreenChanged = "RevGame.Flow.ScreenChanged";
        /// <summary>需要弹一条战斗日志。参数：文本（string）。</summary>
        public const string BattleLog = "RevGame.UI.BattleLog";

        /// <summary>固定区域预警，唯一签名RegionPulseInfo；格子动画开始时还不扣血。</summary>
        public const string RegionPulseWarning = "RevGame.Battle.RegionPulseWarning";
        /// <summary>炸弹开始落下，唯一签名EnergyBombInfo。</summary>
        public const string EnergyBombWarning = "RevGame.Battle.EnergyBombWarning";
        /// <summary>炸弹真实命中，唯一签名EnergyBombInfo；此时震屏/音效/手机振动。</summary>
        public const string EnergyBombImpact = "RevGame.Battle.EnergyBombImpact";
        // ---------------- Boss 技能 ----------------
        /// <summary>
        /// Boss 开始蓄力一个技能（预警）。参数：<c>BossSkillInfo</c>。
        /// 表现层据此画出危险区域、给玩家留出反应时间；此刻还没有任何伤害。
        /// </summary>
        public const string BossSkillTelegraph = "RevGame.Boss.SkillTelegraph";
        /// <summary>
        /// Boss 技能真正命中的一刻（多段技能每段一次）。参数：<c>BossSkillInfo</c>（Index = 第几段）。
        /// 伤害已在服务里结算完毕，表现层只播放爆炸/激光等特效，不能再次扣血。
        /// </summary>
        public const string BossSkillCast = "RevGame.Boss.SkillCast";
    }
}
