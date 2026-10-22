#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Animation
{
    /// <summary>
    /// 开发态业务流程执行器，由常驻 Bootstrap 的 RevServices 创建/注销，能够跨 StartScene/GameScene 继续工作。
    /// 拥有测试计时器、输入手势、观察订阅及可选强化租约；不拥有游戏域，不修改随机种子、战斗时钟或胜负。
    /// CLI 只负责发计划/取报告；这里用 RevTimer 分帧推进，GM 返回后不会阻塞 Unity 主线程。
    /// </summary>
    public sealed class GameAutomationRunner : IDisposable
    {
        /// <summary>测试轮询周期（真实秒），只决定输入的间隔和测试成本，属于工具参数，不是策划数值。</summary>
        private const float PollSeconds = 0.08f;
        private readonly GamePointerInput _input = new GamePointerInput();
        private GameAutomationPlan _plan;
        private GameAutomationReport _report = new GameAutomationReport();
        /// <summary>当前步骤索引、步骤内小阶段；落牌分成选卡、验证选中、点击格子、验证扣牌四段。</summary>
        private int _index;
        private int _stage;
        private int _deployed;
        /// <summary>以下时间均使用真实时间，暂停玩法不能让测试无限等待。</summary>
        private double _startedAt;
        private double _stepAt;
        private int _actionFrame;
        private string _pendingError;
        /// <summary>落牌前保存的不可变预期；下一帧对照模型，防止“点了但没落下”也被算作成功。</summary>
        private int _cardId;
        private PieceKind _cardKind;
        private int _handBefore;
        private int _quotaBefore;
        private BoardPos _cell;
        private GameAutomationRound _currentRound;
        private int _attackBaseline;
        private int _enemyBaseline;
        private int _damageBaseline;
        public string State { get { return _report.state; } }

        /// <summary>
        /// 先完整校验再启动；正在运行时拒绝重入，不能悄悄覆盖另一份测试。
        /// 不清存档、不重开战役，计划必须从当前真实业务状态开始；CLI 默认用新的 StartScene Play 隔离一次运行。
        /// </summary>
        public void Start(GameAutomationPlan plan)
        {
            if (State == "running")
            {
                throw new RevGMUsageException("已有自动化流程在运行，请先取消");
            }
            Validate(plan);
            _plan = plan;
            _index = _stage = _deployed = 0;
            _pendingError = null;
            _currentRound = null;
            _startedAt = _stepAt = Time.realtimeSinceStartupAsDouble;
            _report = new GameAutomationReport
            {
                name = plan.name, state = "running", totalSteps = plan.steps.Length,
                startedUtc = DateTime.UtcNow.ToString("O"), boost = plan.boost
            };
            // 租约只跟本次测试走，不随回合重置，也不会污染Excel/棋子定义；所有终止路径都在StopObserving归还。
            ((BattleService)RevServices.GetRequired<IBattleService>()).SetAutomationBoost(this, plan.boost);
            // 显式 owner=this：大厅域退出时不能误取消整章测试；完成/失败/取消/退出 Play 都由 StopObserving 对称回收。
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, OnAttack, owner: this);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, OnHit, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterBattle, OnBattleStarted, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.BattleEnded, OnBattleEnded, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, OnBossCast, owner: this);
            Application.logMessageReceived += OnLog;
            RevTimer.Every(PollSeconds, Tick, domain: RevTimeDomain.Unscaled, owner: this);
        }

        /// <summary>计划格式和权限边界在执行前检查，避免跑到半章才发现操作拼错或包含作弊命令。</summary>
        public static void Validate(GameAutomationPlan plan)
        {
            if (plan == null || plan.steps == null || plan.steps.Length == 0 || plan.steps.Length > 256 || !ValidTimeout(plan.timeoutSeconds))
            {
                throw new RevGMUsageException("计划需有 1～256 步，总超时需为 1～3600 秒");
            }
            if (plan.boost != null && (plan.boost.attackBonusPercent < 0 || plan.boost.attackBonusPercent > 500
                || plan.boost.attackSpeedBonusPercent < 0 || plan.boost.attackSpeedBonusPercent > 500
                || plan.boost.defenseBonusPercent < 0 || plan.boost.defenseBonusPercent > 500))
            {
                throw new RevGMUsageException("自动化增益范围为0～500%，正式游玩请不传boost");
            }
            foreach (GameAutomationStep step in plan.steps)
            {
                if (step == null || !ValidTimeout(step.timeoutSeconds))
                {
                    throw new RevGMUsageException("步骤不能为空，步骤超时需为 1～3600 秒");
                }
                if (step.op != "wait" && step.op != "assert" && step.op != "click" && step.op != "deployHand" && step.op != "gm")
                {
                    throw new RevGMUsageException("未知操作：" + step.op);
                }
                if ((!string.IsNullOrEmpty(step.screen) && !Enum.TryParse(step.screen, out GameScreen _))
                    || (!string.IsNullOrEmpty(step.phase) && !Enum.TryParse(step.phase, out GamePhase _)))
                {
                    throw new RevGMUsageException("screen/phase 必须使用项目枚举名称");
                }
                if (step.condition != null && step.condition != "" && step.condition != "ready" && step.condition != "roundWon"
                    && step.condition != "chapterWon" && step.condition != "homeClean")
                {
                    throw new RevGMUsageException("未知只读条件：" + step.condition);
                }
                if (step.count < 1 || step.count > Consts.MaxCardsPerRound || step.chapter < 0 || step.chapter > Consts.ChapterCount
                    || step.round < 0 || step.hand < -1 || step.hand > Consts.MaxCardsPerRound)
                {
                    throw new RevGMUsageException("章号、回合、手牌数或打牌张数超出允许范围");
                }
                if (step.op == "gm" && !IsAllowedCommand(step.command))
                {
                    throw new RevGMUsageException("测试只允许玩家业务 GM；禁止跳关、强制胜利、改属性和任意脚本");
                }
                if (step.op == "click" && (string.IsNullOrEmpty(step.target) || step.target.Split('/').Length != 2))
                {
                    throw new RevGMUsageException("click 需要 Panel类型名/按钮节点名");
                }
            }
        }

        private static bool ValidTimeout(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 1f && value <= 3600f;
        }

        /// <summary>精确匹配命令名，参数仍由原 GM 校验；绝不把整个 RevGame 分组当成可执行白名单。</summary>
        private static bool IsAllowedCommand(string command)
        {
            string name = (command ?? "").Split(' ')[0];
            return name == "RevGame/打开选关" || name == "RevGame/进入战斗" || name == "RevGame/返回主界面"
                || name == "RevGame/开始战斗" || name == "RevGame/下一回合" || name == "RevGame/刷新手牌" || name == "RevGame/选奖励";
        }

        /// <summary>每次最多推进一个小动作；异常和 Unity 错误统一变成失败证据，计时器不能静默死掉。</summary>
        private void Tick()
        {
            if (State != "running")
            {
                return;
            }
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                _report.elapsedSeconds = (float)(now - _startedAt);
                if (_pendingError != null)
                {
                    Finish("failed", _pendingError);
                    return;
                }
                GameAutomationStep step = _plan.steps[_index];
                if (now - _startedAt > _plan.timeoutSeconds || now - _stepAt > step.timeoutSeconds)
                {
                    string detail = "步骤超时：" + step.name + "；" + _input.LastError + "；" + RevServices.GetRequired<GameFlow>().LastError;
                    Finish("failed", detail);
                    return;
                }
                // 真实帧屏障：极低帧率下 Timer 可能补发回调，不能在同一帧把按下/松开/落子全做完。
                if (_actionFrame == Time.frameCount)
                {
                    return;
                }
                if (Execute(step))
                {
                    Record(step.name, step.op + " 完成");
                    _report.completedSteps = ++_index;
                    _stage = _deployed = 0;
                    _stepAt = now;
                    _actionFrame = Time.frameCount;
                    if (_index == _plan.steps.Length)
                    {
                        Finish("passed", "");
                    }
                }
            }
            catch (Exception error)
            {
                Finish("failed", error.GetType().Name + "：" + error.Message);
            }
        }

        private bool Execute(GameAutomationStep step)
        {
            if (step.op == "wait")
            {
                return Matches(step);
            }
            if (step.op == "assert")
            {
                Require(Matches(step), "断言不满足：" + step.name);
                return true;
            }
            if (step.op == "gm")
            {
                Require(Matches(step), "GM前置条件不满足");
                RevGMResult result = RevGM.Execute(step.command);
                Require(result.Success && (string.IsNullOrEmpty(step.expected) || result.Message == step.expected), "GM失败：" + result.Message);
                _report.gmActions++;
                Record(step.name, "GM（不是模拟输入）：" + step.command + " → " + result.Message);
                return true;
            }
            if (step.op == "deployHand")
            {
                return DeployHand(step);
            }
            if (_stage == 0)
            {
                if (!Matches(step) || !_input.PressButton(_input.FindButton(step.target)))
                {
                    return false;
                }
                _stage = 1;
                _actionFrame = Time.frameCount;
                return false;
            }
            ReleaseClick();
            Record(step.name, "EventSystem指针点击：" + step.target);
            return true;
        }

        /// <summary>
        /// 模拟选牌和落点的两次点击，并核对手牌 -1、额度 -1、目标格确实出现同种我方棋。
        /// 只读 CanFit 来选择位置，真正提交仍由 BoardCellView → BoardView → BattleLogic → BattleService 完成。
        /// 每个小阶段隔帧，允许原来的 UI 刷新、DOTween 和资源回调正常工作。
        /// </summary>
        private bool DeployHand(GameAutomationStep step)
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            Require(battle.Phase == GamePhase.Prepare, "只有准备阶段可以测试打牌");
            if (_stage == 0)
            {
                Require(Matches(step), "打牌前置条件不满足");
                Require(battle.Hand.Count > 0 && battle.CardsRemaining > 0, "手牌或额度不足，不能凭空补牌");
                Button button = _input.FindButton("PreparePanel/card0");
                CardItem view = button != null ? button.GetComponent<CardItem>() : null;
                if (view == null || view.IsFlipping || view.CardId != battle.Hand[0].Id)
                {
                    return false;
                }
                _cardId = battle.Hand[0].Id;
                _cardKind = battle.Hand[0].Kind;
                _handBefore = battle.Hand.Count;
                _quotaBefore = battle.CardsRemaining;
                if (!_input.PressButton(button))
                {
                    return false;
                }
                _stage = 1;
            }
            else if (_stage == 1)
            {
                ReleaseClick();
                Require(battle.SelectedCardId == _cardId, "点击手牌后未选中对应编号，请检查按钮接线");
                _stage = 2;
            }
            else if (_stage == 2)
            {
                BoardCellView cell = FindPlacement(battle);
                Require(cell != null, "没有合法且能被点击的落点；不清旧棋、不绕过UI遮挡");
                _cell = cell.Position;
                if (!_input.PressCell(cell))
                {
                    return false;
                }
                _stage = 3;
            }
            else
            {
                ReleaseClick();
                var piece = battle.Board.GetPiece(_cell);
                Require(battle.Hand.Count == _handBefore - 1 && battle.CardsRemaining == _quotaBefore - 1
                    && piece != null && piece.Side == GameSide.Player && piece.Def.Kind == _cardKind,
                    "落子后手牌/额度/占格不一致：" + battle.LastError);
                _report.cardsPlayed++;
                Record(step.name, "已验证手牌#" + _cardId + " → " + _cell + "；手牌/额度各扣1，棋种=" + _cardKind);
                _stage = 0;
                _deployed++;
                return _deployed >= step.count;
            }
            _actionFrame = Time.frameCount;
            return false;
        }

        /// <summary>稳定地从己方后排向前找可放空格；只在每次出牌时扫描81格，不进入战斗AI的每帧查询。</summary>
        private BoardCellView FindPlacement(IBattleService battle)
        {
            BoardCellView[] cells = UnityEngine.Object.FindObjectsOfType<BoardCellView>();
            int size = PieceCatalog.PlayerBodySize(battle.Hand[0].Star);
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    BoardPos pos = new BoardPos(x, y);
                    if (!battle.Board.CanFit(pos, size, GameSide.Player, respectEnemyEyes: false))
                    {
                        continue;
                    }
                    foreach (BoardCellView cell in cells)
                    {
                        if (cell.Position.X == x && cell.Position.Y == y && _input.CanReachCell(cell))
                        {
                            return cell;
                        }
                    }
                }
            }
            return null;
        }

        private void ReleaseClick()
        {
            Require(_input.Release(true), "指针松开时目标已失效/被遮挡：" + _input.LastError);
            _report.pointerClicks++;
            _actionFrame = Time.frameCount;
        }

        /// <summary>无副作用状态查询。过场尚未交割时一律不满足，不能把 EnterBattle=true 当“场景已加载”。</summary>
        private bool Matches(GameAutomationStep step)
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            if (flow.IsTransitioning || !string.IsNullOrEmpty(flow.LastError)
                || (!string.IsNullOrEmpty(step.screen) && flow.Screen.ToString() != step.screen)
                || (!string.IsNullOrEmpty(step.phase) && battle.Phase.ToString() != step.phase)
                || (step.chapter > 0 && battle.Chapter != step.chapter) || (step.round > 0 && battle.Round != step.round)
                || (step.hand >= 0 && battle.Hand.Count != step.hand))
            {
                return false;
            }
            switch (step.condition)
            {
                case "ready": return battle.PreparationGranted && RevUI.IsOpen<PreparePanel>();
                case "roundWon": return battle.LastWinner == GameSide.Player && battle.Phase == GamePhase.Settle && RoundWasObserved(battle.Chapter, battle.Round);
                case "chapterWon": return ChapterWasObserved(step.chapter > 0 ? step.chapter : battle.Chapter) && battle.UnlockedChapter > battle.Chapter;
                case "homeClean": return flow.Screen == GameScreen.Home && RevScene.CurrentName == "StartScene"
                    && RevLogic.Get<BattleLogic>() == null && !RevScope.IsActive(GameDomain.Battle)
                    && UnityEngine.Object.FindObjectOfType<BoardView>() == null && !RevUI.IsOpen<PreparePanel>() && !RevUI.IsOpen<BattleHudPanel>();
                default: return true;
            }
        }

        private bool RoundWasObserved(int chapter, int round)
        {
            foreach (GameAutomationRound result in _report.rounds)
            {
                if (result.chapter == chapter && result.round == round && result.winner == GameSide.Player.ToString()
                    && result.enemiesAtStart > 0
                    && ((result.playerAttacks > 0 && result.damageEvents > 0) || (result.kingId > 0 && result.kingCaptured)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>逐个核对全部普通回合与Boss，不能仅凭解锁值证明完整通章；跳过第10轮也无法满足此条件。</summary>
        private bool ChapterWasObserved(int chapter)
        {
            for (int round = 1; round <= Balance.BossRoundIndex; round++)
            {
                if (!RoundWasObserved(chapter, round))
                {
                    return false;
                }
            }
            // 高攻击夹具可能在敌方第一次出手前取胜，因此它验证流程，不用于证明双方交战强度。
            // 普通数值的计划仍要求敌方实际出过手；两种口径在报告中通过boost配置明确区分。
            return (_plan.boost != null || _report.enemyAttacks > 0) && _report.unityErrors == 0;
        }

        private void OnAttack(PieceAttackInfo attack)
        {
            if (attack.Side == GameSide.Player) { _report.playerAttacks++; }
            else { _report.enemyAttacks++; }
        }

        private void OnHit(BattleHitInfo hit)
        {
            if (hit.Damage > 0) { _report.damageEvents++; }
        }

        private void OnBossCast(BossSkillInfo info)
        {
            _report.bossCasts++;
        }

        /// <summary>开战瞬间保存敌人和主将身份，不在结算时反推“之前应该有Boss”。</summary>
        private void OnBattleStarted(int round)
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            _currentRound = new GameAutomationRound { chapter = battle.Chapter, round = round, enemiesAtStart = battle.EnemyPieces.Count };
            if (battle.IsBossRound)
            {
                foreach (var enemy in battle.EnemyPieces)
                {
                    if (enemy.IsKing)
                    {
                        _currentRound.kingId = enemy.Id;
                        break;
                    }
                }
            }
            _attackBaseline = _report.playerAttacks;
            _enemyBaseline = _report.enemyAttacks;
            _damageBaseline = _report.damageEvents;
        }

        /// <summary>事件先保留真实胜负，再由下一次Tick终止失败，避免在业务事件派发栈中重入流程。</summary>
        private void OnBattleEnded(GameSide winner)
        {
            if (_currentRound == null)
            {
                _pendingError = "收到结算但未观察到开战，不允许把中途接入当完整回合";
                return;
            }
            _currentRound.winner = winner.ToString();
            _currentRound.battleSeconds = RevServices.GetRequired<IBattleService>().BattleElapsed;
            _currentRound.playerAttacks = _report.playerAttacks - _attackBaseline;
            _currentRound.enemyAttacks = _report.enemyAttacks - _enemyBaseline;
            _currentRound.damageEvents = _report.damageEvents - _damageBaseline;
            // “擒王”是原有玩法：主将被锁死就立即赢，不一定发生攻击/扣血。
            // 只读核对开战时同一编号的主将，绝不把“没生成Boss”或“删掉全部敌人”误认为围棋获胜。
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            if (battle.IsBossRound && _currentRound.kingId > 0)
            {
                foreach (var enemy in battle.EnemyPieces)
                {
                    if (enemy.Id == _currentRound.kingId && enemy.IsKing)
                    {
                        _currentRound.kingCaptured = !enemy.IsAlive || enemy.IsLocked;
                        break;
                    }
                }
            }
            _report.rounds.Add(_currentRound);
            _currentRound = null;
            if (winner != GameSide.Player)
            {
                _pendingError = "玩家真实战败；测试不会重写胜负或跳过失败回合";
            }
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                _report.unityErrors++;
                _pendingError = "Unity错误：" + condition;
            }
        }

        private void Record(string step, string detail)
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            _report.evidence.Add(new GameAutomationEvidence
            {
                step = step, detail = detail, screen = flow.Screen.ToString(), phase = battle.Phase.ToString(),
                chapter = battle.Chapter, round = battle.Round, atSeconds = (float)(Time.realtimeSinceStartupAsDouble - _startedAt)
            });
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }

        private void Finish(string state, string error)
        {
            _report.state = state;
            _report.error = error;
            _report.elapsedSeconds = (float)(Time.realtimeSinceStartupAsDouble - _startedAt);
            Record("流程结束", state + " " + error);
            StopObserving();
        }

        /// <summary>所有终止路径共用：先松手（不补点击），再取消计时器/订阅；不关闭用户的Unity，不清整个框架。</summary>
        private void StopObserving()
        {
            _input.Release(false);
            RevTimer.CancelAllOf(this);
            RevEvent.RemoveAllByOwner(this);
            Application.logMessageReceived -= OnLog;
            var battle = RevServices.Get<IBattleService>() as BattleService;
            battle?.ClearAutomationBoost(this);
            _report.boostReleased = battle != null && !battle.IsAutomationBoostActive;
        }

        public void Cancel()
        {
            if (State == "running") { Finish("cancelled", "外部取消"); }
        }

        /// <summary>返回快照，不由游戏代码自己拼本机目录写文件；CLI负责把报告导出到指定的测试目录。</summary>
        public string ReportJson()
        {
            if (State == "running") { _report.elapsedSeconds = (float)(Time.realtimeSinceStartupAsDouble - _startedAt); }
            return JsonUtility.ToJson(_report, true);
        }

        public void Dispose()
        {
            Cancel();
            StopObserving();
        }
    }
}
#endif
