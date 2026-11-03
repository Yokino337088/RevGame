#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>独立实例验证一张手牌升级一个演员，保留其它棋子、随机源和非法动作原子性。</summary>
        public static object CheckCardRules()
        {
            string save = _isolatedSave;
            var passed = new List<string>();
            try
            {
                using (var s = new EndlessService())
                {
                    s.Begin(0, 0, 1103, true);
                    s.Units.Clear();
                    s.Board.Reset();
                    var a = s.AddPlayer(PieceKind.Archer, 0, 0, false);
                    s.Run.hand.Clear();
                    s.Run.hand.Add((int)PieceKind.Archer);
                    int identity = a.Piece.Id;
                    Check(s.PlayCardAt(0, 3, 3) && s.Units.Count == 1 && a.Piece.Def.Star == StarTier.Two && a.Piece.Id == identity, "场上一个同名时一张牌直接升级，不要求第二枚伙伴", passed);
                    Check(s.Run.hand.Count == 0 && s.LastCardAction == "Upgrade" && s.CardPlaySerial == 1, "单卡升级准确消耗一张牌一次", passed);
                    var b = s.AddPlayer(PieceKind.Archer, 1, 0, false);
                    s.Run.hand.Add((int)PieceKind.Archer);
                    uint previewRandom = s.Run.random;
                    int previewVersion = s.Board.TopologyVersion;
                    Check(s.CardUpgradeTarget(0).Id == b.Piece.Id && s.CardUpgradeTarget(0, a.Piece.Id).Id == a.Piece.Id, "箭头默认优先低星级，明确指定目标保持同一身份", passed);
                    Check(s.Run.random == previewRandom && s.Board.TopologyVersion == previewVersion && s.Run.hand.Count == 1, "反复预览不抽随机、不改占格、不消费手牌", passed);
                    Check(s.PlayCardAt(0, 1, 0) && b.Piece.Def.Star == StarTier.Two && a.Piece.Def.Star == StarTier.Two && s.Units.Count == 2, "直接指向同名棋子只升级目标，另一枚同名保留", passed);
                    s.Run.hand.Add((int)PieceKind.Archer);
                    Check(s.PlayCardAt(0, 3, 3) && s.Units.Count == 2 && (a.Piece.Def.Star == StarTier.Three ^ b.Piece.Def.Star == StarTier.Three), "空格出牌随机升级恰好一枚同名，场上不会三合一", passed);
                    Check(a.Piece.Size == 1 && b.Piece.Size == 1, "两种升级路径始终一格", passed);
                    EndlessUnit full = a.Piece.Def.Star == StarTier.Three ? a : b;
                    s.Run.hand.Add((int)PieceKind.Archer);
                    int staleHand = s.Run.hand.Count;
                    int staleSerial = s.CardPlaySerial;
                    Check(!s.UpgradeCardTarget(0, full.Piece.Id) && s.Run.hand.Count == staleHand && s.CardPlaySerial == staleSerial, "满星目标提交拒绝，不换另一枚同名棋子、不扣牌", passed);
                    s.Run.hand.Clear();
                    s.Run.hand.Add((int)PieceKind.Priest);
                    uint random = s.Run.random;
                    int hand = s.Run.hand.Count, version = s.Board.TopologyVersion, plays = s.CardPlaySerial;
                    Check(!s.PlayCardAt(0, 4, 4) && !s.PlayCardAt(0, 0, 0) && s.Run.hand.Count == hand && s.Run.random == random && s.Board.TopologyVersion == version && s.CardPlaySerial == plays, "棋心和不同名目标拒绝，不扣牌不改随机源占格", passed);
                    Check(s.PlayCardAt(0, 3, 3) && s.Units.Count == 3 && s.LastCardAction == "Deploy", "没有可升级同名时仍可正常新增一枚棋子", passed);
                    s.Pause();
                    s.Run.hand.Add((int)PieceKind.Archer);
                    hand = s.Run.hand.Count;
                    Check(!s.PlayCardAt(0, 1, 0) && s.Run.hand.Count == hand, "暂停拖放拒绝且保留手牌", passed);
                    s.Pause();
                    Check(s.Save() && s.Restore(true) && s.Units.Count == 3, "单卡升级与同名伙伴可完整读档", passed);
                }
                return new Dictionary<string, object> { ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed };
            }
            finally
            {
                _isolatedSave = save;
            }
        }
    }
}
#endif
