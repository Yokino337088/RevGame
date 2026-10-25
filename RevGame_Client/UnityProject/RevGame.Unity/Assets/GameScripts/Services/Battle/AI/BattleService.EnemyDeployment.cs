// ============================================================
// BattleService.EnemyDeployment.cs —— 战斗中的限额战术落子。
// 本文件是 BattleService 的 partial 片段；“何时该落”由 RevHeavyFSM 的 Deploy 状态决定，这里只提交结果。
// 固定时钟只提出一次落子请求，EnemyTactics在独立快照评分，服务最后提交并通知视图。
// 它是有限的战术增援，不是无限刷怪；后段通关支援与最后一子保护仍使用原来的统一规则。
// ============================================================
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>下一次允许尝试落子的战斗秒数；使用模拟时间，暂停与不同帧率都不会额外刷怪。</summary>
        private float _nextEnemyDeployAt;
        /// <summary>本轮实际提交成功的次数；试算失败不扣预算，但照常消耗一次冷却窗口。</summary>
        private int _enemyDeploymentCount;
        /// <summary>给HUD/GM/自动验收观察实际成功次数；失败试算不算落子。</summary>
        public int EnemyDeploymentCount => _enemyDeploymentCount;
        /// <summary>只读观测接口，用于GM/测试核对低频落子；界面不能直接改时间来跳过冷却。</summary>
        public float NextEnemyDeploymentAt => _nextEnemyDeployAt;
        // 补兵节奏全部来自“战斗数值参数.xlsx”的 Deploy.*：第一手延后让玩家看清原始布阵，随后按 AI 档位缩短间隔，
        // 到停手时刻进入收尾，避免与玩家决胜支援形成无限拉锯；在场上限按数量而非历史生成数，死亡棋子不占额度。

        /// <summary>StartRound统一调用；不能另建跨回合Timer，否则上一轮延迟任务会把兵刷到新一轮。</summary>
        private void ResetEnemyDeployment()
        {
            _nextEnemyDeployAt = Balance.DeployStartSeconds;
            _enemyDeploymentCount = 0;
        }

        /// <summary>每回合敌阵成立后，向首个空眼部署远程守军；没有眼或达到人数上限则不强塞、不覆盖棋子。</summary>
        private void DeployInitialEyeGarrison()
        {
            if (_enemyPieces.Count >= Balance.DeployLivingCap
                || !EyeTactics.TryFindOwnEye(_board, GameSide.Enemy, 1, out BoardPos position)) { return; }
            // 重弩已有跨盘射界，驻眼时不会被四周友军挡住而永远不出手；血攻/星级/成长仍读现有表。
            RoundDef round = LevelCatalog.GetRound(Chapter, Round);
            PieceDef def = PieceCatalog.Get(PieceKind.Crossbow, LevelCatalog.GetEnemyStar(Chapter, false));
            var newcomer = new Piece(_nextPieceId, def, GameSide.Enemy, hpScale: round.HpScale, attackScale: round.AttackScale);
            newcomer.NextMoveAt = Balance.PlayerMoveSeconds;
            CommitEnemyDeployment(newcomer, position, "远程驻守眼位", countAgainstBudget: false);
        }

        /// <summary>只能在战斗固定步调用；失败同样进入冷却，避免满盘时每0.3125秒重做整盘试算。</summary>
        private void TickEnemyDeployment()
        {
            EnemyAI commander = EnsureEnemyCommander();
            if (commander.State == EnemyAIState.Prepare)
            {
                commander.Tick(EnemyAIState.Deploy, Balance.BattleSimulationStepSeconds, "开战，进入战术增援");
            }
            else
            {
                commander.Tick(Balance.BattleSimulationStepSeconds);
            }
        }

        /// <summary>
        /// 增援的唯一提交点：占格、编号、入名单、建 AI、刷新气眼、广播事件。
        /// 顺序不能调换：先占格再广播，界面收到事件时棋盘与气眼已经是最终状态；
        /// 任何一个失败都必须抛错，不能留下“名单里有棋、棋盘上没有”的半提交。
        /// </summary>
        private void CommitEnemyDeployment(Piece newcomer, BoardPos position, string intention, bool countAgainstBudget = true)
        {
            if (!_board.Place(newcomer, position))
            {
                throw new System.InvalidOperationException("敌方落子预检与提交不一致：" + position);
            }
            _nextPieceId++;
            _enemyPieces.Add(newcomer);
            EnsureEnemyAI(newcomer);
            // 开局驻眼属于本轮初始阵容；战斗中的额外补子才消耗原有增援预算。
            if (countAgainstBudget) { _enemyDeploymentCount++; }
            // 新棋的初始移动冷却已由Deploy状态设置；这里不再重复决定AI行为。
            RefreshGoState();
            // 模型先就绪，视图后收到通知：新棋第一次显示时已能读到正确气眼和锁死状态。
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, newcomer.Id);
            // 专用事件只在真实提交后发布：试算中的81个候选不能各弹一次提示。
            // HUD 接收值类型Id和文字，关闭面板后由owner解绑，不把新棋对象交给延迟动画保存。
            RevEvent.DispatchEvent<int, string>(Events.EnemyDeployed, newcomer.Id, intention);
            RevEvent.DispatchEvent<string>(Events.BattleLog, "敌方落子 · " + intention + "：" + newcomer.Def.DisplayName + " → " + position);
        }
    }
}
