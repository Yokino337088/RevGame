#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
// ============================================================
// EndlessService.GuardChecks.cs —— 盾战/刺客/远程/交互规则夹具（3×3防御区与盾击 / 刺客突刺 / 远程连发 / 不停顿 / 智能驻守 / 选牌后点棋）
// 只在隔离实例里建立前置条件，断言全部读真实业务结果；不碰玩家存档、不代表正常难度通关。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        public static object CheckGuardRules()
        {
            var passed = new List<string>();
            string previousSave = _isolatedSave;
            try
            {
                using (var s = new EndlessService())
                {
                    // 炎环棋阵：盾战在 (1,4)，以自身为中心的 3×3 防御区应为 x0~2、y3~5 共 9 格。
                    s.Begin(1, 0, 1101, true);
                    s.RefreshShieldZones();
                    Check(ZoneCells(s) == 9, "单个盾战防御区为以自身为中心的3×3共9格", passed);
                    Check(s.ShieldZone[4 * 9 + 1] && s.ShieldZone[3 * 9 + 0] && s.ShieldZone[5 * 9 + 2] && !s.ShieldZone[4 * 9 + 3], "防御区以盾战为中心（四角在区内、外一格不在）", passed);

                    // 盾击：3×3 范围（半宽 1.5 格）内的外围敌人受伤，范围外的不受伤。
                    EndlessUnit shield = UnitAt(s, 1, 4);
                    PutHorde(s, 0, 2.2f, 4.6f);
                    PutHorde(s, 1, 3.2f, 4);
                    shield.AuraAt = 0;
                    s.UpdateShieldBash(shield);
                    Check(s.Horde[0].hp < 1000 && s.Horde[1].hp == 1000, "盾战盾击打到自身3×3范围内的敌人，范围外不受伤", passed);
                    Check(shield.AuraAt > s.Run.seconds, "盾击按AuraTickSeconds间隔冷却", passed);
                    s.Horde[0] = s.Horde[1] = default(EndlessHorde);

                    // 第二个盾战 (2,5)：区为 x1~3、y4~6，与第一块重叠 2×2=4 格，并集应为 9+9-4=14。
                    s.AddPlayer(PieceKind.Shield, 2, 5, false);
                    s.RefreshShieldZones();
                    Check(ZoneCells(s) == 14, "两块防御区按并集合并（重叠不重复计）", passed);

                    // 伤害减半：水法师 (3,5) 在区内，火法师 (5,5) 在区外；同一次 40 点伤害按同一公式比较。
                    EndlessUnit inside = UnitAt(s, 3, 5), outside = UnitAt(s, 5, 5);
                    Check(inside != null && outside != null && s.InShieldZone(inside.Piece) && !s.InShieldZone(outside.Piece), "区内/区外判定正确", passed);
                    int blocks = s.BlockSerial;
                    int insideHp = inside.Piece.Hp, outsideHp = outside.Piece.Hp;
                    s.HurtPlayer(inside, 40, null);
                    s.HurtPlayer(outside, 40, null);
                    int expectedInside = Mathf.CeilToInt(40 * Mathf.Max(1 - V("DamageReductionCap"), 1 / (inside.Piece.EyeDefenseMultiplier * inside.Piece.EyeGuardDefenseMultiplier)) / (1 + V("ShieldDefenseBonus")));
                    int expectedOutside = Mathf.CeilToInt(40 * Mathf.Max(1 - V("DamageReductionCap"), 1 / (outside.Piece.EyeDefenseMultiplier * outside.Piece.EyeGuardDefenseMultiplier)));
                    Check(insideHp - inside.Piece.Hp == expectedInside && outsideHp - outside.Piece.Hp == expectedOutside, "防御区内伤害按+100%防御减半，区外不变", passed);
                    Check(s.BlockSerial == blocks + 1, "区内受击写一条格挡回执，区外不写", passed);

                    // 刺客 (4,6) 向右突刺：x=5、x=7 两只在 4 格内被贯穿，x=9.5 超出射程不受伤。
                    EndlessUnit assassin = UnitAt(s, 4, 6);
                    Check(assassin != null && assassin.Piece.Def.Kind == PieceKind.Assassin, "炎环棋阵含刺客", passed);
                    PutHorde(s, 0, 5, 6);
                    PutHorde(s, 1, 7, 6);
                    PutHorde(s, 2, 9.5f, 6);
                    int impacts = s.ImpactSerial;
                    s.AssassinThrust(assassin);
                    Check(s.Horde[0].hp < 1000 && s.Horde[1].hp < 1000 && s.Horde[2].hp == 1000, "刺客直线贯穿4格内全部敌人，射程外不受伤", passed);
                    bool thrust = false;
                    for (int serial = impacts + 1; serial <= s.ImpactSerial; serial++)
                    {
                        thrust |= s.Impacts[serial % s.Impacts.Length].route == ThrustRoute;
                    }
                    Check(thrust, "刺客出手写突刺回执", passed);
                    for (int i = 0; i < 3; i++)
                    {
                        s.Horde[i] = default(EndlessHorde);
                    }

                    // 远程攻速：开战后第一步弓箭手出手，下一次出手间隔 = 基础间隔 ÷ (眼位攻速 × (1 + 远程加成))。
                    EndlessUnit archer = UnitAt(s, 5, 3);
                    Check(archer != null && archer.Piece.Def.IsRanged, "炎环棋阵含远程弓箭手", passed);
                    s.StartWave();
                    s.Tick(.1f);
                    float expected = V("PlayerAttackSeconds") / (Mathf.Min(V("AttackSpeedCap"), archer.Piece.EyeSpeedMultiplier) * (1 + V("RangedAttackSpeedBonus")));
                    Check(Mathf.Abs(V("PlayerAttackSeconds") / s.PlayerAttackSpeed(archer) - expected) < .0001f, "远程攻击间隔乘上RangedAttackSpeedBonus", passed);
                    Check(archer.AttackAt > s.Run.seconds - .001f && archer.AttackAt <= s.Run.seconds + expected + .001f, "开战第一步按间隔出手后，下次期限落在一个间隔之内", passed);
                    Check(expected < V("SimulationStep"), "远程出手间隔短于模拟步（需要一步内连发才能兑现攻速）", passed);

                    // 连发：弓箭手身边放一只敌人，出手期限积压很久；积压只算一个 0.1 秒步长，按约 0.033 秒间隔一步内打 3~4 发，不会喷出一大串。
                    PutHorde(s, 0, 5.6f, 3);
                    archer.AttackAt = s.Run.seconds - 5;
                    int before = s.ImpactSerial;
                    s.UpdateUnits(.1f);
                    int shots = 0;
                    for (int serial = before + 1; serial <= s.ImpactSerial; serial++)
                    {
                        EndlessImpact impact = s.Impacts[serial % s.Impacts.Length];
                        shots += impact.sourceId == archer.Piece.Id && impact.combo == 0 && impact.route != ComboRoute ? 1 : 0;
                    }
                    Check(shots >= 3 && shots <= MaxAttacksPerStep, "攻速高于模拟步时一步内连发且不超过每步上限（本次 " + shots + " 发）", passed);
                    Check(archer.AttackAt > s.Run.seconds - .001f && archer.AttackAt <= s.Run.seconds + expected + .001f, "连发后下次期限落在一个间隔之内", passed);
                    s.Horde[0] = default(EndlessHorde);

                    // 不停顿：分钟节点与补牌都不改变战斗阶段；手牌按间隔补一张。
                    s.Run.hand.Clear();
                    s.Run.nextCardAt = 0;
                    s.Run.nextMinute = s.Run.seconds + .05f;
                    s.Tick(.1f);
                    Check(s.Run.phase == EndlessPhase.Fighting && s.Run.hand.Count == 1, "分钟节点不进整备、战斗中自动补牌", passed);
                    Check(s.SelectCard(0), "战斗中可以选手牌", passed);

                    // 智能驻守：左下区堆大量敌人，重新规划后至少一枚近战被派往左下区（区号 0）。
                    for (int i = 0; i < 6; i++)
                    {
                        PutHorde(s, i, -1 - i * .3f, 1);
                    }
                    s._zonePlanAt = 0;
                    s.PlanZones();
                    bool guarded = false;
                    foreach (EndlessUnit u in s.Units)
                    {
                        guarded |= u.Zone == 0;
                    }
                    Check(guarded, "敌人最多的区优先分到近战驻守", passed);

                    // 手动保持：战斗中手动移动近战棋后，ManualHoldSeconds 内不被自动索敌拉走。
                    // 先取消上面选中的手牌，否则点己方棋子会走“手牌合星”分支。
                    s.Run.selectedCard = -1;
                    EndlessUnit swordman = null;
                    foreach (EndlessUnit u in s.Units)
                    {
                        if (u.Piece.Def.Kind == PieceKind.Swordman)
                        {
                            swordman = u;
                        }
                    }
                    Check(swordman != null && s.ClickCell(swordman.Piece.Pos.X, swordman.Piece.Pos.Y), "战斗中可以点选己方棋子", passed);
                    BoardPos target = FreeNeighbour(s, swordman.Piece);
                    Check(s.ClickCell(target.X, target.Y) && swordman.HoldUntil > s.Run.seconds, "手动移动后进入保持时间", passed);
                    BoardPos held = swordman.Piece.Pos;
                    s.MeleeAdvance(swordman);
                    Check(swordman.Piece.Pos.Equals(held), "保持时间内自动索敌不移动该棋", passed);

                    // 交互修复：先点手牌，再点一枚不能合星的己方棋子 → 放下手牌、改为选中这枚棋子，并能显示移动范围、真正移动。
                    s.Run.hand.Clear();
                    s.Run.hand.Add((int)PieceKind.Priest);
                    Check(s.SelectCard(0), "战斗中先选中一张手牌", passed);
                    int handBefore = s.Run.hand.Count;
                    Check(s.ClickCell(swordman.Piece.Pos.X, swordman.Piece.Pos.Y), "选牌状态下点己方棋子被接受", passed);
                    Check(s.Run.selectedCard == -1 && s.Run.selectedUnit == swordman.Piece.Id && s.Run.hand.Count == handBefore, "点棋后放下手牌、选中该棋子，手牌不被消耗", passed);
                    BoardPos step = FreeNeighbour(s, swordman.Piece);
                    Check(s.CanPlaceAt(step.X, step.Y), "选中后空格显示为可移动（绿色范围）", passed);
                    Check(s.ClickCell(step.X, step.Y) && swordman.Piece.Pos.Equals(step), "选牌后改点的棋子可以正常移动", passed);
                }
                return new Dictionary<string, object>
                {
                    ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed,
                    ["scope"] = "独立规则夹具：3×3防御区居中/并集/减伤/格挡回执、盾击范围与冷却、刺客射程与贯穿、远程攻速与一步连发、不停顿补牌、驻守分配与手动保持、选牌后点棋可移动"
                };
            }
            finally
            {
                _isolatedSave = previousSave;
            }
        }
        private static int ZoneCells(EndlessService s)
        {
            int count = 0;
            for (int i = 0; i < 81; i++)
            {
                count += s.ShieldZone[i] ? 1 : 0;
            }
            return count;
        }
        private static EndlessUnit UnitAt(EndlessService s, int x, int y)
        {
            Piece p = s.Board.GetPiece(new BoardPos(x, y));
            return p == null ? null : s.Find(p.Id);
        }
        /// <summary>在指定槽位放一只高血量外围敌人（夹具用，确保一击不死，好比较是否受伤）。</summary>
        private static void PutHorde(EndlessService s, int slot, float x, float y)
        {
            s.Horde[slot] = new EndlessHorde
            {
                active = true, id = s.Run.nextId++, gate = x < 4 ? 0 : 1, hp = 1000, maxHp = 1000, x = x, y = y, speed = 0
            };
        }
        private static BoardPos FreeNeighbour(EndlessService s, Piece p)
        {
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            for (int i = 0; i < 4; i++)
            {
                var pos = new BoardPos(p.Pos.X + dx[i], p.Pos.Y + dy[i]);
                if (pos.IsInside(9) && s.Fits(pos, p.Size, GameSide.Player, p))
                {
                    return pos;
                }
            }
            throw new InvalidOperationException("夹具找不到可移动的空格");
        }
    }
}
#endif
