// RevSoundKind.cs —— 声音分类 + 每一类的默认行为
//
// 【为什么要有分类】你旧框架只有两个全局音量（音效 / BGM），而一个项目至少需要：
//   UI 音、技能/打击音效、角色语音、背景音乐 —— 这四类的默认行为（是否去重、是否循环）不一样。
//
// 【对标王者的哪条哲学】"分类即语义"：王者用 14 个 Bank 域 + 事件名后缀（`_Hit_` / `_VO_` / `_Down`）表达语义，
//   其中后缀匹配是最典型的糟粕（魔法字符串）。我们把它换成**显式枚举**：调用时写清楚是哪一类。
//
// 【2D / 3D 不由分类决定】由你调哪个方法决定：Play = 2D；PlayOn / PlayAt = 3D
//   （这样就没有"某个分类悄悄变成 3D"这种隐藏魔法）。
//
// 【纯 C#】本文件不引用 UnityEngine —— 分类表可以脱离引擎单测。

namespace Revolution
{
    /// <summary>声音分类：决定默认音量归属、是否同帧去重、是否循环。</summary>
    public enum RevSoundKind
    {
        /// <summary>界面音（按钮、弹窗、页签切换）：同帧同名只播一次</summary>
        Ui = 0,

        /// <summary>游戏内音效（技能、打击、脚步）</summary>
        Sfx = 1,

        /// <summary>角色语音 / 台词：不去重（连续两句同一句台词是合理的）</summary>
        Voice = 2,

        /// <summary>背景音乐：循环（单曲模式）、全局只有一条</summary>
        Bgm = 3,
    }

    /// <summary>一类声音的默认行为（纯数据，便于阅读与扩展）。</summary>
    internal readonly struct RevSoundKindInfo
    {
        /// <summary>默认音量（还会再乘分类音量与主音量）</summary>
        internal readonly float Volume;

        /// <summary>是否启用"同一帧、同一个名字只播一次"（防按钮连点/同帧重复触发）</summary>
        internal readonly bool FrameDedupe;

        /// <summary>是否默认循环</summary>
        internal readonly bool Loop;

        internal RevSoundKindInfo(float volume, bool frameDedupe, bool loop)
        {
            Volume = volume;
            FrameDedupe = frameDedupe;
            Loop = loop;
        }
    }

    /// <summary>分类默认行为表（按 <see cref="RevSoundKind"/> 下标取）。</summary>
    internal static class RevSoundKindTable
    {
        private static readonly RevSoundKindInfo[] Table =
        {
            new RevSoundKindInfo(1.0f, frameDedupe: true,  loop: false),   // Ui
            new RevSoundKindInfo(1.0f, frameDedupe: true,  loop: false),   // Sfx
            new RevSoundKindInfo(1.0f, frameDedupe: false, loop: false),   // Voice
            new RevSoundKindInfo(1.0f, frameDedupe: false, loop: true),    // Bgm
        };

        internal static RevSoundKindInfo Of(RevSoundKind kind)
        {
            int index = (int)kind;
            return index >= 0 && index < Table.Length ? Table[index] : Table[1];
        }
    }
}
