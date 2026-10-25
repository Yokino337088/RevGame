// 眼位的动态权益属于战斗服务：共享固定战斗时钟，不为每颗棋新建计时器；结束战斗后不会迟到补兵。
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>上次规则刷新中的真实驻眼编号，用来合并一次落子产生的中央提示；服务独占且复用。</summary>
        private readonly HashSet<int> _previousEyeGarrisons = new HashSet<int>();
        /// <summary>复活治疗去重草稿；同一大棋占据两个邻格也只治疗一次。</summary>
        private readonly HashSet<int> _eyeHealedIds = new HashSet<int>();
        /// <summary>本轮下次眼位支援的战斗秒数，ResetCombatPacing在换轮重设。</summary>
        private float _nextEyeSupportAt;
        /// <summary>本轮实际成功补入眼位的远程棋数；没有空眼不会虚增。</summary>
        public int EyeSupportCount { get; private set; }
        /// <summary>本轮真正发生的驻眼复活次数；单棋资格由Piece持有，换轮不刷新。</summary>
        public int EyeReviveCount { get; private set; }

        /// <summary>每个配置间隔只补一颗弩兵；弩兵能越过己方守子射击。没有合法空眼时跳过本窗口，不向普通格溢出。</summary>
        private void TickEyeSupport()
        {
            if (Phase != GamePhase.Battle || _battleElapsed < _nextEyeSupportAt) { return; }
            _nextEyeSupportAt += Balance.EyeSupportSeconds;
            if (_board.IsSideWiped(GameSide.Player) || _board.IsSideWiped(GameSide.Enemy) || IsKingCaptured()) { return; }
            if (!EyeTactics.TryFindOwnEye(_board, GameSide.Player, 1, out BoardPos pos)) { return; }
            SpawnSupport(pos, PieceKind.Crossbow);
            EyeSupportCount++;
            RefreshGoState();
        }

        /// <summary>每秒治疗有效眼的四邻友军；共享战斗时钟，大步长仍结算完整秒数，比例按最大生命取整。</summary>
        private void TickEyeHealing(float stepSeconds)
        {
            _eyeHealElapsed += stepSeconds;
            while (_eyeHealElapsed >= Balance.EyeHealSeconds)
            {
                _eyeHealElapsed -= Balance.EyeHealSeconds;
                foreach (Piece p in _board.Pieces)
                {
                    if (p.Side == GameSide.Player && p.HasEyeGuardAura && p.Heal(System.Math.Max(1, (int)(p.MaxHp * Balance.EyeHealRatio))) > 0)
                    {
                        RevEvent.DispatchEvent<int>(Events.PieceHpChanged, p.Id);
                    }
                }
            }
        }

        /// <summary>原位复活成功后治疗上下左右存活友军；不治疗敌人、不复活其他死棋，不重置攻击与护盾。</summary>
        private void HealRevivedEyeNeighbors(Piece revived)
        {
            EyeReviveCount++;
            _eyeHealedIds.Clear();
            foreach (BoardPos pos in revived.Pos.GetOrthogonalNeighbors())
            {
                Piece neighbor = _board.GetPiece(pos);
                if (neighbor == null || neighbor == revived || neighbor.Side != GameSide.Player || !neighbor.IsAlive || !_eyeHealedIds.Add(neighbor.Id)) { continue; }
                if (neighbor.Heal(Math.Max(1, (int)(neighbor.MaxHp * Balance.EyeReviveHealRatio))) > 0)
                {
                    RevEvent.DispatchEvent<int>(Events.PieceHpChanged, neighbor.Id);
                }
            }
            RevEvent.DispatchEvent<string>(Events.BattleEventTriggered, "眼位复苏");
        }
    }
}
