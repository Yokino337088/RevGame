// ============================================================
// BattleService.Diagnostics.cs —— 仅开发环境编译的固定验收场景。
// 不开放任意改血/改表接口；只允许几个可重复场景，GM 和自动断言使用同一份前置条件。
// 正式发行包不会包含本文件中的调试入口。
// ============================================================
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        public bool LoadDevelopmentScenario(string name)
        {
            if (name != "merge" && name != "large-merge" && name != "boss" && name != "events" && name != "reward" && name != "round10" && name != "enemy-one-eye" && name != "enemy-two-eyes")
            {
                return Reject("只允许 merge / large-merge / boss / events / reward / round10 / enemy-one-eye / enemy-two-eyes 场景");
            }

            _playerPieces.Clear();
            _enemyPieces.Clear();
            _board.Reset();
            if (!StartRound(1, name == "enemy-two-eyes" ? 9 : (name == "round10" ? 10 : (name == "boss" || name == "reward" ? 11 : 1))))
            {
                return false;
            }
            // 新战术夹具保持正常六张手牌，让人工可以自由布阵观察，不混入原合成夹具的三星大棋。
            if (name == "enemy-one-eye" || name == "enemy-two-eyes") { return true; }
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
                if (name == "reward")
                {
                    Piece king = _enemyPieces.Find(piece => piece.IsKing);
                    king.TakeDamage(king.Hp - 1);
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
    }
}
#endif

