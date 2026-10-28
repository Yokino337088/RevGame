#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
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
        /// <summary>隔离内存输入，只用正式目标算法/固定步/伤害管线；不写当前战役和存档。</summary>
        public static void VerifyStormRules(Action<bool, string> check)
        {
            var battle = new BattleService(false, 1028);
            object owner = new object();
            int missiles = 0, jelly = 0, beams = 0, missileTarget = 0;
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, info =>
            {
                if (!info.Storm) { return; }
                if (info.Weapon == HelperWeapon.Missile) { missiles++; missileTarget = info.TargetId; }
                if (info.Weapon == HelperWeapon.Jellyfish) { jelly += info.Projectiles; }
                if (info.Beam) { beams++; }
                check(info.Cost == 0, "风暴事件不冒充能源扣款");
            }, owner: owner);
            try
            {
                battle.Chapter = battle.Round = 1; battle.Phase = GamePhase.Battle;
                Piece p1 = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
                Piece p2 = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Crossbow, new BoardPos(2, 0));
                Piece p3 = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(4, 0));
                Piece e1 = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(8, 8));
                Piece e2 = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Crossbow, new BoardPos(6, 8));
                Piece e3 = battle.AddHelperCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(4, 8));
                // 显式给定同一合法候选集，交换扫描顺序，验证集火不由各自位置/列表顺序决定。
                var targets = new List<BoardPos> { p3.Pos, p2.Pos, p1.Pos };
                Piece chosen = EnemyTactics.ChooseTarget(battle.Board, e1, targets);
                targets.Reverse();
                check(chosen.Def.IsRanged && EnemyTactics.ChooseTarget(battle.Board, e2, targets) == chosen, "不同远程统一集火同一玩家远程");
                targets.Clear(); targets.Add(p3.Pos);
                check(EnemyTactics.ChooseTarget(battle.Board, e1, targets) == p3, "射界中没有远程时回退合法近战目标");
                targets.Clear();
                check(EnemyTactics.ChooseTarget(battle.Board, e1, targets) == null, "空射界不越界攻击远程");
                battle.RefreshGoState(); battle.ResetHelperRound();
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(!battle.HelperStormActive && missiles == 0, "三枚玩家棋不开风暴");
                battle.RemovePlayerPiece(p3.Id);
                int hp = e1.Hp + e2.Hp + e3.Hp;
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle.HelperStormActive && battle.Helper.State == "Storm", "三减二触发正式FSM风暴状态");
                check(missiles == 1 && (missileTarget == e1.Id || missileTarget == e2.Id), "无能源仍发一枚优先远程的导弹");
                check(jelly == Balance.HelperStormJellyCount && beams == 1, "密集水母数量及宽激光事件符合配置");
                check(e1.Hp + e2.Hp + e3.Hp < hp && battle.CombatScore == 0, "真实扣血且应急火力不自充能源");
                check(StormBeamTouches(new BoardPos(0,8),new BoardPos(8,8),e1,Balance.HelperStormBeamWidth)
                    && StormBeamTouches(new BoardPos(0,8),new BoardPos(8,8),e2,Balance.HelperStormBeamWidth)
                    && !StormBeamTouches(new BoardPos(0,0),new BoardPos(8,0),e2,Balance.HelperStormBeamWidth), "宽激光穿透同线多敌且不命中线外棋");
                // 禁用另外两种武器的冷却仅用于隔离激光数值，不能把视觉事件当成多目标真实伤害。
                battle._helperPosition = new BoardPos(0,8); battle._helperNextMove = float.MaxValue;
                battle._stormMissileAt = battle._stormJellyAt = float.MaxValue;
                int h1=e1.Hp,h2=e2.Hp;
                for (int i=0;i<8;i++) { battle._battleElapsed += Balance.BattleSimulationStepSeconds; battle.TickHelper(Balance.BattleSimulationStepSeconds); }
                check(e1.Hp < h1 && e2.Hp < h2, "连续真实固定步激光同时扣除两颗敌棋生命");
                p3 = battle.AddHelperCheckPiece(GameSide.Player,PieceKind.Shield,new BoardPos(4,0));
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(!battle.HelperStormActive && battle._stormBeamUntil == 0, "补回三颗退出风暴并停止免费激光");
                battle.RemovePlayerPiece(p3.Id); battle.RemovePlayerPiece(p2.Id);
                battle.TickHelper(Balance.BattleSimulationStepSeconds);
                check(battle.HelperStormActive, "只剩一颗仍提供救场支援");
                battle.RemovePlayerPiece(p1.Id); battle.UpdateHelperStorm();
                check(!battle.HelperStormActive && battle.LastWinner == GameSide.Enemy, "全灭仍失败，帮手不能改写胜负");
                battle.ResetHelperRound();
                check(!battle.HelperStormActive && battle._stormTargets.Count == 0, "重置清状态及目标缓存");
            }
            finally { battle.StopEnemyAI(); RevEvent.RemoveAllByOwner(owner); }
        }
    }
}
#endif
