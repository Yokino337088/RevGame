// 开发环境受控入口：AI与人工调用同一服务动作，阶段/范围拒绝规则不会绕过。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using RevGame.Data;
using RevGame.Services;
using Revolution;

namespace RevGame.Bootstrap
{
    public static class GameCombatGmCommands
    {
        private static BattleService Battle => (BattleService)RevServices.GetRequired<IBattleService>();
        private static readonly string[] Names = { "战斗节奏", "区域波动", "能量巨大", "战斗增益" };
        public static void Register()
        {
            RevGM.Register("RevGame/战斗节奏", "查询本轮攻击/防御/攻速/生命增益及波动/炸弹计数", args => Battle.CombatPacingStatus());
            RevGM.Register("RevGame/区域波动", "触发真实随机区域预警，不能重叠施放", args => Result(Battle.TriggerRegionPulse()));
            RevGM.Register("RevGame/能量巨大", "触发真实自动炸弹，等待蓄力后才对敌军扣血", args => Result(Battle.TriggerEnergyBomb()));
            RevGM.Register("RevGame/战斗增益", "本轮全队属性：Attack/Defense/Haste/Vitality；第二参数为额外百分比", args =>
            {
                if (args.Count != 2 || !Enum.TryParse(args.Str(0), out BattleEventKind kind)) { throw new RevGMUsageException("用法：RevGame/战斗增益 Attack|Defense|Haste|Vitality 百分比"); }
                return Result(Battle.AddCombatBuff(kind, args.Float(1) / 100f));
            }, RevGMArg.Str("属性", "Attack"), RevGMArg.Float("额外百分比", 30f));
        }
        private static string Result(bool success)
        {
            if (!success) { throw new RevGMUsageException(Battle.LastError); }
            return "ok";
        }
        public static void Unregister()
        {
            foreach (string name in Names) { RevGM.Unregister("RevGame/" + name); }
        }
    }
}
#endif
