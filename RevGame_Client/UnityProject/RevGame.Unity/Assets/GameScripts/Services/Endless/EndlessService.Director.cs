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
                int directions = Run.seconds < V("EventSeconds") ? 2 : Run.seconds < V("BossInterval") ? 3 : 4;
                int gate = (Next(directions) + Run.circuit - 1) % 4;
                if (Run.affixes.Contains(0))
                {
                    gate = (int)(Run.seconds / V("GateWait")) % 2;
                }
                // 第一波集中到可读的中路三条射线；后续波逐步开放全边，给新局及时的经验反馈。
                float along = Run.seconds < V("MinuteSeconds") ? 2 + Next(3) : Next(9);
                along += ((Run.nextId % 3) - 1) * V("HordeLaneJitter");
                float hp = V("HordeHp") * (1 + V("CircuitHpGrowth") * Math.Min(Run.circuit - 1, N("CircuitHpCap")));
                hp *= Run.difficulty == 1 ? V("DifficultyHp") : 1;
                Run.horde[i] = new EndlessHorde
                {
                    active = true, id = Run.nextId++, gate = gate, hp = hp, maxHp = hp,
                    x = gate == 0 ? -3 : gate == 1 ? 11 : along,
                    y = gate == 2 ? -3 : gate == 3 ? 11 : along,
                    speed = V("HordeSpeed") * Mathf.Min(V("CircuitSpeedCap"), 1 + (Run.circuit - 1) * V("CircuitAttackGrowth"))
                };
                return true;
            }
            return false;
        }
        private void UpdateHorde(float step)
        {
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (!Run.horde[i].active)
                {
                    continue;
                }
                EndlessHorde h = Run.horde[i];
                if (h.burnUntil > Run.seconds && h.burnAt <= Run.seconds)
                {
                    h.hp -= h.burnDamage;
                    h.burnAt = Run.seconds + 1;
                }
                float speed = h.frozenUntil > Run.seconds ? 0 : h.speed * (h.slowUntil > Run.seconds ? 1 - V("SlowRatio") : 1);
                float distance = (h.gate == 0 ? -h.x : h.gate == 1 ? h.x - 8 : h.gate == 2 ? -h.y : h.y - 8);
                float queueDistance = 0.6f + (h.id % N("GateQueueRows")) * V("GateQueueSpacing");
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
