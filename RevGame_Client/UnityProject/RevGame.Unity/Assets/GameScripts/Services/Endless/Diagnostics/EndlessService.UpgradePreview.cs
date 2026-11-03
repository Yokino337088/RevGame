#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>仅开发环境的无尽升级/耐久夹具，使用隔离状态和真实服务入口，不访问玩家存档。</summary>
    public sealed partial class EndlessService
    {
        /// <summary>
        /// 可复现拖牌美术夹具：仅隔离局开放，经既有出生入口建立两枚同名和法师/刺客。
        /// 两枚弓手都未满星，手牌第一槽固定同名；测试仍走EventSystem拖曳与正式服务提交。
        /// 清局只在显式测试动作发生，不修改玩家存档，rev_run finally负责返回大厅。
        /// </summary>
        public object PreviewUpgradeDrag()
        {
            if (!Isolated || Run == null)
            {
                throw new InvalidOperationException("升级拖曳夹具只允许隔离无尽局");
            }
            foreach (EndlessUnit actor in Units)
            {
                if (!actor.Outside)
                {
                    Board.Remove(actor.Piece);
                }
            }
            Units.Clear();
            Horde.Clear();
            EndlessUnit first = AddPlayer(PieceKind.Archer, 1, 1, false);
            EndlessUnit second = AddPlayer(PieceKind.Archer, 7, 1, false);
            AddPlayer(PieceKind.MageFire, 3, 3, false);
            AddPlayer(PieceKind.Shield, 3, 2, false);
            AddPlayer(PieceKind.Assassin, 2, 5, false);
            Run.hand.Clear();
            Run.hand.Add((int)PieceKind.Archer);
            Run.hand.Add((int)PieceKind.MageFire);
            Run.hand.Add((int)PieceKind.Archer);
            Run.selectedCard = Run.selectedUnit = -1;
            Run.phase = EndlessPhase.Formation;
            RefreshRules();
            return new Dictionary<string, object> { ["firstId"] = first.Piece.Id, ["secondId"] = second.Piece.Id, ["targetX"] = 1, ["targetY"] = 1, ["hand"] = Run.hand.Count };
        }

        /// <summary>固定小伤害独立核对Boss配置和防御，避免每秒总预算截断掩盖是否真的应用防御。</summary>
        public object CheckBossDurability()
        {
            var passed = new List<string>();
            using (EndlessService s = new EndlessService())
            {
                s.Begin(0, 0, 1103, true);
                Check(Mathf.Approximately(V("BossHpScale"), 18f) && Mathf.Approximately(V("BossDefenseScale"), 1.5f), "无尽Boss生命12→18与防御1→1.5均来自导表", passed);
                Check(s.SpawnOutsideBoss(0), "固定局沿真实出生口生成领主", passed);
                EndlessUnit boss = s.BossUnit();
                s.Run.seconds = boss.BornAt + V("BossEntranceSeconds") + V("SimulationStep");
                int hp = boss.Piece.Hp;
                // 首波领主每步总预算低于20；用3点小伤害隔离防御，不把预算截断误判为防御失效。
                int damage = s.ApplyUnitDamage(boss, 3f);
                Check(damage == 2 && hp - boss.Piece.Hp == 2, "3点伤害先经1.5防御变2再记录实际扣血", passed);
            }
            return new Dictionary<string, object> { ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed };
        }
    }
}
#endif
