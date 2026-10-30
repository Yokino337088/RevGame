// 固定输入验证最新眼位扶持规则；调用正式伤害、回血与计时入口，不靠改旗标伪造效果。
#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        private static void VerifyEyeBenefits(Action<bool, string> check)
        {
            var service = EyeFixture();
            object owner = new object();
            try
            {
                Piece guard = service.AddEyeGuards(GameSide.Player, false);
                Piece diagonal = service.Board.GetPiece(new BoardPos(1, 1));
                Piece enemy = service.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 8));
                check(guard.HasEyeGuardAura && guard.EyeGuardDefenseMultiplier == 1.5f, "空眼成型即给四邻50%防御");
                check(!diagonal.HasEyeGuardAura && !enemy.HasEyeGuardAura, "斜邻、远处、敌人不领取四邻光环");
                int hp = guard.Hp;
                service.DamagePiece(guard, 150);
                check(hp - guard.Hp == 100, "四邻50%防御走真实伤害出口，150伤害折为100");
                guard.TakeDamage(guard.MaxHp / 2);
                diagonal.TakeDamage(diagonal.MaxHp / 2);
                int wounded = guard.Hp, diagonalHp = diagonal.Hp;
                service.TickEyeHealing(0.99f);
                check(guard.Hp == wounded, "不足一秒不提前回血");
                service.TickEyeHealing(0.02f);
                check(guard.Hp - wounded == (int)(guard.MaxHp * Balance.EyeHealRatio), "每秒按最大生命回复3%");
                check(diagonal.Hp == diagonalHp, "连在同一棋群但不紧邻眼的棋不回血");
                int notices = 0, deaths = 0;
                RevEvent.AddEventListener<string>(Events.BattleEventTriggered, title =>
                {
                    if (title == "眼位成型")
                    {
                        notices++;
                    }
                }, owner: owner);
                RevEvent.AddEventListener<int>(Events.PieceDied, id => { deaths++; }, owner: owner);
                Piece garrison = service.AddImpactPiece(GameSide.Player, PieceKind.Crossbow, new BoardPos(0, 0));
                service.RefreshGoState();
                service.RefreshGoState();
                check(notices == 1, "真实驻眼仅发布一次眼位成型，重复刷新不刷屏");
                int beforeHeal = guard.Hp;
                service.DamagePiece(garrison, int.MaxValue);
                check(garrison.IsAlive && garrison.Hp == garrison.MaxHp && garrison.EyeReviveUsed && service.EyeReviveCount == 1, "有效驻眼致死后仅消耗一次资格并原地满血复活");
                check(service.Board.FindPiece(garrison.Id) == garrison && garrison.Pos == new BoardPos(0, 0) && deaths == 0, "复活保留占格、ID与视图，不广播死亡");
                check(guard.Hp == Math.Min(guard.MaxHp, beforeHeal + (int)(guard.MaxHp * Balance.EyeReviveHealRatio)), "复活四邻回复配置比例最大生命");
                check(diagonal.Hp == diagonalHp, "复活不治疗斜邻");
                check(service.Board.CreateSnapshot().FindPiece(garrison.Id).EyeReviveUsed, "快照保留已消费资格，试算不会补回次数");
                service.Board.Move(garrison, new BoardPos(4, 4));
                service.RefreshGoState();
                service.Board.Move(garrison, new BoardPos(0, 0));
                service.RefreshGoState();
                check(garrison.EyeReviveUsed, "进出眼位不重置复活资格");
                service.RebuildBoardFromRoster();
                service.RefreshGoState();
                check(garrison.EyeReviveUsed, "换轮重建棋盘不重置复活资格");
                service.DamagePiece(garrison, int.MaxValue);
                check(!garrison.IsAlive && service.Board.FindPiece(garrison.Id) == null && deaths == 1 && service.EyeReviveCount == 1, "第二次致死真实移除，只广播一次死亡");
                service.DamagePiece(diagonal, int.MaxValue);
                // 对角连接子死亡不破角眼；真正移走上下左右才撤增益。
                check(guard.HasEyeGuardAura, "非四邻死亡不误撤眼光环");
                Piece otherGuard = service.Board.GetPiece(new BoardPos(0, 1));
                service.DamagePiece(otherGuard, int.MaxValue);
                check(!guard.HasEyeGuardAura && guard.EyeGuardDefenseMultiplier == 1f, "真实破眼同一步撤50%防御");
                hp = guard.Hp;
                service.TickEyeHealing(1f);
                check(guard.Hp == hp, "破眼之后不再回血");
            }
            finally { RevEvent.RemoveAllByOwner(owner); service.StopEnemyAI(); }

            // 同一守子同时挨着两个眼，必须仍然只有一份防御和治疗。
            service = EyeFixture();
            try
            {
                foreach (BoardPos pos in new[] { new BoardPos(0, 1), new BoardPos(1, 0), new BoardPos(1, 1), new BoardPos(2, 1), new BoardPos(3, 0) })
                {
                    service.AddImpactPiece(GameSide.Player, PieceKind.Shield, pos);
                }
                service.RefreshGoState();
                Piece shared = service.Board.GetPiece(new BoardPos(1, 0));
                check(service.Board.GetCell(new BoardPos(0, 0)).IsEyeMarked && service.Board.GetCell(new BoardPos(2, 0)).IsEyeMarked, "双眼共享守子夹具有效");
                shared.TakeDamage(shared.MaxHp / 2);
                int hp = shared.Hp;
                service.TickEyeHealing(1f);
                check(shared.EyeGuardDefenseMultiplier == 1.5f && shared.Hp - hp == (int)(shared.MaxHp * Balance.EyeHealRatio), "两个眼不叠加防御与每秒治疗");
            }
            finally { service.StopEnemyAI(); }
        }
        private static void VerifyEyePacing(Action<bool, string> check)
        {
            var service = EyeFixture();
            try
            {
                service.ResetCombatPacing();
                service.AddEyeGuards(GameSide.Player, false);
                service.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 8));
                service._battleElapsed = Balance.EyeSupportSeconds - 0.01f;
                service.TickEyeSupport();
                check(service.EyeSupportCount == 0, "眼位支援未到配置间隔不发生");
                service._battleElapsed = Balance.EyeSupportSeconds;
                service.TickEyeSupport();
                Piece ranged = service.Board.GetPiece(new BoardPos(0, 0));
                check(service.EyeSupportCount == 1 && ranged != null && ranged.Def.IsRanged && ranged.IsEyeGarrison, "配置间隔窗口真实补入驻眼远程且领取增益");
                service._battleElapsed = 2f * Balance.EyeSupportSeconds;
                service.TickEyeSupport();
                check(service.EyeSupportCount == 1, "眼位已满不在普通格强塞援军");
                service.DamagePiece(ranged, int.MaxValue);
                service.DamagePiece(ranged, int.MaxValue);
                service._battleElapsed = 3f * Balance.EyeSupportSeconds - 0.01f;
                service.TickEyeSupport();
                check(service.EyeSupportCount == 1, "空眼出现仍遵守下个支援窗口");
                service._battleElapsed = 3f * Balance.EyeSupportSeconds;
                service.TickEyeSupport();
                check(service.EyeSupportCount == 2 && service.Board.GetPiece(new BoardPos(0, 0)) != ranged, "第三个窗口补回阵亡后空出的眼位");
                service.Phase = GamePhase.Settle;
                service._battleElapsed = 4f * Balance.EyeSupportSeconds;
                service.TickEyeSupport();
                check(service.EyeSupportCount == 2, "结算不补迟到支援");
            }
            finally { service.StopEnemyAI(); }

            service = EyeFixture();
            try
            {
                service.ResetCombatPacing();
                Piece player = service.AddImpactPiece(GameSide.Player, PieceKind.Shield, new BoardPos(4, 4));
                Piece enemy = service.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(4, 5));
                service.RefreshGoState();
                service._battleElapsed = Balance.RegionIntervalSeconds - 0.01f;
                service.TickCombatPacing();
                check(service.RegionPulseCount == 0, "区域波动未到配置间隔不触发");
                service._battleElapsed = Balance.RegionIntervalSeconds;
                service.TickCombatPacing();
                check(service.RegionPulseCount == 1, "区域波动配置间隔触发");
                int hp = enemy.Hp, friendlyHp = player.Hp;
                service._battleElapsed = Balance.RegionIntervalSeconds + Balance.RegionWarningSeconds + Balance.RegionSize * Balance.RegionSize * Balance.RegionCellDelaySeconds;
                service.TickCombatPacing();
                check(hp - enemy.Hp == (int)(enemy.MaxHp * Balance.RegionDamageHpRatio) && player.Hp == friendlyHp, "区域波动仅扣敌方配置比例最大生命，不伤友军");
                service._nextRegionAt = 100f;
                service._battleElapsed = Balance.BombIntervalSeconds - 0.01f;
                service.TickCombatPacing();
                check(!service._bombActive, "能量巨大未到配置间隔不触发");
                service._battleElapsed = Balance.BombIntervalSeconds;
                service.TickCombatPacing();
                check(service._bombActive, "能量巨大配置间隔触发");
                hp = enemy.Hp;
                service._battleElapsed = Balance.BombIntervalSeconds + Balance.BombWarningSeconds;
                service.TickCombatPacing();
                check(hp - enemy.Hp == (int)(enemy.MaxHp * Balance.BombDamageHpRatio) && player.Hp == friendlyHp && service.EnergyBombCount == 1, "能量巨大直接扣敌方配置比例最大生命且只命中一次");
            }
            finally { service.StopEnemyAI(); }
        }
    }
}
#endif
