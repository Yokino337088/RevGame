using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>手动移除与Boss吞噬共用占格出口；移除不是击杀，不返牌、不给分、不触发眼位复活。</summary>
    public sealed partial class BattleService
    {
        /// <summary>最近成功进入Boss回合实际吞噬的数量，用于GM与自动化核对。</summary>
        public int LastBossSwallowedCount { get; private set; }
        private int _rangedCountTopology = -1, _rangedCount;

        /// <summary>所有新增己方远程入口先检查。预检只给布尔值；用户提交失败才发一次可见提示。</summary>
        private bool CanAddPlayerRanged(PieceDef definition, bool notify)
        {
            if (definition == null) { return false; }
            if (!definition.IsRanged) { return true; }
            if (_rangedCountTopology != _board.TopologyVersion)
            {
                _rangedCount = 0;
                foreach (Piece piece in _playerPieces) { if (piece.IsAlive && piece.Def.IsRanged) { _rangedCount++; } }
                _rangedCountTopology = _board.TopologyVersion;
            }
            if (_rangedCount < Balance.RangedLimit) { return true; }
            if (notify)
            {
                LastError = "远程棋子最多保留" + Balance.RangedLimit + "个，请先移除部分远程棋子";
                RevEvent.DispatchEvent<string>(Events.BoardRuleNotice, LastError);
            }
            return false;
        }

        /// <summary>UI/GM共用入口。允许准备和战斗时主动腾位，战斗最后一颗被移除立即按全灭失败。</summary>
        public bool RemovePlayerPiece(int id)
        {
            if (Phase != GamePhase.Prepare && Phase != GamePhase.Battle) { return Reject("结算时不能移除棋子"); }
            Piece piece = _playerPieces.Find(p => p.Id == id && p.IsAlive);
            if (piece == null || _board.FindPiece(id) != piece) { return Reject("只能移除当前棋盘上的存活己方棋子"); }
            RemovePlayerPieceCore(piece);
            RefreshGoState();
            LastError = string.Empty;
            if (Phase == GamePhase.Battle && HasBattleEnded()) { EndBattle(); }
            return true;
        }

        /// <summary>只撤占格和名单，不走伤害/击杀出口。必须先广播移除再刷新视图，让原棋子有离场表现。</summary>
        private void RemovePlayerPieceCore(Piece piece)
        {
            _board.Remove(piece);
            _playerPieces.Remove(piece);
            if (_playerAI.TryGetValue(piece.Id, out PlayerAI ai))
            {
                ai.Dispose();
                _playerAI.Remove(piece.Id);
            }
            if (SelectedPieceId == piece.Id)
            {
                SelectedPieceId = 0;
                RevEvent.DispatchEvent(Events.SelectionChanged);
            }
            RevEvent.DispatchEvent<int>(Events.PieceDied, piece.Id);
        }

        /// <summary>
        /// 创建待提交的独立名单，原Piece只读。星级升序，同星近战优先，最后按Id保证同输入同结果。
        /// 最终仅在敌阵完整预检成功后提交吞噬，所以失败不丢棋、不改血、不改当前回合。
        /// </summary>
        private List<Piece> PlanBossSwallow(RoundDef definition, bool newBoard, out List<Piece> swallowed)
        {
            swallowed = new List<Piece>();
            var retained = _playerPieces.FindAll(p => p.IsAlive);
            if (newBoard || !definition.IsBoss || retained.Count <= Balance.BossPlayerPieceLimit) { return retained; }
            var ranked = new List<Piece>(retained);
            ranked.Sort((a, b) =>
            {
                int star = ((int)a.Def.Star).CompareTo((int)b.Def.Star);
                if (star != 0) { return star; }
                int role = a.Def.IsRanged.CompareTo(b.Def.IsRanged);
                return role != 0 ? role : a.Id.CompareTo(b.Id);
            });
            int excess = retained.Count - Balance.BossPlayerPieceLimit;
            for (int i = 0; i < excess; i++)
            {
                swallowed.Add(ranked[i]);
                retained.Remove(ranked[i]);
            }
            return retained;
        }

        /// <summary>
        /// 极端散布时，用同一吞噬数量换取连续出生区。只从原排序截断处的同星/同职业候选中替换，
        /// 高级棋、优先保留的远程不会为了腾位被额外吞掉。每个矩形只试一次独立敌阵，搜索有限。
        /// </summary>
        private bool TryPlanBossSpace(RoundDef definition, bool newBoard, int budget, out List<Piece> retained, out List<Piece> swallowed, out List<Piece> enemies)
        {
            retained = null; swallowed = null; enemies = null;
            if (newBoard || !definition.IsBoss || budget <= 0) { return false; }
            List<Piece> original;
            PlanBossSwallow(definition, false, out original);
            Piece threshold = original[original.Count - 1];
            var mandatory = original.FindAll(p => p.Def.Star < threshold.Def.Star
                || (p.Def.Star == threshold.Def.Star && !p.Def.IsRanged && threshold.Def.IsRanged));
            var eligible = _playerPieces.FindAll(p => p.IsAlive && p.Def.Star == threshold.Def.Star
                && p.Def.IsRanged == threshold.Def.IsRanged);
            eligible.Sort((a, b) => a.Id.CompareTo(b.Id));
            int size = LevelCatalog.BossOf(definition.Chapter).BodySizes[0];
            for (int y = 0; y <= Consts.BoardSize - size; y++)
            {
                for (int x = 0; x <= Consts.BoardSize - size; x++)
                {
                    var chosen = new List<Piece>(mandatory);
                    bool blocked = false;
                    foreach (Piece player in _playerPieces)
                    {
                        if (!player.IsAlive || player.Pos.X >= x + size || player.Pos.Y >= y + size
                            || player.Pos.X + player.Size <= x || player.Pos.Y + player.Size <= y) { continue; }
                        if (!chosen.Contains(player))
                        {
                            if (!eligible.Contains(player)) { blocked = true; break; }
                            chosen.Add(player);
                        }
                    }
                    if (blocked || chosen.Count > budget) { continue; }
                    foreach (Piece player in eligible)
                    {
                        if (chosen.Count == budget) { break; }
                        if (!chosen.Contains(player)) { chosen.Add(player); }
                    }
                    var trialRoster = _playerPieces.FindAll(p => p.IsAlive && !chosen.Contains(p));
                    if (TryPlanEnemies(definition, false, out List<Piece> candidateEnemies, trialRoster))
                    {
                        retained = trialRoster; swallowed = chosen; enemies = candidateEnemies;
                        LastError = string.Empty;
                        return true;
                    }
                }
            }
            return Reject("同等低级棋子的吞噬预算内仍无法放下Boss，请先主动移除棋子腾出连续区域");
        }
    }
}
