// ============================================================
// RevInputCodes.cs —— 键位码表（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   业务要能说"跳跃绑空格、移动绑 WASD"，而内核又不能引用引擎的 `KeyCode`——
//   于是这里定义一份**名字与 Unity `KeyCode` 完全一致**的枚举：
//   名字一致，适配层就能靠"名字"一次性映射（`Enum.TryParse<KeyCode>`），
//   不用维护上百行手工对照表，也不会因为数值抄错而绑错键。
//
// 【三条铁律】
//   ① **只用名字对齐**：本枚举的数值是**紧凑序号**（0..N-1），与 Unity 的数值无关；
//      映射发生在适配层（`Support\RevInputUnityDevice.cs`），且只在启动时做一次。
//   ② **序号即下标**：`(int)RevKey` 就是键位掩码里的位下标 —— 所以键位增删只影响掩码宽度，
//      不影响任何业务代码。
//   ③ **键位只增不改**：已发布过的成员名字不要改（玩家的改键存档按名字存）。
//
// 【键位怎么落到 Unity】
//   适配层把 <see cref="RevKey"/> 的**名字**解析成 <c>UnityEngine.KeyCode</c>；
//   手柄键用的是 Unity 既有的 `JoystickButton0..19` 名字，所以同一套机制直接覆盖手柄。
// ============================================================

namespace Revolution
{
    /// <summary>
    /// 键位（键盘 + 手柄按钮）。**成员名与 Unity 的 <c>KeyCode</c> 一致**，方便适配层按名字映射。
    /// </summary>
    public enum RevKey
    {
        None = 0,

        // ── 字母（26）────────────────────────────────────────
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

        // ── 主键盘数字（10）──────────────────────────────────
        Alpha0, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,

        // ── 功能键（12）──────────────────────────────────────
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

        // ── 编辑与导航（11）──────────────────────────────────
        Space, Return, Escape, Tab, Backspace, Delete, Insert,
        Home, End, PageUp, PageDown,

        // ── 方向键（4）───────────────────────────────────────
        UpArrow, DownArrow, LeftArrow, RightArrow,

        // ── 修饰键（6）───────────────────────────────────────
        LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,

        // ── 符号键（11）──────────────────────────────────────
        BackQuote, Minus, Equals, LeftBracket, RightBracket, Backslash,
        Semicolon, Quote, Comma, Period, Slash,

        // ── 小键盘（16）──────────────────────────────────────
        Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
        KeypadPlus, KeypadMinus, KeypadMultiply, KeypadDivide, KeypadEnter, KeypadPeriod,

        // ── 锁定与系统键（5）─────────────────────────────────
        CapsLock, Numlock, ScrollLock, PrintScreen, Pause,

        // ── 手柄（20）——名字与 Unity 的 KeyCode.JoystickButtonN 一致 ──────
        JoystickButton0, JoystickButton1, JoystickButton2, JoystickButton3, JoystickButton4,
        JoystickButton5, JoystickButton6, JoystickButton7, JoystickButton8, JoystickButton9,
        JoystickButton10, JoystickButton11, JoystickButton12, JoystickButton13, JoystickButton14,
        JoystickButton15, JoystickButton16, JoystickButton17, JoystickButton18, JoystickButton19,
    }

    /// <summary>键位码表的元信息（键位总数、掩码需要几个 <c>ulong</c>）。</summary>
    public static class RevInputCodes
    {
        /// <summary>键位总数（含 <see cref="RevKey.None"/>）。</summary>
        public static readonly int KeyCount = System.Enum.GetValues(typeof(RevKey)).Length;

        /// <summary>掩码需要几个 <c>ulong</c>（64 位一组）。</summary>
        public static readonly int MaskWordCount = (KeyCount + 63) / 64;

        /// <summary>掩码够不够放下全部键位（自检用：加了键位忘了放宽掩码会在这里暴露）。</summary>
        public static bool MaskFits => MaskWordCount <= RevKeyMask.Words;
    }

    /// <summary>
    /// 键位掩码：定长 <see cref="Words"/> 个 <c>ulong</c>，按下/按住/抬起各一份 —— 每帧零分配。
    /// 内核只用它，不碰任何引擎 API。
    /// </summary>
    public struct RevKeyMask
    {
        /// <summary>mask 的宽度（4 × 64 = 256 位，够 250 个键位）。</summary>
        public const int Words = 4;

        public ulong W0;
        public ulong W1;
        public ulong W2;
        public ulong W3;

        /// <summary>置位（下标越界静默忽略：键位表变了也不会崩）。</summary>
        public void Set(int index)
        {
            switch (index >> 6)
            {
                case 0: W0 |= 1UL << (index & 63); break;
                case 1: W1 |= 1UL << (index & 63); break;
                case 2: W2 |= 1UL << (index & 63); break;
                case 3: W3 |= 1UL << (index & 63); break;
            }
        }

        /// <summary>读位。</summary>
        public bool Get(int index)
        {
            switch (index >> 6)
            {
                case 0: return (W0 & (1UL << (index & 63))) != 0;
                case 1: return (W1 & (1UL << (index & 63))) != 0;
                case 2: return (W2 & (1UL << (index & 63))) != 0;
                case 3: return (W3 & (1UL << (index & 63))) != 0;
                default: return false;
            }
        }

        /// <summary>定向置位（<paramref name="on"/> 为 false 时清位）。</summary>
        public void Set(int index, bool on)
        {
            if (on) Set(index);
            else Clear(index);
        }

        /// <summary>清位。</summary>
        public void Clear(int index)
        {
            switch (index >> 6)
            {
                case 0: W0 &= ~(1UL << (index & 63)); break;
                case 1: W1 &= ~(1UL << (index & 63)); break;
                case 2: W2 &= ~(1UL << (index & 63)); break;
                case 3: W3 &= ~(1UL << (index & 63)); break;
            }
        }

        /// <summary>整片清空（保留容量，不产生垃圾）。</summary>
        public void ClearAll()
        {
            W0 = W1 = W2 = W3 = 0UL;
        }

        /// <summary>有没有任何一位被置上（自检 / 诊断用）。</summary>
        public bool Any => (W0 | W1 | W2 | W3) != 0UL;

        /// <summary>两个掩码是否有交集（"这组键里有按下的吗"一次算完）。</summary>
        public bool Intersects(in RevKeyMask other)
            => ((W0 & other.W0) | (W1 & other.W1) | (W2 & other.W2) | (W3 & other.W3)) != 0UL;

        /// <summary>把 <paramref name="other"/> 里的位并进来。</summary>
        public void Or(in RevKeyMask other)
        {
            W0 |= other.W0;
            W1 |= other.W1;
            W2 |= other.W2;
            W3 |= other.W3;
        }

        public override string ToString()
            => "KeyMask(" + W0.ToString("X") + " " + W1.ToString("X") + " " + W2.ToString("X") + " " + W3.ToString("X") + ")";
    }
}
