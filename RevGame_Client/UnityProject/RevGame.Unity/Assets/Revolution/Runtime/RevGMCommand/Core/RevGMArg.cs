// RevGMArg.cs —— 参数说明（写给人看 + 给面板用）
//
// 【用法】注册命令时把它跟在后头，一条一个：
//   RevGM.Register("经济/加金币", "给当前玩家加金币", args => AddGold(args.Int(0)), RevGMArg.Int("数量", 1000));
//
// 【它带来三件事】① 面板会显示 `加金币 <数量|Int32(默认 1000)>`；
//                 ② 执行前框架会先按它校验参数（数量不够 / 类型不对 → 直接告诉你，不用等业务报错）；
//                 ③ 枚举类参数会在面板上列出候选值（王者那套只能靠执行完回一段长文案告诉用户选错了）。

using System.Globalization;

namespace Revolution
{
    /// <summary>参数类型（用于面板提示与执行前的参数校验）。</summary>
    public enum RevGMArgType
    {
        /// <summary>文本</summary>
        Str = 0,

        /// <summary>整数</summary>
        Int = 1,

        /// <summary>小数</summary>
        Float = 2,

        /// <summary>开关（true/false/1/0/on/off/是/否）</summary>
        Bool = 3,

        /// <summary>枚举（候选值写在 <see cref="RevGMArg.Candidates"/> 里）</summary>
        Enum = 4,
    }

    /// <summary>一个参数的说明：名字 / 类型 / 默认值 / 候选值。</summary>
    public sealed class RevGMArg
    {
        /// <summary>参数名（显示在帮助里，也是参数错误信息里的名字）</summary>
        public readonly string Name;

        /// <summary>参数类型</summary>
        public readonly RevGMArgType Type;

        /// <summary>默认值（缺参数时用它；为空表示"必填"）</summary>
        public readonly string DefaultValue;

        /// <summary>候选值（只有 <see cref="RevGMArgType.Enum"/> 用得上；面板会列出来）</summary>
        public readonly string[] Candidates;

        private RevGMArg(string name, RevGMArgType type, string defaultValue, string[] candidates)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            Candidates = candidates;
        }

        /// <summary>是否必填（<b>没有声明默认值</b>才算必填；显式写了空串默认值 = 可不传）</summary>
        public bool IsRequired => DefaultValue == null;

        // ============================================================
        // 便捷构造（一行一个参数）
        // ============================================================

        /// <summary>整数参数：<c>RevGMArg.Int("数量", 1000)</c></summary>
        public static RevGMArg Int(string name, int defaultValue = 0)
            => new RevGMArg(name, RevGMArgType.Int, defaultValue.ToString(CultureInfo.InvariantCulture), null);

        /// <summary>必填整数参数：<c>RevGMArg.IntRequired("英雄ID")</c></summary>
        public static RevGMArg IntRequired(string name) => new RevGMArg(name, RevGMArgType.Int, null, null);

        /// <summary>小数参数：<c>RevGMArg.Float("倍率", 1.5f)</c></summary>
        public static RevGMArg Float(string name, float defaultValue = 0f)
            => new RevGMArg(name, RevGMArgType.Float, defaultValue.ToString(CultureInfo.InvariantCulture), null);

        /// <summary>开关参数：<c>RevGMArg.Bool("开启", true)</c></summary>
        public static RevGMArg Bool(string name, bool defaultValue = false)
            => new RevGMArg(name, RevGMArgType.Bool, defaultValue ? "true" : "false", null);

        /// <summary>文本参数（必填）：<c>RevGMArg.Str("玩家名")</c></summary>
        public static RevGMArg Str(string name) => new RevGMArg(name, RevGMArgType.Str, null, null);

        /// <summary>文本参数（可空并有默认值）：<c>RevGMArg.Str("备注", "无")</c></summary>
        public static RevGMArg Str(string name, string defaultValue) => new RevGMArg(name, RevGMArgType.Str, defaultValue, null);

        /// <summary>枚举参数（候选值会列在面板上）：<c>RevGMArg.Enum("频道", "lobby", "guild", "customteam")</c></summary>
        public static RevGMArg Enum(string name, params string[] candidates)
            => new RevGMArg(name, RevGMArgType.Enum, candidates != null && candidates.Length > 0 ? candidates[0] : null, candidates);

        /// <summary>
        /// 帮助文本里的一段：<c>数量|Int32(默认 1000)</c> / <c>频道|枚举(lobby/guild)</c>
        /// </summary>
        public string Describe()
        {
            string type = Type switch
            {
                RevGMArgType.Str => "文本",
                RevGMArgType.Int => "整数",
                RevGMArgType.Float => "小数",
                RevGMArgType.Bool => "开关",
                RevGMArgType.Enum => "枚举",
                _ => Type.ToString(),
            };

            string tail = Candidates != null && Candidates.Length > 0 ? "(" + string.Join("/", Candidates) + ")"
                        : DefaultValue == null ? "(必填)"
                        : DefaultValue.Length == 0 ? "(可空)"
                        : "(默认 " + DefaultValue + ")";

            return Name + "|" + type + tail;
        }

        /// <summary>调试显示</summary>
        public override string ToString() => Describe();
    }
}
