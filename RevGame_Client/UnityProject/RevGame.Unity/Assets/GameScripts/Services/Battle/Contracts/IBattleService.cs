// ============================================================
// IBattleService.cs —— 战斗服务的“对外契约”，也是 Services 目录存在的理由。
// 界面、流程、GM 只依赖这个接口；谁实现、怎么实现由启动入口注册一次决定。
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
    /// 战斗服务契约。新手请先看下面三段，再读成员。
    ///
    /// 【一、Services 目录是干什么的】
    /// 这里放“一局战斗/一整次运行都要用的业务能力”，例如落子、判胜、发奖励。
    /// 它不是 UI（面板只管显示），也不是纯工具（无状态算法放 Battle/AI、Board/Rules），
    /// 而是**有状态、跨多个界面复用、必须唯一**的业务对象。当前项目只有这一套战斗服务，
    /// 所以目录里全部是 IBattleService 与它的分部实现 BattleService.*.cs。
    ///
    /// 【二、它是不是用来代替单例的】
    /// 目的相同（全局只有一个实例、哪都能取到），但做法和后果不同：
    /// 1) 单例（XxxMgr.Instance）把“实现类型”写死在调用方，测试时无法替换，也无法同时存在两份；
    /// 2) 这里调用方只写接口 + RevServices.GetRequired&lt;IBattleService&gt;()，
    ///    注册语句只有启动入口一行：RevServices.Register&lt;IBattleService, BattleService&gt;()；
    ///    想换实现、想在测试里塞假实现，只改注册处，不动业务和界面；
    /// 3) 单例通常是 static 字段 + 自己管初始化顺序；RevServices 由框架统一登记、查重和注销，
    ///    漏注册会当场抛出中文原因，而不是给你一个 null。
    /// 换句话说：**用服务定位器替代单例，而不是换一个名字继续写单例**。
    ///
    /// 【三、它和框架哪些概念配合】
    /// · RevServices：注册/取用/注销（本接口由启动入口注册，随运行期存活）。
    /// · RevScope + RevLogic：BattleLogic 属于战斗域，域关闭就停 Tick；服务本身不建 Timer。
    /// · RevTimer：节奏由 BattleLogic 的 Every(固定小步) 驱动，保证可复现，不在 MonoBehaviour.Update 里推。
    /// · RevEvent：服务只广播 HandChanged / PiecePlaced / PhaseEnterBattle 等事实，UI 自己听。
    /// · RevHeavyFSM：敌方 AI 的状态机由服务持有并驱动（见 Battle/AI），但胜负仍只由本服务判定。
    /// · RevLog / LastError：失败必须带原因，禁止静默 return 或只打日志。
    /// · Excel 导表：血量、增益、节奏等策划数值全部读 Balance / PieceCatalog，不在本契约里写死。
    ///
    /// 约定：所有方法都在主线程调用；返回 false 时必须读 <see cref = "LastError"/> 得到中文原因。
    /// </summary>
    public interface IBattleService
    {
        /// <summary>本回合积分换回合清零；永久总积分在本机跨挑战保存；倍率随进度和存活棋数变化。</summary>
        long RoundScore { get; }
        long LifetimeScore { get; }
        float ScoreMultiplier { get; }
        void FlushScore();
        // ---------------- 只读状态（界面/GM 观察用，不能从这里改数据） ----------------
        /// <summary>通关辅助是否开启；开启时含最后一子保护、阶段支援，AI不能绕开此统一伤害规则。</summary>
        bool VictoryAssistEnabled { get; }
        /// <summary>本轮已发生的玩家战斗增益次数，不含准备增益与敌方落子。</summary>
        int BattleEventCount { get; }
        /// <summary>本轮敌方战斗补子成功次数；用于监测总预算，不允许调用方修改。</summary>
        int EnemyDeploymentCount { get; }
        /// <summary>下一次敌方尝试补子的战斗时间；达到时间仍须通过人数、空间、气眼检查。</summary>
        float NextEnemyDeploymentAt { get; }
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

        /// <summary>
        /// 手牌只读视图。返回的是内部列表的只读包装，不是拷贝：
        /// 界面遍历没问题，但**删除/添加必须走 TryPlayCard、RefreshHand**，
        /// 否则服务里的额度、组合升级和事件都会和画面不一致。
        /// </summary>
        IReadOnlyList<HandCard> Hand { get; }

        /// <summary>当前选中的牌编号，0 表示未选；选中只影响落点提示。</summary>
        int SelectedCardId { get; }

        /// <summary>本回合剩余落子额度（并非剩余手牌数）：刷新手牌不会恢复已用掉的额度。</summary>
        int CardsRemaining { get; }

        /// <summary>准备或战斗中选中的己方棋子编号，0 表示未选。它与手牌/眼位石互斥。</summary>
        int SelectedPieceId { get; }

        /// <summary>奖励三选一的候选；生成后只能 ChooseReward 消费一次，重复点击必须失败。</summary>
        IReadOnlyList<RewardDef> Rewards { get; }

        /// <summary>持有的眼位石数量；0 时准备面板直接隐藏按钮，不能用它可以凭空补眼。</summary>
        int EyeStones { get; }

        /// <summary>当前是否处于“点一个金色空格补眼”的选择状态；与手牌、棋子选择互斥。</summary>
        bool EyeStoneSelected { get; }

        /// <summary>本局累计的全队攻击加成（跳过回合、奖励等来源），只读用于 HUD 显示。</summary>
        float TeamAttackBonus { get; }

        /// <summary>已解锁到第几章；选关界面据此决定哪些章节按钮可点。</summary>
        int UnlockedChapter { get; }

        /// <summary>当前阶段（准备 / 战斗 / 结算）。所有写操作都先检查它。</summary>
        GamePhase Phase { get; }

        /// <summary>当前章（1~6）。</summary>
        int Chapter { get; }

        /// <summary>当前回合（1~10）。</summary>
        int Round { get; }

        /// <summary>本回合是否 Boss 回合（擒王、不限时）。</summary>
        bool IsBossRound { get; }

        /// <summary>棋盘数据（只读用途；外部不要直接改它，改棋盘请走服务方法）。</summary>
        BoardModel Board { get; }

        /// <summary>战斗已进行秒数（准备阶段为 0）。</summary>
        float BattleElapsed { get; }

        /// <summary>普通回合90秒；Boss返回正无穷，辅助开启时仍有最后收尾保护。</summary>
        float BattleDuration { get; }

        /// <summary>我方棋子（只读包装，UI 遍历用；增删只能由服务完成）。</summary>
        IReadOnlyList<Piece> PlayerPieces { get; }

        /// <summary>敌方棋子（只读包装，UI 遍历用）。</summary>
        IReadOnlyList<Piece> EnemyPieces { get; }

        /// <summary>最近一次结算的胜方；未结算为 null。</summary>
        GameSide? LastWinner { get; }

        /// <summary>
        /// 最近一次失败的原因（空字符串 = 没有失败）。UI 直接显示，方便排查。
        /// 约定：返回 false 而不写原因，等于把问题丢给玩家猜，禁止这样做。
        /// </summary>
        string LastError { get; }

        // ---------------- 玩家意图：全部先校验，再改数据 ----------------
        /// <summary>
        /// 两阶段共用点击入口：点己方棋子 → 再点空格换位；准备阶段还可以落手牌/眼位石。
        /// 三种选择互斥；失败时棋子、手牌、额度都保持原样，并写 LastError。
        /// </summary>
        bool HandleBoardClick(BoardPos pos);

        /// <summary>仅重抽当前剩余手牌，保持张数、星级和剩余落子额度。</summary>
        bool RefreshHand();

        /// <summary>选择/取消一张手牌（传 0 取消）。只改选择状态，不扣牌、不落子。</summary>
        bool SelectCard(int cardId);

        /// <summary>
        /// 查询“当前选择能否落在该格”，供棋盘高亮：
        /// 只做预检，不占格、不扣牌，因此可以在鼠标移动时反复调用。
        /// </summary>
        bool CanPlaySelectedAt(BoardPos pos);

        /// <summary>
        /// 按棋子种类+星级落一张牌（GM/教学入口）。内部仍要找到手中真实存在的牌，
        /// 不能凭种类凭空生成棋子。
        /// </summary>
        bool TryPlayCard(PieceKind kind, StarTier star, BoardPos pos);

        /// <summary>
        /// 准备阶段落一张牌（按手牌编号）。成功后扣牌、占格并广播事件；
        /// 失败原因：阶段不对 / 牌不在手中 / 越界 / 格子被占 / 额度已用完。
        /// </summary>
        bool TryPlayCard(int cardId, BoardPos pos);

        /// <summary>开始一局新战役（重置棋盘、局内强化和章节进度），失败写 LastError。</summary>
        bool BeginCampaign(int chapter);

        /// <summary>结算读完并选择奖励后，进入下一回合；返回是否整局结束（通关/失败）。</summary>
        bool ContinueRound();

        /// <summary>领取第 index 个奖励（0~2）；重复领取或不存在的编号必须返回 false。</summary>
        bool ChooseReward(int index);

        /// <summary>最后普通回合未落子时跳过，直接挑战 Boss 并换取永久攻击加成。</summary>
        bool SkipRound();

        /// <summary>切换“补眼模式”；必须准备阶段且持有眼位石。</summary>
        bool SelectEyeStone();

        /// <summary>在合法金色空格补一颗支援棋并扣一枚眼位石；不合法的点不能消耗石头。</summary>
        bool TryPlaceEyeStone(BoardPos pos);

        /// <summary>
        /// 受控触发一次战斗事件（GM/自动验收与自动事件共用同一入口）。
        /// 事件有冷却和每回合上限，不能同一秒无限叠增益。
        /// </summary>
        bool TriggerBattleEvent(BattleEventKind kind);

        /// <summary>
        /// 开一个新回合（清空敌方、重建棋盘、保留我方棋子 —— 布局与状态全部保留）。
        /// 先完整预检敌方出生空间，失败时不改棋盘；原因写入 LastError。
        /// </summary>
        bool StartRound(int chapter, int round);

        /// <summary>准备阶段结束，进入战斗（全自动）。要求至少一颗我方棋子且准备增益已发放。</summary>
        bool StartBattle();

        /// <summary>
        /// 推进战斗一小步。由域逻辑 <c>BattleLogic</c> 用固定小步长驱动，
        /// **不要**在 MonoBehaviour 的 Update 里直接调 —— 那样不同帧率会打出不同结果。
        /// </summary>
        void Tick(float deltaSeconds);

        /// <summary>结算阶段读完战报后调用：结束本回合，返回是否整局结束（通关 / 失败）。</summary>
        bool CompleteSettle();
    }
}
