using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>无尽手牌职责：提供无副作用升级目标查询与指定身份的单卡原子提交，保持模型/手牌/回执一致。</summary>
    public sealed partial class EndlessService
    {
        /// <summary>
        /// 手牌拖曳的只读升级预览。有效的preferredId优先，之后按低星级、低Id选一枚主力。
        /// 查询不抽随机数、不消费手牌，也不改变选择；同一输入反复查询得到同一目标，箭头不会乱跳。
        /// 返回null表示当前没有合法升级对象，调用方可以继续显示部署落点。
        /// </summary>
        public Piece CardUpgradeTarget(int index, int preferredId = 0)
        {
            if (Run == null || !CanArrange() || index < 0 || index >= Run.hand.Count)
            {
                return null;
            }
            PieceKind kind = (PieceKind)Run.hand[index];
            Piece best = null;
            foreach (EndlessUnit actor in Units)
            {
                Piece candidate = actor.Piece;
                // actor已经在当前名单里，直接检查它的资格；不要再Find同一Id，让一次预览退化成平方遍历。
                if (actor.Stone || actor.Summoned || !candidate.IsAlive || candidate.Side != GameSide.Player || candidate.Def.Kind != kind || candidate.Def.Star >= StarTier.Three)
                {
                    continue;
                }
                if (candidate.Id == preferredId)
                {
                    return candidate;
                }
                if (best == null || candidate.Def.Star < best.Def.Star || (candidate.Def.Star == best.Def.Star && candidate.Id < best.Id))
                {
                    best = candidate;
                }
            }
            return best;
        }

        /// <summary>
        /// 松手提交箭头已经指向的那枚棋子。目标死亡/移除/满星时明确拒绝，绝不换一个目标偷偷升级。
        /// 预览到提交之间只保存Id；最终预检和单卡原子提交仍由服务负责，UI不能直接改变棋子星级。
        /// </summary>
        public bool UpgradeCardTarget(int index, int targetId)
        {
            if (Run == null || !CanArrange() || targetId <= 0)
            {
                return Reject("当前阶段不能升级，手牌未消耗");
            }
            EndlessUnit actor = Find(targetId);
            return UpgradeWithCard(index, actor);
        }

        /// <summary>本局成功打牌序号，用于验证拖放恰好消费一次；动作失败不会递增。</summary>
        public int CardPlaySerial
        {
            get;
            private set;
        }
        /// <summary>最近动作的事实回执，Deploy或Upgrade；表现层不据此修改战力。</summary>
        public string LastCardAction
        {
            get;
            private set;
        }
        = "";
        /// <summary>本局最近打牌目标Id，部署/升级成功时更新，失败不改变。</summary>
        public int LastCardTarget
        {
            get;
            private set;
        }

        /// <summary>
        /// 拖放与自动化共同入口。合法空格打牌时随机选一个同名未满星主力升级，
        /// 直接指向同名棋子时优先升级该棋子；不同名棋子、棋心、越界或暂停拒绝且不扣牌。
        /// 没有可升级同名主力才按原部署入口新增一格棋子，三张场上棋子不会被自动消耗。
        /// </summary>
        public bool PlayCardAt(int index, int x, int y)
        {
            var position = new BoardPos(x, y);
            if (Run == null || !CanArrange() || index < 0 || index >= Run.hand.Count || !position.IsInside(9) || (x == 4 && y == 4))
            {
                return Reject("请把手牌拖到合法空格或可升级的同名棋子");
            }
            Piece at = Board.GetPiece(position);
            if (at != null)
            {
                if (!CanMergeCardInto(at, (PieceKind)Run.hand[index]))
                {
                    return Reject("目标不是可升级的同名棋子，手牌未消耗");
                }
                return UpgradeWithCard(index, Find(at.Id));
            }
            return Deploy(index, position);
        }
        /// <summary>先计数再从存档随机源抽一个，不分配列表；非法落点在调用前已拒绝，因此不消耗随机数。</summary>
        private EndlessUnit RandomCardTarget(PieceKind kind)
        {
            int count = 0;
            foreach (EndlessUnit actor in Units)
            {
                if (CanMergeCardInto(actor.Piece, kind))
                {
                    count++;
                }
            }
            if (count == 0)
            {
                return null;
            }
            int pick = Next(count);
            foreach (EndlessUnit actor in Units)
            {
                if (CanMergeCardInto(actor.Piece, kind) && pick-- == 0)
                {
                    return actor;
                }
            }
            return null;
        }
        /// <summary>
        /// 单卡升级原子提交：先预检，再替换同一演员的Piece，保持Id/位置/计时与复活状态。
        /// 血量按原比例继承到新星级上限，避免每次升级偷偷回满；不动其它场上同名演员。
        /// </summary>
        private bool UpgradeWithCard(int index, EndlessUnit actor)
        {
            if (index < 0 || index >= Run.hand.Count || actor == null || !CanMergeCardInto(actor.Piece, (PieceKind)Run.hand[index]))
            {
                return Reject("同名棋子已满星或目标已失效，手牌未消耗");
            }
            Piece old = actor.Piece;
            BoardPos position = old.Pos;
            if (!Fits(position, 1, GameSide.Player, old))
            {
                return Reject("升级落点失效，手牌未消耗");
            }
            StarTier star = (StarTier)((int)old.Def.Star + 1);
            var upgraded = new Piece(old.Id, PieceCatalog.Get(old.Def.Kind, star), GameSide.Player, size: 1, attackScale: old.AttackScale);
            // 先恢复新星级基础生命，再应用本局倍率，避免把已有倍率存成基础值后再次相乘。
            int hp = Mathf.Clamp(Mathf.CeilToInt(upgraded.MaxHp * old.HpRatio), 1, upgraded.MaxHp);
            upgraded.RestoreDetachedVitals(upgraded.MaxHp, hp, old.EyeReviveUsed);
            upgraded.SetRoundHealthScale(PlayerHealthScale());
            Board.Remove(old);
            if (!Board.Place(upgraded, position))
            {
                Board.Place(old, position);
                return Reject("升级占格提交失败，已还原原棋子");
            }
            actor.Piece = upgraded;
            actor.HoldUntil = Run.seconds + V("ManualHoldSeconds");
            Run.hand.RemoveAt(index);
            Run.selectedCard = Run.selectedUnit = -1;
            CardPlaySerial++;
            LastCardAction = "Upgrade";
            LastCardTarget = old.Id;
            RefreshRules();
            return true;
        }
    }
}
