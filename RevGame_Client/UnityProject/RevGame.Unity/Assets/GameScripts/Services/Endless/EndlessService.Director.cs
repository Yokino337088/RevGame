using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>唯一推进入口；现实时间只用于改阵期限，其余计时共用战斗时间，暂停没有隐形刷怪。</summary>
        public void Tick(float realStep)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                TickCore(realStep);
            }
            finally
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                TickCount++;
                TickMilliseconds += ms;
                MaxTickMilliseconds = Math.Max(MaxTickMilliseconds, ms);
            }
        }
        /// <summary>配方（满级主祝福 + 对应辅印 + 核心）一凑齐就自动进化，这是爽点，不应该藏在按钮后面。</summary>
        private void AutoEvolve()
        {
            if (Run.cores <= 0)
            {
                return;
            }
            for (int i = 0; i < 6; i++)
            {
                if (Run.mains[i] >= N("MainMax") && Run.seals[i] > 0 && !Run.evolved[i])
                {
                    Evolve(i);
                    return;
                }
            }
        }
        private void TickCore(float realStep)
        {
            if (Run == null || Run.phase == EndlessPhase.Result)
            {
                return;
            }
            if (Run.phase == EndlessPhase.Formation)
            {
                Run.formationLeft = Mathf.Max(0, Run.formationLeft - realStep);
                if (Run.formationLeft == 0)
                {
                    StartWave();
                }
                return;
            }
            if (Run.phase != EndlessPhase.Fighting)
            {
                return;
            }
            float step = realStep;
            if (Run.slowLeft > 0)
            {
                step *= V("SlowScale");
                Run.slowLeft = Mathf.Max(0, Run.slowLeft - realStep);
            }
            int oldSeconds = (int)Run.seconds;
            Run.seconds += step;
            AddScore(((int)Run.seconds - oldSeconds) * N("ScoreSeconds"));
            RefreshRules();
            UpdateDirector();
            UpdateHorde(step);
            UpdateUnits(step);
            UpdateEyes(step);
            UpdateBurnZones();
            ResolveDeaths();
            RefreshRules();
            UpdateRisk();
            AutoEvolve();
            if (Run.heart <= 0)
            {
                Finish();
                return;
            }
            // 击杀/掉落在本步全部提交，再打开选择，避免弹窗截断一条死亡流水。
            if (Run.pendingLevels > 0 && Run.seconds >= Run.nextChoiceAt)
            {
                OfferUpgrade();
            }
            else if (Run.phase == EndlessPhase.Fighting && Run.seconds >= Run.nextMinute)
            {
                Run.nextMinute += V("MinuteSeconds");
                UpdateMinuteBudget();
                GiveCards(N("MinuteCards"));
                Run.phase = EndlessPhase.Preparation;
                Save();
            }
            else if (Run.phase == EndlessPhase.Fighting && Run.seconds >= Run.nextEvent)
            {
                Run.nextEvent += V("EventSeconds");
                Run.eventKind = ((int)(Run.nextEvent / V("EventSeconds")) - 2) % 4;
                Run.phase = EndlessPhase.Event;
                AutoEvent();
            }
            if (Run.seconds >= Run.nextSave)
            {
                Run.nextSave = Run.seconds + V("SaveSeconds");
                Save();
            }
            if (_saveAtStepEnd)
            {
                _saveAtStepEnd = false;
                Save();
            }
            if (PlayerCount(true) == 0 && !Run.rescueUsed)
            {
                Run.rescueUsed = true;
                Run.rescueUntil = Run.seconds + V("RescueSeconds");
                if (Run.hand.Count == N("HandLimit"))
                {
                    Run.hand.RemoveAt(Run.hand.Count - 1);
                }
                Run.hand.Add((int)PieceKind.Shield);
                Run.phase = EndlessPhase.Preparation;
            }
        }
        private void UpdateDirector()
        {
            float minute = Run.seconds % V("MinuteSeconds");
            if (minute < V("TailStart") && Run.seconds >= Run.nextSpawn)
            {
                float interval = V("SpawnInterval") * (minute >= V("PeakStart") ? V("PeakSpawnFactor") : 1) / Run.adaptiveBudget;
                if (Run.affixes.Contains(2) && minute >= V("PeakStart"))
                {
                    interval /= V("RiskSpawn");
                }
                if (Run.riskUntil > Run.seconds)
                {
                    interval /= V("RiskSpawn");
                }
                if (SpawnHorde())
                {
                    Run.nextSpawn = Run.seconds + interval;
                }
            }
            if (Run.seconds >= Run.nextInvader && SpawnInvader(false, -1))
            {
                Run.nextInvader = Run.seconds + V("EnemyInterval");
            }
            if (Run.seconds >= Run.nextElite && SpawnInvader(true, -1))
            {
                Run.nextElite = Run.seconds + V("EliteInterval");
            }
            if (Run.seconds >= Run.nextBoss && !HasBossIdentity())
            {
                int template = (int)((Run.nextBoss - 1) % V("CircuitSeconds") / V("BossInterval"));
                if (SpawnInvader(false, template))
                {
                    Run.nextBoss += V("BossInterval");
                }
            }
        }
        public EndlessUnit BossUnit()
        {
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].BossTemplate >= 0)
                {
                    return Units[i];
                }
            }
            return null;
        }
        private bool SpawnHorde()
        {
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (Run.horde[i].active)
                {
                    continue;
                }
                // 左右两侧 × 九行轮转出生（见 NextLane）：敌人对齐棋盘横线，从两侧均匀铺开再往里涌。
                NextLane(out int gate, out int row);
                if (Run.affixes.Contains(0))
                {
                    gate = (int)(Run.seconds / V("GateWait")) % 2;
                }
                float hp = V("HordeHp") * (1 + V("CircuitHpGrowth") * Math.Min(Run.circuit - 1, N("CircuitHpCap")));
                hp *= Run.difficulty == 1 ? V("DifficultyHp") : 1;
                Run.horde[i] = new EndlessHorde
                {
                    active = true, id = Run.nextId++, gate = gate, hp = hp, maxHp = hp,
                    x = gate == 0 ? -3 : 11,
                    y = row,
                    speed = V("HordeSpeed") * Mathf.Min(V("CircuitSpeedCap"), 1 + (Run.circuit - 1) * V("CircuitAttackGrowth"))
                };
                return true;
            }
            return false;
        }
        /// <summary>每只外围敌人在自己那一行里的排队名次（0 = 最前排），下标与 Run.horde 一致；每步重算，不进存档。</summary>
        private int[] _queueRank = new int[0];
        /// <summary>
        /// 计算排队名次：同一侧同一行里，比自己早出生（编号更小）的活敌人有几只，名次就是几。
        /// 用编号而不是当前坐标排序，冻结、减速、被击退时队形也不会前后乱跳。
        /// 上限 120 只时最多约 1.4 万次比较/步，固定步长 0.1 秒下可以接受，也不分配内存。
        /// </summary>
        private void RankHordeQueues()
        {
            if (_queueRank.Length != Run.horde.Length)
            {
                _queueRank = new int[Run.horde.Length];
            }
            for (int i = 0; i < Run.horde.Length; i++)
            {
                _queueRank[i] = 0;
                if (!Run.horde[i].active)
                {
                    continue;
                }
                int lane = LaneOf(Run.horde[i]);
                for (int n = 0; n < Run.horde.Length; n++)
                {
                    // 杂兵和入侵者各排各的队：入侵者停在棋盘边等进盘，杂兵要涌进去，混排会互相卡位。
                    if (n != i && Run.horde[n].active && Run.horde[n].id < Run.horde[i].id && LaneOf(Run.horde[n]) == lane && (Run.horde[n].kind == 0) == (Run.horde[i].kind == 0))
                    {
                        _queueRank[i]++;
                    }
                }
            }
        }
        /// <summary>出生口编号：哪一侧 × 第几行。旧存档里上下门点的敌人也有自己的口，不会和左右混排。</summary>
        private static int LaneOf(EndlessHorde h)
        {
            return h.gate * 16 + Mathf.RoundToInt(h.gate < 2 ? h.y : h.x);
        }
        private void UpdateHorde(float step)
        {
            RankHordeQueues();
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (!Run.horde[i].active)
                {
                    continue;
                }
                EndlessHorde h = Run.horde[i];
                if (h.burnUntil > Run.seconds && h.burnAt <= Run.seconds)
                {
                    ReportDamage(h.id, new Vector2(h.x, h.y), Mathf.Min(h.hp, h.burnDamage), GameSide.Enemy);
                    h.hp -= h.burnDamage;
                    h.burnAt = Run.seconds + 1;
                }
                float speed = h.frozenUntil > Run.seconds ? 0 : h.speed * (h.slowUntil > Run.seconds ? 1 - V("SlowRatio") : 1);
                if (h.kind == 0 && h.gate < 2)
                {
                    // 普通杂兵：沿自己那一行涌进棋盘（见 MarchHorde），和棋子正面火拼。
                    MarchHorde(ref h, speed * step, _queueRank[i]);
                    Run.horde[i] = h;
                    if (h.hp <= 0)
                    {
                        KillHorde(i);
                    }
                    else if (h.arrivedAt > 0 && Run.seconds - h.arrivedAt >= V("GateWait") && Run.gateNext[h.gate] <= Run.seconds && Run.gateWarnings[h.gate] == 0)
                    {
                        Run.gateWarnings[h.gate] = Run.seconds + V("WarningSeconds");
                    }
                    continue;
                }
                float distance = (h.gate == 0 ? -h.x : h.gate == 1 ? h.x - 8 : h.gate == 2 ? -h.y : h.y - 8);
                // 同一行按到达先后排队：第 0 名贴着棋盘边，后面的每名退后一个层距；
                // 前排被打死，后排的名次跟着变小，自然往前补位，看起来就是一波接一波往里涌。
                float queueDistance = 0.6f + Mathf.Min(_queueRank[i], N("GateQueueRows") - 1) * V("GateQueueSpacing");
                if (distance > queueDistance)
                {
                    if (h.gate == 0)
                    {
                        h.x += speed * step;
                    }
                    else if (h.gate == 1)
                    {
                        h.x -= speed * step;
                    }
                    else if (h.gate == 2)
                    {
                        h.y += speed * step;
                    }
                    else
                    {
                        h.y -= speed * step;
                    }
                }
                else if (h.arrivedAt == 0)
                {
                    h.arrivedAt = Run.seconds;
                }
                if (h.arrivedAt > 0 && Run.seconds - h.arrivedAt >= V("GateWait") && Run.gateNext[h.gate] <= Run.seconds && Run.gateWarnings[h.gate] == 0)
                {
                    Run.gateWarnings[h.gate] = Run.seconds + V("WarningSeconds");
                }
                Run.horde[i] = h;
                if (h.hp <= 0)
                {
                    KillHorde(i);
                }
                else if (h.kind > 0 && h.arrivedAt > 0 && Run.seconds - h.arrivedAt >= V("WarningSeconds"))
                {
                    TryEnterBoard(i);
                }
            }
            for (int gate = 0; gate < Run.gateWarnings.Length; gate++)
            {
                if (Run.gateWarnings[gate] > 0 && Run.gateWarnings[gate] <= Run.seconds)
                {
                    bool alive = false;
                    for (int i = 0; i < Run.horde.Length; i++)
                    {
                        alive |= Run.horde[i].active && Run.horde[i].gate == gate && Run.horde[i].arrivedAt > 0;
                    }
                    if (alive)
                    {
                        if (Run.riskKind == 0 && Run.relicGate == gate)
                        {
                            Run.relicHp = Mathf.Max(0, Run.relicHp - V("HordeAttack"));
                        }
                        else
                        {
                            DamageHeart(V("HordeAttack"), "门点" + (gate + 1) + "突破投射");
                        }
                    }
                    Run.gateWarnings[gate] = 0;
                    Run.gateNext[gate] = Run.seconds + V("GateWait");
                }
            }
        }
        /// <summary>杂兵停在挡路棋子/棋心前多远（格）：略小于一格，看起来是贴脸肉搏而不是重叠。几何容差，不是数值。</summary>
        private const float ContactGap = .85f;
        /// <summary>
        /// 杂兵涌入（吸血鬼幸存者式“敌潮压上来”）：
        ///   1. 沿所在行朝棋盘中心前进，前方第一枚己方棋子就是“墙”，停在墙前 ContactGap 处；
        ///   2. 同一行的后来者按排队名次依次退后 GateQueueSpacing，前排倒下后排自动顶上；
        ///   3. 排头贴住棋子时，每 EnemyAttackSeconds 秒对它造成一次 HordeAttack 伤害（随巡回成长，同棋心伤害口径）；
        ///   4. 整行没有棋子挡（或挡在前面的就是棋心）时，排头冲到中心列算“突破”，沿用原有的门点预警 + 打棋心规则。
        /// 杂兵不占格、不参与围棋连通块，所以能穿过盘内的敌方入侵者；只有己方棋子能挡住它们——这正是布阵的意义。
        /// </summary>
        private void MarchHorde(ref EndlessHorde h, float move, int rank)
        {
            int row = Mathf.Clamp(Mathf.RoundToInt(h.y), 0, 8);
            float dir = h.gate == 0 ? 1 : -1;
            Piece blocker = null;
            // 默认目标：中心列（第 4 列）前方，即突破点。
            float front = 4 - dir * ContactGap;
            for (int step = 0; step < 9; step++)
            {
                int cx = h.gate == 0 ? step : 8 - step;
                // 只看还在自己前方的格子：已经被甩在身后的棋子挡不住它。
                if ((cx - h.x) * dir < -.5f)
                {
                    continue;
                }
                if (row == 4 && cx == 4)
                {
                    break;
                }
                Piece piece = Board.GetPiece(new BoardPos(cx, row));
                if (piece != null && piece.Side == GameSide.Player && piece.IsAlive)
                {
                    blocker = piece;
                    front = cx - dir * ContactGap;
                    break;
                }
            }
            float stop = front - dir * rank * V("GateQueueSpacing");
            float remaining = (stop - h.x) * dir;
            if (remaining > .01f)
            {
                h.x += dir * Mathf.Min(move, remaining);
                remaining = (stop - h.x) * dir;
            }
            bool engaged = rank == 0 && remaining <= .05f;
            h.arrivedAt = engaged && blocker == null ? (h.arrivedAt > 0 ? h.arrivedAt : Run.seconds) : 0;
            if (!engaged || blocker == null || h.attackAt > Run.seconds)
            {
                return;
            }
            h.attackAt = Run.seconds + V("EnemyAttackSeconds") / (h.slowUntil > Run.seconds ? 1 - V("SlowRatio") : 1);
            EndlessUnit victim = Find(blocker.Id);
            HurtPlayer(victim, V("HordeAttack") * HordeAttackScale(), null);
            // 敌方斩击回执：表现层用六章近战斩击画出“杂兵砍棋子”。
            ImpactFx(new Vector2(h.x, h.y), Position(blocker), 6, 0, 0, PieceKind.Swordman, true, 0);
        }
        /// <summary>杂兵攻击随巡回与难度的成长倍率，与 DamageHeart 同一口径，保证打棋子和打棋心一样随难度变强。</summary>
        private float HordeAttackScale()
        {
            float scale = 1 + Math.Min(Run.circuit - 1, N("CircuitAttackCap")) * V("CircuitAttackGrowth");
            return scale * (Run.difficulty == 1 ? V("DifficultyAttack") : 1);
        }
        private bool SpawnInvader(bool elite, int boss)
        {
            return QueueInvader(elite, boss);
        }
        private void DamageHeart(float amount, string source)
        {
            float scale = 1 + Math.Min(Run.circuit - 1, N("CircuitAttackCap")) * V("CircuitAttackGrowth");
            scale *= Run.difficulty == 1 ? V("DifficultyAttack") : 1;
            if (Run.heartShieldUntil > Run.seconds)
            {
                return;
            }
            if (Run.breakthroughs.Contains(2) && amount * scale >= V("HeartShieldTrigger") && Run.heartShieldReady <= Run.seconds)
            {
                Run.heartShieldUntil = Run.seconds + V("HeartShieldSeconds");
                Run.heartShieldReady = Run.seconds + V("MinuteSeconds");
            }
            Run.heartDamageEvents++;
            Run.heart = Mathf.Max(0, Run.heart - amount * scale);
            Run.lastDamage = source;
            Impact(new Vector2(4, 9), new Vector2(4, 4), 6, 1);
        }
    }
}
