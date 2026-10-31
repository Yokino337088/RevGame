using System;
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>
    /// 普通棋子的攻击配方。由配置快照持有，整局只读，不保存血量、目标或计时进度。
    /// 所有可调数字来自 BalanceRule；枚举对应的几何算法放在 PieceAttackRules，避免表现层决定伤害。
    /// </summary>
    public sealed class PieceAttackProfile
    {
        /// <summary>从攻击者身体到目标身体的最近棋盘距离，单位格，包含对角。</summary>
        public readonly int Range;
        /// <summary>相对现有职业行动周期的间隔倍率；大于1表示更慢，不影响移动或Boss技能。</summary>
        public readonly float IntervalScale;
        /// <summary>首个命中与后续命中的攻击力倍率；范围伤害用较低次要倍率约束总输出。</summary>
        public readonly float PrimaryScale, SecondaryScale;
        /// <summary>一次出手的不同目标上限；大棋占多个格仍只占一个名额。</summary>
        public readonly int MaxTargets;
        /// <summary>爆炸半径或水流每次跳跃的距离，单位格；0表示不扩散。</summary>
        public readonly int Radius;
        /// <summary>收割触发血量比例及额外倍率；阈值0关闭，判断时读取实时血量。</summary>
        public readonly float ExecuteThreshold, ExecuteScale;
        /// <summary>盾战自疗或牧师支援的攻击力转换倍率；0关闭，不复活已经死亡的棋子。</summary>
        public readonly float HealScale;

        private PieceAttackProfile(BalanceReader reader, PieceKind kind)
        {
            string key = "PieceAttack." + kind + ".";
            Range = reader.Int(key + "Range", 1, 9);
            IntervalScale = reader.Float(key + "IntervalScale", .1f, 10f);
            PrimaryScale = reader.Float(key + "PrimaryScale", .01f, 5f);
            SecondaryScale = reader.Float(key + "SecondaryScale", 0f, 5f);
            MaxTargets = reader.Int(key + "MaxTargets", 1, 81);
            Radius = reader.Int(key + "Radius", 0, 8);
            ExecuteThreshold = reader.Float(key + "ExecuteThreshold", 0f, 1f);
            ExecuteScale = reader.Float(key + "ExecuteScale", 1f, 5f);
            HealScale = reader.Float(key + "HealScale", 0f, 2f);
        }

        /// <summary>配置初始化时一次读齐九种棋子；缺键、非法数字由同一个读取器汇总，失败不发布新快照。</summary>
        internal static Dictionary<PieceKind, PieceAttackProfile> Read(BalanceReader reader)
        {
            var result = new Dictionary<PieceKind, PieceAttackProfile>();
            foreach (PieceKind kind in Enum.GetValues(typeof(PieceKind)))
            {
                result.Add(kind, new PieceAttackProfile(reader, kind));
            }
            return result;
        }
    }
}
