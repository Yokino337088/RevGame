// 开发属性GM集中复用原自动化夹具：人工面板和AI调用同一条属性/伤害链，不修改正式Excel或存档。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Revolution;
using RevGame.Services;
using UnityEngine;

namespace RevGame.Animation
{
    /// <summary>开发属性命令的唯一注册点；只保存本次Play中的临时强化，不写入存档或正式配置。</summary>
    public static class GameAttributeGmCommands
    {
        /// <summary>由Bootstrap的自动化装配成对注册/注销；面板、官方Automation和业务计划共用这些命令。</summary>
        public static void Register()
        {
            RevGM.Register("RevGame/属性设置", "依次设置额外攻击/攻速/等效防御/最大生命百分比；250=原值×3.5，重复设置不叠加", args =>
            {
                if (args.Count != 4) { throw new RevGMUsageException("用法：RevGame/属性设置 攻击% 攻速% 防御% 生命%"); }
                return Apply(new GameAutomationBoost { attackBonusPercent = args.Int(0), attackSpeedBonusPercent = args.Int(1),
                    defenseBonusPercent = args.Int(2), healthBonusPercent = args.Int(3) });
            }, RevGMArg.Int("攻击%", 0), RevGMArg.Int("攻速%", 0), RevGMArg.Int("防御%", 0), RevGMArg.Int("生命%", 0));
            RegisterAdd("RevGame/加攻击", 0);
            RegisterAdd("RevGame/加攻速", 1);
            RegisterAdd("RevGame/加防御", 2);
            RegisterAdd("RevGame/加血量", 3);
            RevGM.Register("RevGame/属性查询", "返回四项开发额外加成JSON；0为不额外增加，未修改正常准备增益", args =>
            {
                if (args.Count != 0) { throw new RevGMUsageException("属性查询不接受参数"); }
                return Snapshot();
            });
            RevGM.Register("RevGame/属性清除", "清除四项开发强化，生命按比例恢复原上限，不复活死棋", args =>
            {
                if (args.Count != 0) { throw new RevGMUsageException("属性清除不接受参数"); }
                return Apply(null);
            });
        }

        /// <summary>attribute仅为本类内部路由：0攻击、1攻速、2等效防御、3最大生命；单位都是额外百分比。</summary>
        private static void RegisterAdd(string command, int attribute)
        {
            RevGM.Register(command, "累计增加该项额外整数百分比；负数可调低，最终需在0～10000%；AI重试建议用属性设置", args =>
            {
                if (args.Count != 1) { throw new RevGMUsageException("请提供一个整数百分比"); }
                int amount = args.Int(0);
                GameAutomationBoost next = Battle.CopyAutomationBoost();
                // 用long先做加法，防止int溢出后反而绕过范围校验；校验成功前绝不写回原配置。
                int current = attribute == 0 ? next.attackBonusPercent : attribute == 1 ? next.attackSpeedBonusPercent
                    : attribute == 2 ? next.defenseBonusPercent : next.healthBonusPercent;
                long sum = (long)current + amount;
                if (sum < 0 || sum > GameAutomationBoost.MaxBonusPercent) { throw new RevGMUsageException("累计加成超出0～10000%"); }
                if (attribute == 0) { next.attackBonusPercent = (int)sum; }
                else if (attribute == 1) { next.attackSpeedBonusPercent = (int)sum; }
                else if (attribute == 2) { next.defenseBonusPercent = (int)sum; }
                else { next.healthBonusPercent = (int)sum; }
                return Apply(next);
            }, RevGMArg.Int("增加百分比", 0));
        }

        private static BattleService Battle
        {
            get { return (BattleService)RevServices.GetRequired<IBattleService>(); }
        }

        /// <summary>先整体校验再更新；自动化持有租约时沿用该owner，以便失败/取消也能收回本次GM强化。</summary>
        private static string Apply(GameAutomationBoost boost)
        {
            GameAutomationBoost.Validate(boost);
            var runner = RevServices.GetRequired<GameAutomationRunner>();
            if (runner.State == "running") { runner.SetBoostFromGm(boost); }
            else { Battle.SetManualDevelopmentBoost(boost); }
            return Snapshot();
        }

        private static string Snapshot()
        {
            return JsonUtility.ToJson(Battle.CopyAutomationBoost());
        }

        internal static bool IsAttributeCommand(string name)
        {
            return name == "RevGame/属性设置" || name == "RevGame/属性查询" || name == "RevGame/属性清除"
                || name == "RevGame/加攻击" || name == "RevGame/加攻速" || name == "RevGame/加防御" || name == "RevGame/加血量";
        }

        /// <summary>只注销本类拥有的七条命令，不能清空框架里其他业务注册的GM。</summary>
        public static void Unregister()
        {
            foreach (string name in new[] { "RevGame/属性设置", "RevGame/属性查询", "RevGame/属性清除", "RevGame/加攻击",
                "RevGame/加攻速", "RevGame/加防御", "RevGame/加血量" })
            {
                RevGM.Unregister(name);
            }
        }
    }
}
#endif
