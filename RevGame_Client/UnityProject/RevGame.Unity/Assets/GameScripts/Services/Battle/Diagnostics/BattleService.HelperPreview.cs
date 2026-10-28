#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>视觉夹具与正常战役分离：明确生成高生命测试输入、给能源，真实FSM和伤害规则仍执行。</summary>
    public sealed partial class BattleService
    {
        public bool LoadHelperPreview(int chapter, bool crowded)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || Phase != GamePhase.Prepare)
            { throw new InvalidOperationException("帮手视觉造局只允许隔离Play准备阶段"); }
            if (!StartRound(chapter, 1)) { return false; }
            StopEnemyAI(); _board.Reset(); _playerPieces.Clear(); _enemyPieces.Clear();
            int rows = crowded ? 4 : 1;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    // 视觉高生命输入只用于保持激光/粒子可见，不能将这个夹具的胜率计为数值测试。
                    var piece = new Piece(_nextPieceId++, PieceCatalog.Get(x % 3 == 0 ? PieceKind.Archer : PieceKind.Swordman, StarTier.One), GameSide.Player, 1, hpScale: 1000);
                    _board.Place(piece, new BoardPos(x, y)); _playerPieces.Add(piece);
                }
            }
            for (int x = 0; x < 6; x++)
            {
                var piece = new Piece(_nextPieceId++, PieceCatalog.Get(x % 2 == 0 ? PieceKind.Archer : PieceKind.Swordman, StarTier.One), GameSide.Enemy, 1, hpScale: 1000);
                _board.Place(piece, new BoardPos(x, 8)); _enemyPieces.Add(piece);
            }
            RefreshGoState(); ResetHelperRound();
            _preparationGranted = true;
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            return true;
        }

        /// <summary>只供视觉验收注入指定档位能源；前置隔离，保留历史分、手牌、血量和占格。</summary>
        public void SetHelperPreviewTier(int tier)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || tier < 0 || tier > Balance.HelperMaxTier)
            { throw new InvalidOperationException("帮手预览档位超界或不是隔离测试"); }
            ChapterScore = (long)tier * Balance.HelperScoreStep;
            CombatScore = Math.Max(10000L, Balance.HelperBeamReserve * 10L);
            _helperBeamUntil = 0;
            _helperNextBeam = _helperNextShot = _helperNextMove = 0;
        }

        /// <summary>数量边界视觉输入。真实上限/吞噬仍走PlaceCard/StartRound，不直接派期望提示事件。</summary>
        public bool LoadHelperLimitsPreview(bool boss)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || Phase != GamePhase.Prepare)
            { throw new InvalidOperationException("数量视觉夹具仅允许隔离准备期"); }
            if (!StartRound(6, boss ? 9 : 1)) { return false; }
            StopEnemyAI(); _board.Reset(); _playerPieces.Clear(); _enemyPieces.Clear();
            int count = boss ? 54 : Balance.RangedLimit;
            for (int i = 0; i < count; i++)
            {
                var piece = new Piece(_nextPieceId++, PieceCatalog.Get(boss ? PieceKind.Swordman : PieceKind.Archer, StarTier.One), GameSide.Player, 1);
                _board.Place(piece, new BoardPos(i % 9, i / 9), respectEnemyEyes: false); _playerPieces.Add(piece);
            }
            if (boss) { return StartRound(6, 10); }
            var enemy = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy, 1);
            _board.Place(enemy, new BoardPos(8, 8)); _enemyPieces.Add(enemy);
            _hand.Clear();
            for (int i = 0; i < Consts.MaxHandSize; i++) { _hand.Add(PieceKind.Archer, StarTier.One); }
            RefreshGoState(); ResetHelperRound();
            _preparationGranted = true;
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            RevEvent.DispatchEvent(Events.HandChanged);
            return true;
        }
    }
}
#endif
