// ============================================================
// IBattleService.cs
// 定义界面和流程可以做的业务动作，拒绝时通过 LastError 返回能读懂的原因。
// ============================================================
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Data;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Services
{
    /// <summary>
    /// 战斗服务契约。
    ///
    /// 【为什么用接口 + 服务定位器，而不是原项目那种 XxxMgr 单例】
    /// 1) 原项目的 `ChessPieceMgr.Instance` / `GameDataMgr.Instance` 互相直接调，谁也测不了、谁也替不掉；
    /// 2) 这里业务只依赖接口，实现由启动入口用 `RevServices.Register&lt;IBattleService, BattleService&gt;()`
    ///    注册一次，任何地方 `RevServices.GetRequired&lt;IBattleService&gt;()` 取 —— 想换实现（比如接入真实数值表）
    ///    只改注册那一行；
    /// 3) 此服务由启动入口拥有，保留本次运行的解锁进度；每局的节奏和表现由战斗域收回。
    ///    BeginCampaign 显式重置棋盘和局内强化，不能把跨局服务误当成每局都会自动销毁。
    ///
    /// 约定：所有方法都在主线程调用（框架本身也是单线程模型）。
    /// </summary>
    public interface IBattleService
    {
        bool VictoryAssistEnabled { get; }
        int BattleEventCount { get; }
        /// <summary>敌方当前AI档位（1～4）；随连续关卡进度提高，界面只能查看，不能自行改难度。</summary>
        int EnemyIntelligence { get; }
        /// <summary>本轮已发放增益；由准备阶段时钟在3秒后一次性产生，界面只负责排队播报。</summary>
        IReadOnlyList<PreparationBuffDef> PreparationBuffs { get; }
        /// <summary>本轮准备已经过的模拟秒数；按钮据此显示三秒发奖倒计时。</summary>
        float PreparationElapsed { get; }
        /// <summary>本轮增益是否已统一生效；为true后即可开战，无需等待所有播报动画结束。</summary>
        bool PreparationGranted { get; }
        /// <summary>实际验证成功的敌阵/空间降级说明，界面只能读取，不能借此修改棋盘。</summary>
        string EnemyFormation { get; }
        /// <summary>当前通关兜底阶段的说明，供玩家看见渐进支援和最终收尾何时生效。</summary>
        string VictorySupportStatus { get; }
        /// <summary>手牌只读视图；消费牌必须走 TryPlayCard，不能由按钮自行删除。</summary>
        IReadOnlyList<HandCard> Hand { get; }

        /// <summary>当前选中的牌编号，0 表示未选；选中只影响落点提示。</summary>
        int SelectedCardId { get; }

        /// <summary>本回合剩余落子额度（并非剩余手牌数）。</summary>
        int CardsRemaining { get; }

        /// <summary>准备期选中的己方棋子编号，0 表示未选。它与手牌/眼位石互斥。</summary>
        int SelectedPieceId { get; }
        /// <summary>统一棋盘点击入口：点己方棋子选中，再点空格免费重摆；有手牌时空格用于落牌。</summary>
        bool HandlePrepareClick(BoardPos pos);
        /// <summary>仅重抽当前剩余手牌，保持张数、星级和剩余落子额度。</summary>
        bool RefreshHand();
        bool SelectCard(int cardId);
        bool CanPlaySelectedAt(BoardPos pos);
        bool TryPlayCard(int cardId, BoardPos pos);
        IReadOnlyList<RewardDef> Rewards { get; }

        int EyeStones { get; }

        bool EyeStoneSelected { get; }

        float TeamAttackBonus { get; }

        int UnlockedChapter { get; }

        bool BeginCampaign(int chapter);
        bool ContinueRound();
        bool ChooseReward(int index);
        bool SkipRound();
        bool SelectEyeStone();
        bool TryPlaceEyeStone(BoardPos pos);
        bool TriggerBattleEvent(BattleEventKind kind);
        /// <summary>当前阶段（准备 / 战斗 / 结算）。</summary>
        GamePhase Phase { get; }

        /// <summary>当前章（1~6）。</summary>
        int Chapter { get; }

        /// <summary>当前回合（1~11）。</summary>
        int Round { get; }

        /// <summary>本回合是否 Boss 回合。</summary>
        bool IsBossRound { get; }

        /// <summary>棋盘数据（只读用途；外部不要直接改它，改棋盘请走服务方法）。</summary>
        BoardModel Board { get; }

        /// <summary>战斗已进行秒数（准备阶段为 0）。</summary>
        float BattleElapsed { get; }

        /// <summary>普通回合90秒；Boss返回正无穷，辅助开启时仍有最后收尾保护。</summary>
        float BattleDuration { get; }

        /// <summary>我方棋子（只读快照，UI 遍历用）。</summary>
        IReadOnlyList<Piece> PlayerPieces { get; }

        /// <summary>敌方棋子（只读快照）。</summary>
        IReadOnlyList<Piece> EnemyPieces { get; }

        /// <summary>最近一次结算的胜方；未结算为 null。</summary>
        GameSide? LastWinner { get; }

        /// <summary>
        /// 开一个新回合（会清空敌方、重建棋盘、保留我方棋子 —— 策划案：布局与状态全部保留）。
        /// 参数越界返回 false，并把原因写进 <see cref = "LastError"/>。
        /// </summary>
        bool StartRound(int chapter, int round);
        /// <summary>
        /// 准备阶段落一张牌。成功返回 true。
        /// 失败原因写进 <see cref = "LastError"/>（阶段不对 / 格子被占 / 越界 / 牌数超上限）。
        /// </summary>
        bool TryPlayCard(PieceKind kind, StarTier star, BoardPos pos);
        /// <summary>准备阶段结束，进入战斗（慢节奏全自动）。</summary>
        bool StartBattle();
        /// <summary>
        /// 推进战斗一帧。由域逻辑（<c>BattleLogic</c>）用 <c>Every</c> 固定步长驱动，
        /// **不要**在 MonoBehaviour 的 Update 里直接调 —— 那样结果不可复现。
        /// </summary>
        void Tick(float deltaSeconds);
        /// <summary>结算阶段读完战报后调用：结束本回合，返回是否整局结束（通关 / 失败）。</summary>
        bool CompleteSettle();
        /// <summary>最近一次失败的原因（空字符串 = 没有失败）。UI 直接显示，方便排查。</summary>
        string LastError { get; }
    }
}
