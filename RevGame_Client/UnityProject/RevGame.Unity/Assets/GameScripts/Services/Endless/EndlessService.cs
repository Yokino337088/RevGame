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
        public readonly List<EndlessUnit> Units = new List<EndlessUnit>(81);
        public readonly EndlessImpact[] Impacts = new EndlessImpact[256];
        // 81格棋盘和256条短效收据是布局/表现结构容量，伤害不依赖表现缓冲容量。
        private readonly List<BoardPos> _targets = new List<BoardPos>(81);
        private readonly HashSet<Piece> _consumed = new HashSet<Piece>();
        private int _ruleVersion = -1, _impactSerial;
        private bool _saveAtStepEnd;
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
            Run = new EndlessRun
            {
                formation = formation, difficulty = difficulty, seed = seed, random = unchecked((uint)seed) | 1u,
                runId = Guid.NewGuid().ToString("N"), heart = V("HeartHp"), phase = EndlessPhase.Formation,
                formationLeft = V("PreparationSeconds"), rerolls = N("Rerolls"), banishes = N("Banishes"),
                nextMinute = V("MinuteSeconds"), nextBoss = V("BossInterval"), nextElite = V("EliteInterval"),
                nextEvent = V("EventSeconds"), nextSave = V("SaveSeconds"), horde = new EndlessHorde[N("HordeLimit")]
            };
            PieceKind primary = formation == 0 ? PieceKind.Crossbow : formation == 1 ? PieceKind.MageFire : PieceKind.Priest;
            AddPlayer(primary, 3, 3, false);
            AddPlayer(formation == 0 ? PieceKind.Crossbow : PieceKind.Shield, 2, 3, false);
            AddPlayer(PieceKind.Shield, 3, 2, false);
            if (formation == 2)
            {
                AddPlayer(PieceKind.Shield, 4, 3, true);
                AddPlayer(PieceKind.Shield, 3, 4, true);
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
            var piece = new Piece(Run.nextId++, PieceCatalog.Get(kind, StarTier.One), GameSide.Player);
            if (Run.rare[2])
            {
                piece.SetRoundHealthScale(1 - V("PaperHealthLoss"));
            }
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
                if (p.Side == side)
                {
                    cells += p.Size * p.Size;
                }
            }
            return cells;
        }
        public int HordeCount()
        {
            int count = 0;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (Run.horde[i].active)
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
        private bool CanArrange()
        {
            return Run.phase == EndlessPhase.Formation || Run.phase == EndlessPhase.Preparation || (Run.phase == EndlessPhase.Fighting && Run.slowLeft > 0 && Run.slowMoves > 0);
        }
        public bool SelectCard(int index)
        {
            if (!CanArrange() || Run.phase == EndlessPhase.Fighting || index < 0 || index >= Run.hand.Count)
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
            // 战斗中点自己的棋子，就直接进入慢动作改阵，不需要额外的按钮。每分钟一次的限制仍由 Rearrange 把关。
            if (Run.phase == EndlessPhase.Fighting && Run.slowLeft <= 0)
            {
                Piece tapped = Board.GetPiece(pos);
                if (tapped != null && tapped.Side == GameSide.Player && !Rearrange())
                {
                    return false;
                }
            }
            if (!CanArrange())
            {
                return Reject("点自己的棋子可以调整站位");
            }
            if (Run.selectedCard >= 0)
            {
                Piece mergeTarget = Board.GetPiece(pos);
                if (mergeTarget != null)
                {
                    return MergeWithCard(mergeTarget);
                }
                return Deploy(Run.selectedCard, pos);
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
            if (Run.phase == EndlessPhase.Fighting)
            {
                Run.slowMoves--;
            }
            Run.selectedUnit = -1;
            RefreshRules();
            unit.StationarySince = Run.seconds;
            unit.SteadyShield = false;
            ApplyBrokenEyeBonus();
            return true;
        }
        /// <summary>先预检空间/上限，再扣牌。失败不改变手牌、随机数或占格。</summary>
        private bool Deploy(int index, BoardPos pos)
        {
            if (index >= Run.hand.Count || PlayerCount() >= N("PlayerLimit") || Occupied(GameSide.Player) + 1 > N("PlayerCells"))
            {
                return Reject("棋盘放满了，把牌放到同名棋子上可以升星");
            }
            if (!Fits(pos, 1, GameSide.Player))
            {
                return Reject("此格不可部署");
            }
            PieceKind kind = (PieceKind)Run.hand[index];
            AddPlayer(kind, pos.X, pos.Y, false);
            Run.hand.RemoveAt(index);
            Run.selectedCard = -1;
            RefreshRules();
            AutoMerge();
            return true;
        }
        /// <summary>
        /// 自动三合一：场上凑齐三枚同名同星就自动升星，玩家不用找按钮。
        /// 合完可能又凑齐高一星的三枚，所以循环到没有可合的为止。
        /// 放不下等合不了的情况不算错误，不提示玩家，也不消耗棋子。
        /// </summary>
        private void AutoMerge()
        {
            string error = LastError;
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < Units.Count; i++)
                {
                    if (Units[i].Piece.Side != GameSide.Player)
                    {
                        continue;
                    }
                    Run.selectedUnit = Units[i].Piece.Id;
                    if (MergeSelected())
                    {
                        merged = true;
                        break;
                    }
                }
            }
            Run.selectedUnit = -1;
            LastError = error;
        }
        private EndlessUnit Find(int id)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].Piece.Id == id)
                {
                    return Units[i];
                }
            }
            return null;
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
        private void Impact(Vector2 from, Vector2 to, int route, float radius = 0)
        {
            int serial = ++_impactSerial;
            Impacts[serial % Impacts.Length] = new EndlessImpact
            {
                serial = serial, from = from, to = to, route = route, time = Run.seconds, radius = radius
            };
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
            Board.Reset();
            _ruleVersion = -1;
            _impactSerial = 0;
            Array.Clear(Impacts, 0, Impacts.Length);
            TickCount = 0;
            TickMilliseconds = MaxTickMilliseconds = 0;
            _saveAtStepEnd = false;
            _targets.Clear();
            _consumed.Clear();
            Run = null;
        }
    }
}
