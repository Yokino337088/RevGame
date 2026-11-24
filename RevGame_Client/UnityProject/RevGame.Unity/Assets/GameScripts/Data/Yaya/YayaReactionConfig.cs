// 鸦鸦互动参数和台词只读Excel生成表，图片映射独立SO；此类校验边界，避免真机播放到一半才发现错表。
using System;
using Revolution;
namespace RevGame.Data
{
    /// <summary>远征和无尽共用的互动配置入口。模式 1 是远征，模式 2 是无尽，档 4 是两种模式共用的无操作台词。</summary>
    public static class YayaReactionConfig
    {
        /// <summary>无操作嘲讽专用档位，两种玩法都从这一档抽选。</summary>
        public const int IdleTauntBand = 4;
        /// <summary>两种玩法的无操作阈值，单位为战斗秒。保留原表键以直接读取已发布的配置。</summary>
        public const string IdleTauntSecondsKey = "CampaignIdleTauntSeconds";

        /// <summary>判断台词是否适用当前玩法。无操作档沿用原表里的远征行与共用行，战绩台词按玩法匹配。</summary>
        public static bool MatchesMode(YayaReactionLine line, int mode)
        {
            int sourceMode = line.Band == IdleTauntBand ? 1 : mode;
            return line.Mode == 0 || line.Mode == sourceMode;
        }

        /// <summary>按参数键取有限数值，缺配置明确失败，不能给静默默认值掩盖导表遗漏。</summary>
        public static float Value(string key)
        {
            if (!RevDataTableManager.Get<YayaReactionRuleTable>().FindByKey(key, out YayaReactionRule row) || float.IsNaN(row.Value) || float.IsInfinity(row.Value))
            {
                throw new InvalidOperationException("鸦鸦互动参数缺失或非法：" + key);
            }
            return row.Value;
        }
        /// <summary>进场前验证时间、阈值和各档台词覆盖，数值变更无需程序重编译。</summary>
        public static void Validate()
        {
            Validate(RevDataTableManager.Get<YayaReactionRuleTable>(), RevDataTableManager.Get<YayaReactionLineTable>());
        }
        /// <summary>编辑器纯配置检查可以传入解析后的同类型表，不注册运行时全局服务或改变资源策略。</summary>
        public static void Validate(YayaReactionRuleTable rules, YayaReactionLineTable lines)
        {
            float V(string key)
            {
                if (!rules.FindByKey(key, out YayaReactionRule row) || float.IsNaN(row.Value) || float.IsInfinity(row.Value))
                {
                    throw new InvalidOperationException("鸦鸦互动参数缺失或非法：" + key);
                }
                return row.Value;
            }
            foreach (string key in new[] { "WindowSeconds", "GlobalCooldown", "SameBandCooldown", "InitialDelay", IdleTauntSecondsKey, "CampaignGoodKills", "EndlessGoodKills", "PoorLosses", "PopupSeconds", "SampleSeconds", "MaximumLines" })
            {
                if (V(key) <= 0)
                {
                    throw new InvalidOperationException("鸦鸦互动参数必须为正数：" + key);
                }
            }
            if (V("GoodTakenRatio") < 0 || V("PoorTakenRatio") <= V("GoodTakenRatio") || V("PoorTakenRatio") > 1)
            {
                throw new InvalidOperationException("鸦鸦受伤阈值要求0≤好表现阈值<不佳阈值≤1");
            }
            bool[,] bands = new bool[3, IdleTauntBand];
            int count = 0;
            foreach (YayaReactionLine line in lines.All)
            {
                if (line.Id <= 0 || line.Band < 1 || line.Band > IdleTauntBand || line.Mode < 0 || line.Mode > 2
                    || (line.Band == IdleTauntBand && line.Mode == 2) || line.Expression <= 0
                    || string.IsNullOrWhiteSpace(line.Text) || line.Weight <= 0 || line.Duration < 0)
                {
                    throw new InvalidOperationException("鸦鸦互动台词非法：" + line.Id);
                }
                for (int mode = 1; mode <= 2; mode++)
                {
                    if (MatchesMode(line, mode))
                    {
                        bands[mode, line.Band - 1] = true;
                    }
                }
                count++;
            }
            if (count > V("MaximumLines"))
            {
                throw new InvalidOperationException("鸦鸦台词超过配置上限");
            }
            for (int mode = 1; mode <= 2; mode++)
            {
                for (int band = 0; band < IdleTauntBand; band++)
                {
                    if (!bands[mode, band])
                    {
                        throw new InvalidOperationException("鸦鸦台词缺档：模式" + mode + "，档" + (band + 1));
                    }
                }
            }
        }
    }
}
