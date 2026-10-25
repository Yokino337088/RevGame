// ============================================================
// BattleService.Diagnostics.cs —— 仅开发环境编译的固定验收场景。
// 不开放任意改血/改表接口；只允许几个可重复场景，GM 和自动断言使用同一份前置条件。
// 正式发行包不会包含本文件中的调试入口。
// 它属于 Services 目录是因为验收必须调用真实业务方法，而不是在测试里复制一套规则。
// ============================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>
        /// 仅开发验收：打开后我方棋子受到的任何伤害都记为 0。
        /// 用于单独观察 Boss 技能表现，避免棋子提前阵亡导致后续技能没有合法目标；
        /// 正式发行包不包含本文件，这个开关也不会进入发布版本。
        /// </summary>
        public bool PlayerInvincible { get; private set; }

        /// <summary>只允许开发 GM 调用；返回设置后的状态，便于自动化精确断言。</summary>
        public bool SetPlayerInvincible(bool value)
        {
            PlayerInvincible = value;
            return PlayerInvincible;
        }

        /// <summary>
        /// 仅开发验收：把 Boss 血量压到指定比例，专门满足“汲取只在残血时可用”的前置条件。
        /// 走统一扣血出口，血量、事件与表现保持一致；不是任意改血接口，比例也限制在 0.1~0.85。
        /// </summary>
        public bool PressBossHp(float ratio)
        {
            ratio = System.Math.Max(0.1f, System.Math.Min(0.85f, ratio));
            Piece boss = _enemyPieces.Find(p => p.IsAlive && p.IsKing);
            if (boss == null)
            {
                return false;
            }
            int targetHp = System.Math.Max(1, (int)(boss.MaxHp * ratio));
            int delta = boss.Hp - targetHp;
            if (delta > 0)
            {
                DamagePiece(boss, delta);
            }
            return true;
        }

        /// <summary>
        /// 加载预定义的开发场景（merge / enemy-one-eye / enemy-two-eyes 等）。
        /// 只用于编辑器验收与 GM 观察，正式发行包不包含本文件。
        /// </summary>
        public bool LoadDevelopmentScenario(string name)
        {
            if (name != "merge" && name != "large-merge" && name != "boss" && name != "events" && name != "reward" && name != "round10" && name != "enemy-one-eye" && name != "enemy-two-eyes")
            {
                return Reject("只允许 merge / large-merge / boss / events / reward / round10 / enemy-one-eye / enemy-two-eyes 场景");
            }

            _playerPieces.Clear();
            _enemyPieces.Clear();
            _board.Reset();
            if (!StartRound(1, name == "enemy-two-eyes" ? 9 : (name == "round10" ? Balance.NormalRoundsPerChapter : (name == "boss" || name == "reward" ? Balance.BossRoundIndex : 1))))
            {
                return false;
            }
            // 新战术夹具保持正常六张手牌，让人工可以自由布阵观察，不混入原合成夹具的三星大棋。
            if (name == "enemy-one-eye" || name == "enemy-two-eyes")
            {
                return true;
            }
            _eventScheduled = VictoryAssistEnabled;
            _hand.Clear();
            if (name == "merge" || name == "large-merge")
            {
                StarTier star = name == "merge" ? StarTier.One : StarTier.Two;
                _playedPairs.Clear();
                _playedPairs[PieceKind.Shield] = 1;
                for (int i = 0; i < 1; i++)
                {
                    var p = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.Shield, star), GameSide.Player);
                    _board.Place(p, new BoardPos(i, 0));
                    _playerPieces.Add(p);
                }

                _hand.Add(PieceKind.Shield, star);
            }
            else
            {
                _hand.Add(PieceKind.Shield, StarTier.One);
                _hand.Add(PieceKind.Archer, StarTier.One);
                var p = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.HeavySword, StarTier.Three), GameSide.Player);
                _board.Place(p, new BoardPos(5, 4));
                _playerPieces.Add(p);
                if (name == "boss")
                {
                    // 再放一颗 1×1 小棋：Boss 技能验收需要“贴身”目标，而 3★ 大棋占 2×2，
                    // 贴脸格往往放不下；小棋让横扫等近身技能始终有合法目标。
                    var small = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
                    if (_board.Place(small, new BoardPos(3, 3)))
                    {
                        _playerPieces.Add(small);
                    }
                }
                if (name == "reward")
                {
                    Piece king = _enemyPieces.Find(piece => piece.IsKing);
                    DamagePiece(king, king.Hp - 1);
                    _board.Move(p, new BoardPos(5, 5));
                }
            }

            // 固定验收输入只约束前几张牌，剩余槽位仍补满六张。
            // 调试场景可能被人工继续游玩，不能把“只留一张合成牌”的夹具状态误留给玩家。
            while (_hand.Cards.Count < Consts.MaxHandSize) { _hand.Add(PickUnlockedKind(), StarTier.One); }
            RefreshGoState();
            RevEvent.DispatchEvent(Events.HandChanged);
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            return true;
        }

        /// <summary>
        /// 自动化专用：移除 Boss 以外的所有敌方棋子，给贴身技能腾出落点。
        /// 敌方布阵会把护卫摆在 Boss 四周，导致“贴脸”格全被占；这不是规则问题，是夹具限制。
        /// 只走真实 Remove、死亡事件和气眼刷新，不改血量或属性；返回实际移除数量。
        /// </summary>
        public int ClearBossGuards()
        {
            var guards = new System.Collections.Generic.List<Piece>();
            foreach (Piece piece in _enemyPieces)
            {
                if (!piece.IsKing)
                {
                    guards.Add(piece);
                }
            }
            foreach (Piece guard in guards)
            {
                _board.Remove(guard);
                _enemyPieces.Remove(guard);
                if (!_enemyAI.Remove(guard.Id, out _))
                {
                    _bossAI.Remove(guard.Id);
                }
                RevEvent.DispatchEvent<int>(Events.PieceDied, guard.Id);
            }
            RefreshGoState();
            return guards.Count;
        }
    }
}
#endif

