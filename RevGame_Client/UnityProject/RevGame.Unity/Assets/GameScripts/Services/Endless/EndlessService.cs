using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;
using UnityEngine;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽棋局的事实所有者。六章BattleService不会参与此局，永久攻血与章节进度不会带入。
    /// 名单、外围缓冲、伤害结算和随机源都归本实例；UI/GM使用相同公开动作。
    /// </summary>
    public sealed partial class EndlessService : IDisposable
    {
        public readonly BoardModel Board = new BoardModel(9, new BoardPos(4, 4));
        /// <summary>本局轻量ECS世界，出生后持有组件与索引，退出即清空；六章不会创建它。</summary>
        public EndlessEcsWorld World
        {
            get;
            private set;
        }
        = new EndlessEcsWorld(0);
        /// <summary>敌潮组件池唯一真源；保存时才导出快照，不从Run.horde驱动热循环。</summary>
        public EndlessHordeComponents Horde => World.Horde;
        /// <summary>盘内演员和盘外领主的实体组件索引，随World一起释放。</summary>
        public EndlessActorComponents Units => World.Actors;
        /// <summary>按用途拥有独立候选缓冲，范围攻击嵌套不覆盖普攻/雷链正在遍历的名单。</summary>
        private readonly List<int> _attackCandidates = new List<int>(256), _areaCandidates = new List<int>(256);
        private readonly List<int> _chainCandidates = new List<int>(256), _auraCandidates = new List<int>(256);
        private readonly List<int> _rayCandidates = new List<int>(256);
        private readonly List<int> _comboCandidates = new List<int>(1024), _slowCandidates = new List<int>(1024);
        /// <summary>移动遍历快照只保存槽号；死亡回调会重建活跃索引，不能让它改变正在推进的名单。</summary>
        private int[] _marchSlots = new int[0];
        public readonly EndlessImpact[] Impacts = new EndlessImpact[512];
        /// <summary>伤害数字回执，表现层按 DamageSerial 读取新增部分；容量只影响显示，不影响结算。</summary>
        public readonly EndlessDamageReceipt[] DamageReceipts = new EndlessDamageReceipt[512];
        /// <summary>死亡回执，表现层据此播放六章同款溶解；同上，只是显示用的有界缓冲。</summary>
        public readonly EndlessDeathReceipt[] DeathReceipts = new EndlessDeathReceipt[128];
        // 81格棋盘和512条短效收据是布局/表现结构容量，伤害不依赖表现缓冲容量。
        private readonly List<BoardPos> _targets = new List<BoardPos>(81);
        private readonly HashSet<Piece> _consumed = new HashSet<Piece>();
        private int _ruleVersion = -1, _impactSerial;
        private bool _saveAtStepEnd;
        /// <summary>最新一条伤害回执的序号；表现层记住自己读到哪，只处理比它新的。</summary>
        public int DamageSerial
        {
            get;
            private set;
        }
        /// <summary>最新一条攻击/范围回执（Impacts）的序号，用法同 DamageSerial。</summary>
        public int ImpactSerial => _impactSerial;
        /// <summary>最新一条死亡回执的序号。</summary>
        public int DeathSerial
        {
            get;
            private set;
        }
        public EndlessRun Run
        {
            get;
            private set;
        }
        public string LastError
        {
            get;
            private set;
        }
        = "";
        public bool Isolated
        {
            get;
            private set;
        }
        public long TickCount
        {
            get;
            private set;
        }
        public double TickMilliseconds
        {
            get;
            private set;
        }
        public double MaxTickMilliseconds
        {
            get;
            private set;
        }
        private static float V(string key)
        {
            return EndlessConfig.Value(key);
        }
        private static int N(string key)
        {
            return EndlessConfig.Count(key);
        }
        private bool Reject(string reason)
        {
            LastError = reason;
            return false;
        }
        /// <summary>新局使用标准图鉴和自己的随机状态。seed=0也可用；内部避开xorshift的零锁死状态。</summary>
        public void Begin(int formation, int difficulty, int seed, bool isolated = false)
        {
            if (formation < 0 || formation > 2 || difficulty < 0 || difficulty > 2)
            {
                throw new ArgumentOutOfRangeException("无尽棋阵或难度不合法");
            }
            Clear();
            Isolated = isolated;
            World = new EndlessEcsWorld(N("HordeLimit"));
            Run = new EndlessRun
            {
                formation = formation, difficulty = difficulty, seed = seed, random = unchecked((uint)seed) | 1u,
                runId = Guid.NewGuid().ToString("N"), heart = V("HeartHp"), phase = EndlessPhase.Formation,
                formationLeft = V("PreparationSeconds"), rerolls = N("Rerolls"), banishes = N("Banishes"),
                nextMinute = V("MinuteSeconds"), nextBoss = V("BossInterval"), nextElite = V("EliteInterval"),
                nextEvent = V("EventSeconds"), nextSave = V("SaveSeconds"), horde = null
            };
            PieceKind primary = formation == 0 ? PieceKind.Crossbow : formation == 1 ? PieceKind.MageFire : PieceKind.Priest;
            // 开局棋阵来自 Excel EndlessFormation：每个棋阵 8 枚左右，开局就能凑出组合技、近战带剑气守门，满屏开火。
            foreach (EndlessFormation row in RevDataTableManager.Get<EndlessFormationTable>().All)
            {
                if (row.Formation == formation)
                {
                    AddPlayer(EndlessConfig.FormationKind(row), row.X, row.Y, row.Stone != 0);
                }
            }
            Run.hand.Add((int)primary);
            Run.hand.Add((int)PieceKind.Shield);
            Run.hand.Add((int)PieceKind.Archer);
            // 起手路线从一级开始，避免法师/驻眼棋要先等随机抽中本职形态才具备清潮能力。
            Run.mains[formation] = 1;
            Run.minuteHeart = Run.heart;
            RefreshRules();
            Save();
        }
        /// <summary>唯一随机入口。存档保存random而不只是seed，恢复后同一选择不能重抽。</summary>
        private int Next(int maximum)
        {
            uint x = Run.random;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            Run.random = x;
            return (int)(x % (uint)maximum);
        }
        private EndlessUnit AddPlayer(PieceKind kind, int x, int y, bool stone, bool summoned = false)
        {
            var piece = new Piece(Run.nextId++, PieceCatalog.Get(kind, StarTier.One), GameSide.Player, size: 1);
            // 纸甲祝福与领主“不灭圣体”都改生命上限；新上场的棋子也要吃到，否则滚雪球只对老棋子生效。
            piece.SetRoundHealthScale(PlayerHealthScale());
            if (!Fits(new BoardPos(x, y), piece.Size, GameSide.Player) || !Board.Place(piece, new BoardPos(x, y)))
            {
                throw new InvalidOperationException("无尽初始棋阵占格冲突");
            }
            var unit = new EndlessUnit
            {
                Piece = piece, Stone = stone, Summoned = summoned
            };
            Units.Add(unit);
            return unit;
        }
        /// <summary>棋心是中立目标，不构成炮架/连气。全部放置和移动先拒绝覆盖中央格。</summary>
        private bool Fits(BoardPos position, int size, GameSide side, Piece ignore = null, HashSet<Piece> consumed = null)
        {
            if (position.X <= 4 && position.Y <= 4 && position.X + size > 4 && position.Y + size > 4)
            {
                return false;
            }
            return Board.CanFit(position, size, side, ignore, consumed);
        }
        public int PlayerCount(bool includeSummons = false)
        {
            int count = 0;
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit u = Units[i];
                if (u.Piece.Side == GameSide.Player && !u.Stone && (includeSummons || !u.Summoned))
                {
                    count++;
                }
            }
            return count;
        }
        public int Occupied(GameSide side)
        {
            int cells = side == GameSide.Player ? 1 : 0;
            for (int i = 0; i < Units.Count; i++)
            {
                Piece p = Units[i].Piece;
                if (p.Side == side && !Units[i].Outside)
                {
                    cells += p.Size * p.Size;
                }
            }
            return cells;
        }
        public int HordeCount()
        {
            int count = 0;
            for (int i = 0; i < Horde.Length; i++)
            {
                if (Horde[i].active)
                {
                    count++;
                }
            }
            return count;
        }
        public bool StartWave()
        {
            if (Run.phase != EndlessPhase.Formation && Run.phase != EndlessPhase.Preparation)
            {
                return Reject("当前不在布阵或整备阶段");
            }
            Run.phase = EndlessPhase.Fighting;
            Run.selectedCard = Run.selectedUnit = -1;
            return true;
        }
        public bool Pause()
        {
            if (Run.phase == EndlessPhase.Paused)
            {
                Run.phase = Run.previousPhase;
                return true;
            }
            if (Run.phase == EndlessPhase.Result)
            {
                return Reject("本局已结算");
            }
            Run.previousPhase = Run.phase;
            Run.phase = EndlessPhase.Paused;
            return true;
        }
        public bool Rearrange()
        {
            if (Run.phase != EndlessPhase.Fighting || Run.slowMinute == (int)(Run.seconds / V("MinuteSeconds")))
            {
                return Reject("本分钟改阵已用，或当前不在战斗");
            }
            Run.slowMinute = (int)(Run.seconds / V("MinuteSeconds"));
            Run.slowLeft = V("SlowSeconds");
            Run.slowMoves = N("SlowMoves");
            return true;
        }
        /// <summary>
        /// 能否操作棋盘（选牌、落子、移动棋子）。战斗中也可以：无尽模式全程不暂停，玩家随时调整阵型、随时放牌。
        /// 只有暂停、结算等弹窗状态下不能操作。
        /// </summary>
        private bool CanArrange()
        {
            return Run.phase == EndlessPhase.Formation || Run.phase == EndlessPhase.Preparation || Run.phase == EndlessPhase.Fighting;
        }
        public bool SelectCard(int index)
        {
            if (!CanArrange() || index < 0 || index >= Run.hand.Count)
            {
                return Reject("现在不能部署这张牌");
            }
            Run.selectedCard = index;
            Run.selectedUnit = -1;
            return true;
        }
        public bool ClickCell(int x, int y)
        {
            var pos = new BoardPos(x, y);
            if (!pos.IsInside(9))
            {
                return Reject("落点超出棋盘");
            }
            if (!CanArrange())
            {
                return Reject("暂停中不能操作棋盘");
            }
            if (Run.selectedCard >= 0)
            {
                Piece mergeTarget = Board.GetPiece(pos);
                if (mergeTarget == null)
                {
                    return Deploy(Run.selectedCard, pos);
                }
                if (Run.selectedCard < Run.hand.Count && CanMergeCardInto(mergeTarget, (PieceKind)Run.hand[Run.selectedCard]))
                {
                    return MergeWithCard(mergeTarget);
                }
                // 选着手牌却点了一枚不能合星的棋子：玩家的意图是“改去操作这枚棋子”。
                // 以前这里直接走合星并失败，选牌状态没清掉，棋子既选不中也显示不出移动范围。
                // 现在放下手牌，落到下面的“选中己方棋子”分支；点到敌人/棋心则保持选牌并给出原因。
                if (mergeTarget.Side != GameSide.Player)
                {
                    return Reject("这里不能部署，点空位落子或点同名未满星棋子升星");
                }
                Run.selectedCard = -1;
            }
            Piece at = Board.GetPiece(pos);
            if (at != null && at.Side == GameSide.Player)
            {
                Run.selectedUnit = at.Id;
                return true;
            }
            EndlessUnit unit = Find(Run.selectedUnit);
            if (unit == null || !Fits(pos, unit.Piece.Size, GameSide.Player, unit.Piece))
            {
                return Reject("先选择己方棋，再点击空位；棋心不可覆盖");
            }
            RememberEyes();
            if (!Board.Move(unit.Piece, pos))
            {
                return Reject("落点非法或棋子处于锁定状态");
            }
            // 玩家亲手摆的位置优先：近战棋在 ManualHoldSeconds 秒内不被自动索敌拉走。
            unit.HoldUntil = Run.seconds + V("ManualHoldSeconds");
            Run.selectedUnit = -1;
            RefreshRules();
            unit.StationarySince = Run.seconds;
            unit.SteadyShield = false;
            ApplyBrokenEyeBonus();
            return true;
        }
        /// <summary>先预检合法空格，再随机升级一个同名未满星主力；没有目标才部署，成功后只扣一张牌。</summary>
        private bool Deploy(int index, BoardPos pos)
        {
            if (index < 0 || index >= Run.hand.Count)
            {
                return Reject("棋盘放满了，把牌放到同名棋子上可以升星");
            }
            if (!Fits(pos, 1, GameSide.Player))
            {
                return Reject("此格不可部署");
            }
            PieceKind kind = (PieceKind)Run.hand[index];
            EndlessUnit upgrade = RandomCardTarget(kind);
            if (upgrade != null)
            {
                return UpgradeWithCard(index, upgrade);
            }
            EndlessUnit placed = AddPlayer(kind, pos.X, pos.Y, false);
            placed.HoldUntil = Run.seconds + V("ManualHoldSeconds");
            Run.hand.RemoveAt(index);
            Run.selectedCard = -1;
            CardPlaySerial++;
            LastCardAction = "Deploy";
            LastCardTarget = placed.Piece.Id;
            RefreshRules();
            return true;
        }
        /// <summary>Id来自本局实体注册表，死亡/升星移除时同步失效，不扫描整个演员名单。</summary>
        public EndlessUnit Find(int id)
        {
            return Units.Find(id);
        }
        private void RefreshRules()
        {
            if (_ruleVersion == Board.TopologyVersion)
            {
                return;
            }
            GoRules.Evaluate(Board);
            _ruleVersion = Board.TopologyVersion;
        }
        private void Impact(Vector2 from, Vector2 to, int route, float radius = 0, Piece source = null)
        {
            ImpactFx(from, to, route, radius, source == null ? 0 : source.Id, source == null ? PieceKind.Swordman : source.Def.Kind, source != null, 0);
        }
        /// <summary>完整版回执：可以单独指定外观棋种（组合技的弹道/范围没有出手棋子）和组合技编号。</summary>
        private void ImpactFx(Vector2 from, Vector2 to, int route, float radius, int sourceId, PieceKind kind, bool hasKind, int combo)
        {
            int serial = ++_impactSerial;
            Impacts[serial % Impacts.Length] = new EndlessImpact
            {
                serial = serial, from = from, to = to, route = route, time = Run.seconds, radius = radius,
                sourceId = sourceId, sourceKind = kind, hasKind = hasKind, combo = combo
            };
        }
        /// <summary>
        /// 记一条伤害数字。调用方必须已经把血扣完，这里只抄结果；0 伤害（被挡、溢出）不显示。
        /// </summary>
        private void ReportDamage(int target, Vector2 position, float damage, GameSide side, BattleDamageKind kind = BattleDamageKind.Normal)
        {
            int shown = Mathf.CeilToInt(damage);
            if (shown <= 0)
            {
                return;
            }
            int serial = ++DamageSerial;
            DamageReceipts[serial % DamageReceipts.Length] = new EndlessDamageReceipt
            {
                serial = serial, target = target, damage = shown, position = position, side = side, kind = kind
            };
            RevEvent.DispatchEvent<YayaCombatFact>(Events.YayaCombatObserved, new YayaCombatFact(2, side, shown));
        }
        /// <summary>记一条死亡。只在真正死亡处调用；三合一吃掉、进盘转身份都不算死亡。</summary>
        private void ReportDeath(int id, Vector2 position, bool horde)
        {
            // 仍在Units移除前取得身份；外围敌潮必为敌方，三合一和主动拆子不会走此出口。
            GameSide side = horde ? GameSide.Enemy : Units.Find(id).Piece.Side;
            RevEvent.DispatchEvent<YayaCombatFact>(Events.YayaCombatObserved, new YayaCombatFact(2, side, 0, side == GameSide.Enemy ? 1 : 0, side == GameSide.Player ? 1 : 0));
            int serial = ++DeathSerial;
            DeathReceipts[serial % DeathReceipts.Length] = new EndlessDeathReceipt
            {
                serial = serial, id = id, position = position, horde = horde
            };
        }
        /// <summary>
        /// 外围敌人的出生槽：左右两侧交替，九行按步长 4 轮转（0,4,8,3,7,2,6,1,5…）。
        /// 这样连续刷出的敌人在两侧九条横线上均匀铺开，不会扎堆在中间几行；
        /// 步长 4 与 9 互质，每 18 只正好把两侧 18 个出生口各用一次。
        /// 这是棋盘结构（9 行、左右两侧），不是策划数值，所以不进表。
        /// </summary>
        private void NextLane(out int gate, out int row)
        {
            int slot = Run.laneCursor++;
            gate = slot & 1;
            row = (slot >> 1) * 4 % 9;
        }
        /// <summary>
        /// 绿色落子提示用：当前选中的手牌或棋子能不能落到这一格。
        /// 和 ClickCell 走同一套判断（占格、棋心、人口/占地上限、同名升星），保证“亮的格子点下去一定成功”。
        /// 只读查询，不改任何状态。
        /// </summary>
        public bool CanPlaceAt(int x, int y)
        {
            if (Run == null || !CanArrange())
            {
                return false;
            }
            var pos = new BoardPos(x, y);
            if (Run.selectedCard >= 0 && Run.selectedCard < Run.hand.Count)
            {
                Piece at = Board.GetPiece(pos);
                if (at != null)
                {
                    // 点同名一星棋子可以升星（规则见 MergeWithCard：还需要场上另一枚同名一星），六章同样把这类格子点亮。
                    return CanMergeCardInto(at, (PieceKind)Run.hand[Run.selectedCard]);
                }
                return Fits(pos, 1, GameSide.Player);
            }
            EndlessUnit unit = Find(Run.selectedUnit);
            if (unit == null)
            {
                return false;
            }
            // 选中棋子自己脚下也点亮，和六章“已选中的棋子格子高亮”一致。
            return unit.Piece.Pos.Equals(pos) || Fits(pos, unit.Piece.Size, GameSide.Player, unit.Piece);
        }
        /// <summary>一张同名手牌可升级一个未满星主力；不再要求第二个场上伙伴，不消费其它棋子。</summary>
        private bool CanMergeCardInto(Piece target, PieceKind card)
        {
            EndlessUnit actor = target == null ? null : Find(target.Id);
            return actor != null && !actor.Stone && !actor.Summoned && target.IsAlive && target.Side == GameSide.Player && target.Def.Kind == card && target.Def.Star < StarTier.Three;
        }
        private void AddScore(long value)
        {
            if (!Run.scoreStopped && value > 0)
            {
                Run.score = value > long.MaxValue - Run.score ? long.MaxValue : Run.score + value;
            }
        }
        public void Dispose()
        {
            Clear();
        }
        private void Clear()
        {
            Units.Clear();
            World?.Horde.Clear();
            Board.Reset();
            _ruleVersion = -1;
            _impactSerial = 0;
            Array.Clear(Impacts, 0, Impacts.Length);
            DamageSerial = DeathSerial = 0;
            ComboCasts = 0;
            CardPlaySerial = 0;
            LastCardAction = "";
            LastCardTarget = 0;
            Banner = MajorBanner = MajorBannerColor = "";
            BlockSerial = 0;
            System.Array.Clear(ShieldZone, 0, ShieldZone.Length);
            ShieldZoneVersion++;
            _zonePlanAt = 0;
            Array.Clear(DamageReceipts, 0, DamageReceipts.Length);
            Array.Clear(DeathReceipts, 0, DeathReceipts.Length);
            TickCount = 0;
            TickMilliseconds = MaxTickMilliseconds = 0;
            _saveAtStepEnd = false;
            _targets.Clear();
            _consumed.Clear();
            Run = null;
            World = new EndlessEcsWorld(0);
        }
    }
}
