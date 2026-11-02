#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// ============================================================
// EndlessService.SnowballChecks.cs —— 第四轮规则夹具（快节奏敌潮 / 波次成长 / 领主来袭 / 领主奖励滚雪球）
// 只在隔离实例里建立前置条件，断言全部读真实业务结果（Excel 数值 + 真实公式），不碰玩家存档。
// ============================================================
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
    public sealed partial class EndlessService
    {
        public static object CheckSnowballRules()
        {
            var passed = new List<string>();
            string previousSave = _isolatedSave;
            try
            {
                using (var s = new EndlessService())
                {
                    s.Begin(1, 0, 2202, true);
                    s.StartWave();
                    Check(s.Run.horde.Length == N("HordeLimit") && N("HordeLimit") >= 240, "外围并发上限按表加倍到240", passed);

                    // 波次：第 4 波（已过 3 波）的杂兵血量 = 基础 × (1 + 3 × WaveHpGrowth)，攻击同理。
                    s.Run.seconds = V("MinuteSeconds") * 3 + 1;
                    Check(s.WaveNumber == 4, "波次按MinuteSeconds计算（第4波）", passed);
                    int slot = FreeSlot(s);
                    Check(s.SpawnHorde(), "可以刷出外围杂兵", passed);
                    float expectedHp = V("HordeHp") * (1 + 3 * V("WaveHpGrowth"));
                    Check(Mathf.Abs(s.Run.horde[slot].hp - expectedHp) < .01f, "敌人血量随波次成长", passed);
                    Check(Mathf.Abs(s.HordeAttackScale() - (1 + 3 * V("WaveAttackGrowth"))) < .0001f, "敌人攻击随波次成长", passed);
                    s.Run.horde[slot] = default(EndlessHorde);

                    // 快节奏敌潮：出生期限积压时，一步内连刷（期限追上“现在”后再补一只），不会一口气补刷一大坨。
                    s.Run.nextInvader = s.Run.nextElite = s.Run.nextBoss = float.MaxValue;
                    // 一步 0.5 秒、期限落后 0.2 秒：按 0.14 秒间隔应在本步刷出 0.2 / 0.14 → 2 只。
                    float interval = V("SpawnInterval") / s.Run.adaptiveBudget;
                    s.Run.nextSpawn = s.Run.seconds - interval * 1.4f;
                    int hordeBefore = s.HordeCount();
                    s.UpdateDirector(.5f);
                    int spawned = s.HordeCount() - hordeBefore;
                    Check(spawned == 2, "出生间隔短于步长时一步内按间隔连刷（本次 " + spawned + " 只）", passed);
                    // 期限落后很久：积压只算一个步长，且每步最多 MaxSpawnsPerStep 只。
                    s.Run.nextSpawn = s.Run.seconds - 5;
                    hordeBefore = s.HordeCount();
                    s.UpdateDirector(.5f);
                    spawned = s.HordeCount() - hordeBefore;
                    Check(spawned == MaxSpawnsPerStep, "积压很久也最多每步刷 " + MaxSpawnsPerStep + " 只（本次 " + spawned + " 只）", passed);
                    for (int i = 0; i < s.Run.horde.Length; i++)
                    {
                        s.Run.horde[i] = default(EndlessHorde);
                    }

                    // 领主来袭：每 BossInterval（10 波）刷一只领主，并推一条大横幅。
                    Check(Mathf.Abs(V("BossInterval") / V("MinuteSeconds") - 10) < .001f, "每10波一次领主战", passed);
                    s.Run.seconds = V("BossInterval");
                    s.Run.nextSpawn = float.MaxValue;
                    s.Run.nextBoss = s.Run.seconds;
                    int banner = s.MajorBannerSerial;
                    s.UpdateDirector(.1f);
                    Check(s.HasBossIdentity() && s.Run.nextBoss > s.Run.seconds, "到点刷出领主", passed);
                    Check(s.MajorBannerSerial > banner && s.MajorBanner.Contains("领主来袭"), "领主来袭推大横幅", passed);
                    for (int i = 0; i < s.Run.horde.Length; i++)
                    {
                        s.Run.horde[i] = default(EndlessHorde);
                    }

                    // 领主奖励：击杀领主（RewardEnemy 角色 ≥ 6）随机获得 EndlessBoon 表里的一条增益，推彩色大横幅。
                    banner = s.MajorBannerSerial;
                    float sum = BoonSum(s.Run), heart = s.Run.heart = V("HeartHp") * .5f;
                    s.RewardEnemy(6);
                    Check(s.Run.bossKills == 1 && s.BoonCount == 1, "击杀领主获得一次增益", passed);
                    Check(BoonSum(s.Run) > sum || s.Run.heart > heart, "增益确实累加到本局（伤害/攻速/生命/范围/组合技/棋心之一）", passed);
                    Check(s.MajorBannerSerial > banner && s.MajorBanner.Contains("领主击破") && s.MajorBannerColor.Length > 0, "领主奖励推带颜色的大横幅（独立通道，不被同步升级的小横幅覆盖）", passed);

                    // 滚雪球：再连杀 9 只领主，断言累加只增不减、次数正确（具体抽到哪条由存档随机源决定）。
                    for (int i = 0; i < 9; i++)
                    {
                        float previous = BoonSum(s.Run);
                        s.RewardEnemy(6);
                        Check(BoonSum(s.Run) >= previous, "增益只增不减（第" + (i + 2) + "次）", passed);
                    }
                    Check(s.BoonCount == 10, "十次领主奖励全部计入", passed);

                    // 增益落到真实公式：伤害倍率、远程攻速、剑气/盾击范围、新上场棋子的生命上限。
                    EndlessRun run = s.Run;
                    run.boonAttack = 1;
                    run.boonSpeed = 1;
                    run.boonAura = .5f;
                    run.boonHealth = 1;
                    Check(Mathf.Abs(s.BoonDamage() - 2) < .0001f, "伤害增益=1时己方伤害×2", passed);
                    EndlessUnit archer = null, shield = null, sword = null;
                    foreach (EndlessUnit u in s.Units)
                    {
                        if (u.Piece.Side != GameSide.Player || u.Stone)
                        {
                            continue;
                        }
                        if (u.Piece.Def.Kind == PieceKind.Archer)
                        {
                            archer = u;
                        }
                        else if (u.Piece.Def.Kind == PieceKind.Shield)
                        {
                            shield = u;
                        }
                        else if (IsSwordAuraKind(u.Piece.Def.Kind))
                        {
                            sword = u;
                        }
                    }
                    float baseSpeed = Mathf.Min(V("AttackSpeedCap"), archer.Piece.EyeSpeedMultiplier * (archer.FuryUntil > run.seconds ? 1 + V("BrokenEyeSpeed") : 1)) * (1 + V("RangedAttackSpeedBonus"));
                    Check(Mathf.Abs(s.PlayerAttackSpeed(archer) - baseSpeed * 2) < .001f, "攻速增益=1时攻速×2（乘在上限之后）", passed);
                    Check(Mathf.Abs(s.ShieldReach(shield.Piece) - (N("ShieldZoneSize") * .5f + (shield.Piece.Size - 1) * .5f) * 1.5f) < .001f, "范围增益放大盾击范围", passed);
                    if (sword != null)
                    {
                        float auraBase = V("AuraRadius") * (1 + ((int)sword.Piece.Def.Star - 1) * V("AuraStarRadius")) + (sword.Piece.Size - 1) * .5f;
                        Check(Mathf.Abs(s.AuraRadius(sword.Piece) - auraBase * 1.5f) < .001f, "范围增益放大剑气半径", passed);
                    }
                    BoardPos cell = FreeCell(s);
                    EndlessUnit fresh = s.AddPlayer(PieceKind.Swordman, cell.X, cell.Y, false);
                    Check(fresh.Piece.MaxHp == Mathf.Max(1, (int)(PieceCatalog.Get(PieceKind.Swordman, StarTier.One).MaxHp * s.PlayerHealthScale())), "生命增益对新上场棋子同样生效", passed);
                }
                return new Dictionary<string, object>
                {
                    ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed,
                    ["scope"] = "独立规则夹具：外围上限、波次血攻成长、一步连刷与上限、10波领主与来袭横幅、领主奖励随机/累加/彩色横幅、增益接入伤害攻速范围生命"
                };
            }
            finally
            {
                _isolatedSave = previousSave;
            }
        }
        /// <summary>
        /// 隔离测试局：直接走一次“击杀领主”的真实奖励流程（RewardEnemy 角色 6：计数、经验、积分、随机增益、大横幅），
        /// 用来在画面上验收大横幅动画，不必先苦战 200 秒。正式局拒绝。
        /// </summary>
        public bool DebugBossReward()
        {
            if (Run == null || !Isolated)
            {
                return Reject("只有隔离测试局可以直接发放领主奖励");
            }
            RewardEnemy(6);
            return true;
        }
        private static int FreeSlot(EndlessService s)
        {
            for (int i = 0; i < s.Run.horde.Length; i++)
            {
                if (!s.Run.horde[i].active)
                {
                    return i;
                }
            }
            return -1;
        }
        private static BoardPos FreeCell(EndlessService s)
        {
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (s.Fits(pos, 1, GameSide.Player))
                    {
                        return pos;
                    }
                }
            }
            throw new InvalidOperationException("夹具找不到空格");
        }
        private static float BoonSum(EndlessRun run)
        {
            return run.boonAttack + run.boonSpeed + run.boonHealth + run.boonAura + run.boonCombo;
        }
    }
}
#endif
