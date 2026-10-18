// ============================================================
// BattleService.HandChecks.cs —— 编辑器中检查刷新事务与远程实际出手时间。
// 使用真实服务推进和事件快照，避免“配置写了两倍，战斗却仍把两发挤在同一帧”。
// ============================================================
#if UNITY_EDITOR
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
        private static void VerifyHandRefreshAndAttackRate(Action<bool, string> check)
        {
            var battle = new BattleService(false);
            battle.StartRound(1, 1);
            var previous = new List<HandCard>(battle.Hand);
            battle.SelectCard(previous[0].Id);
            string boardBefore = battle.Board.Dump();
            int notifications = 0;
            object owner = new object();
            RevEvent.AddEventListener(Events.HandRerolled, () => notifications++, owner: owner);
            try
            {
                check(battle.RefreshHand() && battle.Hand.Count == 6, "剩六张时刷新仍为六张");
                check(battle.CardsRemaining == 6 && battle.Board.Dump() == boardBefore, "刷新不增加额度、不改变棋盘");
                check(battle.SelectedCardId == 0 && battle.SelectedPieceId == 0 && !battle.EyeStoneSelected, "刷新清除互斥选择");
                for (int i = 0; i < previous.Count; i++)
                {
                    check(battle.Hand[i].Id != previous[i].Id && battle._hand.Find(previous[i].Id) == null, "旧牌编号全部作废：" + i);
                    check(battle.Hand[i].Star == previous[i].Star && battle.Hand[i].Kind != previous[i].Kind, "保留星级并换不同已解锁棋种：" + i);
                }
                check(!battle.TryPlayCard(previous[0].Id, new BoardPos(0, 0)) && battle.Hand.Count == 6, "旧按钮编号不能消费新牌");
                check(battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(0, 0)), "刷新后的新牌可以正常落子");
                check(battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(3, 0)), "可再消耗一张牌");
                boardBefore = battle.Board.Dump();
                check(battle.RefreshHand() && battle.Hand.Count == 4 && battle.CardsRemaining == 4, "落掉两张后只刷新四张，不补回额度");
                check(battle.Board.Dump() == boardBefore && notifications == 2, "两次刷新各通知一次，已落棋子完全不变");

                // 高星牌也保持星级；仅剩一种可抽棋种时允许同种，不能陷入一直重抽的死循环。
                battle._hand.Clear();
                battle._hand.Add(PieceKind.Shield, StarTier.Three);
                battle._unlockedKinds.Clear();
                battle._unlockedKinds.Add(PieceKind.Shield);
                check(battle.RefreshHand() && battle.Hand.Count == 1 && battle.Hand[0].Star == StarTier.Three, "高星牌刷新不会降星，单棋种能正常结束");
                battle._hand.Clear();
                check(!battle.RefreshHand() && battle.Hand.Count == 0 && notifications == 3, "空手拒绝刷新且不播放动画通知");
                battle._hand.Add(PieceKind.Shield, StarTier.One);
                battle.StartBattle();
                int id = battle.Hand[0].Id;
                check(!battle.RefreshHand() && battle.Hand[0].Id == id && notifications == 3, "战斗期刷新失败，手牌和通知保持原样");
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
            }

            int singleDamage = 0;
            for (int rate = 1; rate <= 2; rate++)
            {
                battle = new BattleService(false);
                battle.ConfigureRangedAttackRate(rate);
                Piece archer = battle.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                Piece victim = battle.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(6, 6), StarTier.Three);
                battle.RefreshGoState();
                battle.StartBattle();
                int hp = victim.Hp;
                var attacks = new List<PieceAttackInfo>();
                owner = new object();
                RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, attacks.Add, owner: owner);
                try
                {
                    battle.Tick(0.3125f);
                    check(attacks.Count == 0 && victim.Hp == hp, "未达到间隔不提前攻击，倍率：" + rate);
                    battle.Tick(0.3125f);
                    check(attacks.Count == rate - 1, "半周期仅两倍远程开火，倍率：" + rate);
                    battle.Tick(0.625f);
                    check(attacks.Count == rate && victim.Hp < hp, "一个周期实际出手次数匹配配置，倍率：" + rate);
                    check(archer.Pos == new BoardPos(0, 0), "提高攻击频率不导致我方移动，倍率：" + rate);
                    foreach (PieceAttackInfo attack in attacks)
                    {
                        check(attack.PieceId == archer.Id && attack.Kind == PieceKind.Archer && attack.Position == archer.Pos && attack.Side == GameSide.Player, "每次真实攻击派发正确快照");
                    }
                    if (rate == 1) { singleDamage = hp - victim.Hp; }
                    else { check(hp - victim.Hp == singleDamage * 2, "相同周期两倍频率造成两倍真实伤害"); }
                }
                finally
                {
                    RevEvent.RemoveAllByOwner(owner);
                }
            }

            // 高频回归必须观察真实五秒出手次数，不能只检查常量改没改；高生命靶子避免因提前击败目标少算攻击。
            battle = new BattleService(false);
            Piece fastArcher = battle.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
            Piece durable = new Piece(999, PieceCatalog.Get(PieceKind.Shield, StarTier.Three), GameSide.Enemy, hpScale: 1000f);
            battle._board.Place(durable, new BoardPos(6, 6));
            battle._enemyPieces.Add(durable);
            battle.RefreshGoState();
            battle.StartBattle();
            int acceleratedAttacks = 0;
            owner = new object();
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info => { if (info.PieceId == fastArcher.Id) { acceleratedAttacks++; } }, owner: owner);
            try
            {
                battle.Tick(5f);
                check(acceleratedAttacks > 8 && durable.Hp < durable.MaxHp, "五秒真实远程攻击多于旧版八次，确实提高频率并扣血");
                check(fastArcher.Pos == new BoardPos(0, 0), "高频远程保持原阵位");
            }
            finally { RevEvent.RemoveAllByOwner(owner); }

            foreach (float invalid in new[] { 0f, -1f, 5f, float.NaN, float.PositiveInfinity })
            {
                bool rejected = false;
                try { battle.ConfigureRangedAttackRate(invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                check(rejected && battle.RangedActionsPerTick == 2f, "非法攻速拒绝且保留旧配置：" + invalid);
            }
        }
    }
}
#endif
