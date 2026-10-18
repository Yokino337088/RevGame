// ============================================================
// BattleService.EnemyDeployment.cs —— 战斗中的低频敌方落子。
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
        // 第一手延后12秒，让玩家看清原始布阵；随后12～16秒一手，60秒后停手进入收尾。
        private const float EnemyDeployStart = 12f; // 第一次落子最早时刻，预留开局观察时间。
        private const float EnemyDeployStop = 60f; // 这时开始不再补兵，避免与玩家决胜支援形成无限拉锯。
        private const int EnemyLivingCap = 26; // 在场数量而非历史生成数，死亡棋子不占额度。

        /// <summary>StartRound统一调用；不能另建跨回合Timer，否则上一轮延迟任务会把兵刷到新一轮。</summary>
        private void ResetEnemyDeployment()
        {
            _nextEnemyDeployAt = EnemyDeployStart;
            _enemyDeploymentCount = 0;
        }

        /// <summary>只能在战斗固定步调用；失败同样进入冷却，避免满盘时每0.3125秒重做整盘试算。</summary>
        private void TickEnemyDeployment()
        {
            if (Phase != GamePhase.Battle || _battleElapsed < _nextEnemyDeployAt || _battleElapsed >= EnemyDeployStop) { return; }
            if (_enemyDeploymentCount >= 2 + EnemyIntelligence || IsKingCaptured() || _board.IsSideWiped(GameSide.Enemy)) { return; }
            // ★ 先推进下次时刻，再做昂贵的空间与策略检查；满盘/无合法点也不能在每个固定步重算。
            _nextEnemyDeployAt = _battleElapsed + System.Math.Max(12f, 18f - EnemyIntelligence * 2f);
            if (_board.IsSideWiped(GameSide.Player) || _board.GetPiecesOf(GameSide.Enemy).Count >= EnemyLivingCap) { return; }
            int empty = 0;
            for (int y = 0; y < _board.Size; y++)
            {
                for (int x = 0; x < _board.Size; x++) { if (_board.CanPlace(new BoardPos(x, y))) { empty++; } }
            }
            // 留八个以上空格给玩家调阵和援军；不能为了制造紧张感把手机棋盘塞满。
            if (empty <= 8) { return; }
            RoundDef round = LevelCatalog.GetRound(Chapter, Round);
            if (round == null) { return; }
            PieceKind kind = _enemyDeploymentCount % 2 == 0 ? PieceKind.Archer : PieceKind.Shield;
            if (EnemyIntelligence >= 3 && _enemyDeploymentCount % 3 == 2) { kind = PieceKind.Crossbow; }
            PieceDef def = PieceCatalog.Get(kind, LevelCatalog.GetEnemyStar(Chapter, false));
            // 后补棋按章节/回合成长，但不再叠加继承大军的平方血量倍率，避免一手援军拖长整场。
            var newcomer = new Piece(_nextPieceId, def, GameSide.Enemy, hpScale: round.HpScale, attackScale: round.AttackScale);
            BoardPos position;
            string intention;
            // 预检不发布任何事件。只有真实Place成功才增加Id/名单/次数，这三个数据必须一起提交。
            if (!EnemyTactics.TryChooseDeployment(_board, newcomer, EnemyIntelligence, out position, out intention)) { return; }
            if (!_board.Place(newcomer, position)) { throw new System.InvalidOperationException("敌方落子预检与提交不一致：" + position); }
            _nextPieceId++;
            _enemyPieces.Add(newcomer);
            _enemyDeploymentCount++;
            // 新棋不在落地瞬间攻击/移动；至少完整积攒攻击进度，移动再等五秒，给玩家反应窗口。
            newcomer.NextMoveAt = _battleElapsed + Consts.BattleTickSeconds * Consts.EnemyMoveEveryTicks;
            RefreshGoState();
            // 模型先就绪，视图后收到通知：新棋第一次显示时已能读到正确气眼和锁死状态。
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, newcomer.Id);
            RevEvent.DispatchEvent<string>(Events.BattleLog, "敌方落子 · " + intention + "：" + def.DisplayName + " → " + position);
        }
    }
}
