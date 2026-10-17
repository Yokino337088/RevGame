// ============================================================
// RevInputBinding.cs —— 一个"逻辑动作"的绑定（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   业务只说"跳跃"，不该关心它绑的是空格、手柄 A 还是屏幕上的按钮 ——
//   绑定表就是这层翻译：**动作名 → 输入源**。改键、存档、冲突检测全都围着它转。
//
// 【三条铁律】
//   ① **掩码预计算**：绑定时把"这个动作占哪些键位 / 鼠标键"算成两份掩码，
//      每帧判定就是一次位运算 —— 不用遍历绑定表（这是"每帧零分配、零遍历"的关键）。
//   ② **一个动作三种输入源可共存**：键位（最多 4）、鼠标键（最多 5）、一根轴（键位对或 Unity 轴名）。
//      "按下"判定 = 键位或鼠标键任一命中；"轴"判定独立（移动这类连续量）。
//   ③ **改键只改这张表**：业务代码里不出现任何键位字面量（写 `RevInput.Pressed("Jump")`，
//      不写 `Input.GetKeyDown(KeyCode.Space)`）——这是"傻瓜式"的前提。
//
// 【文本格式】（`RevInput.SaveBindings()` 产出的就是它，玩家改键存这个）
//   Jump  = Space, JoystickButton0
//   Attack= Mouse0
//   MoveX = -A +D
//   LookX = axis:Mouse X
// ============================================================

using System.Collections.Generic;

namespace Revolution
{
    /// <summary>一个逻辑动作的绑定定义（数据 + 预计算掩码）。</summary>
    public sealed class RevInputBinding
    {
        /// <summary>动作名（业务侧唯一标识，例如 "Jump" / "MoveX"）。</summary>
        public readonly string Action;

        private readonly List<RevKey> _keys = new List<RevKey>(RevInputLimits.MaxKeysPerAction);
        private readonly List<RevMouseButton> _mouse = new List<RevMouseButton>(2);

        /// <summary>轴的反向键（例如 A）；<see cref="RevKey.None"/> 表示没有键位轴。</summary>
        public RevKey AxisNegative = RevKey.None;

        /// <summary>轴的正向键（例如 D）。</summary>
        public RevKey AxisPositive = RevKey.None;

        /// <summary>Unity InputManager 里的轴名（走它时忽略键位轴；手柄左摇杆就是这种）。</summary>
        public string NamedAxis;

        /// <summary>命名轴是否取反（例如"上"在 Unity 里是 +1，而屏幕 y 向下为正）。</summary>
        public bool NamedAxisInvert;

        /// <summary>死区（只对轴有意义）。</summary>
        public float Deadzone = RevInputLimits.DefaultDeadzone;

        /// <summary>连发：按住后多久开始连发（秒；0 = 不连发）。</summary>
        public float RepeatDelay = RevInputLimits.DefaultRepeatDelay;

        /// <summary>连发：连发间隔（秒）。</summary>
        public float RepeatInterval = RevInputLimits.DefaultRepeatInterval;

        /// <summary>键位掩码（预计算：本动作占用了哪些键位）。</summary>
        public RevKeyMask KeyMask;

        /// <summary>鼠标键掩码（位下标 = <see cref="RevMouseButton"/>）。</summary>
        public byte MouseMask;

        public RevInputBinding(string action)
        {
            Action = action;
        }

        /// <summary>绑了几个键位。</summary>
        public int KeyCount => _keys.Count;

        /// <summary>绑了几个鼠标键。</summary>
        public int MouseCount => _mouse.Count;

        /// <summary>有没有"按钮"行为（决定 <c>Pressed/Held/Released</c> 是否有意义）。</summary>
        public bool HasButton => _keys.Count > 0 || _mouse.Count > 0;

        /// <summary>有没有"轴"行为（决定 <c>Axis</c> 是否有意义）。</summary>
        public bool HasAxis => AxisNegative != RevKey.None || AxisPositive != RevKey.None
                               || !string.IsNullOrEmpty(NamedAxis);

        /// <summary>读第 <paramref name="index"/> 个键位（越界返回 <see cref="RevKey.None"/>）。</summary>
        public RevKey Key(int index) => index >= 0 && index < _keys.Count ? _keys[index] : RevKey.None;

        /// <summary>读第 <paramref name="index"/> 个鼠标键。</summary>
        public RevMouseButton Mouse(int index)
            => index >= 0 && index < _mouse.Count ? _mouse[index] : RevMouseButton.Left;

        /// <summary>加一个键位（重复加不报错；超上限返回 false 并让调用方报原因码）。</summary>
        public bool AddKey(RevKey key)
        {
            // enum 可被强转成越界整数；拒绝无效值，避免写入键位掩码时索引越界或产生不可采集的绑定。
            if (key == RevKey.None || !System.Enum.IsDefined(typeof(RevKey), key)) return false;
            if (_keys.Contains(key)) return true;
            if (_keys.Count >= RevInputLimits.MaxKeysPerAction) return false;
            _keys.Add(key);
            RebuildMasks();
            return true;
        }

        /// <summary>去掉一个键位。</summary>
        public bool RemoveKey(RevKey key)
        {
            bool removed = _keys.Remove(key);
            if (removed) RebuildMasks();
            return removed;
        }

        /// <summary>加一个鼠标键。</summary>
        public bool AddMouse(RevMouseButton button)
        {
            // 防止强转出来的非法按钮值移位生成错误掩码，或让绑定与实际鼠标采集不一致。
            if (!System.Enum.IsDefined(typeof(RevMouseButton), button)) return false;
            if (_mouse.Contains(button)) return true;
            if (_mouse.Count >= 5) return false;
            _mouse.Add(button);
            RebuildMasks();
            return true;
        }

        /// <summary>去掉一个鼠标键。</summary>
        public bool RemoveMouse(RevMouseButton button)
        {
            bool removed = _mouse.Remove(button);
            if (removed) RebuildMasks();
            return removed;
        }

        /// <summary>清掉全部输入源（改键前的"先清再设"用）。</summary>
        public void ClearSources()
        {
            _keys.Clear();
            _mouse.Clear();
            AxisNegative = RevKey.None;
            AxisPositive = RevKey.None;
            NamedAxis = null;
            NamedAxisInvert = false;
            RebuildMasks();
        }

        /// <summary>重算掩码（任何键位变动后必须调用；内部已自动调，手改字段时别忘了）。</summary>
        public void RebuildMasks()
        {
            KeyMask.ClearAll();
            for (int i = 0; i < _keys.Count; i++)
                KeyMask.Set((int)_keys[i]);

            MouseMask = 0;
            for (int i = 0; i < _mouse.Count; i++)
                MouseMask |= (byte)(1 << (int)_mouse[i]);
        }

        /// <summary>本动作占用的键位是否与本帧快照有交集（一次位运算）。</summary>
        public bool HitKeys(in RevKeyMask mask) => KeyMask.Intersects(mask);

        /// <summary>本动作占用的鼠标键是否与本帧掩码有交集。</summary>
        public bool HitMouse(byte mask) => (MouseMask & mask) != 0;

        /// <summary>把绑定写成人可读的一行（用于存档与冲突提示）。</summary>
        public string ToText()
        {
            var parts = new List<string>();
            for (int i = 0; i < _keys.Count; i++) parts.Add(_keys[i].ToString());
            for (int i = 0; i < _mouse.Count; i++) parts.Add("Mouse" + (int)_mouse[i]);
            if (AxisNegative != RevKey.None) parts.Add("-" + AxisNegative);
            if (AxisPositive != RevKey.None) parts.Add("+" + AxisPositive);
            if (!string.IsNullOrEmpty(NamedAxis)) parts.Add("axis:" + NamedAxis + (NamedAxisInvert ? "-" : string.Empty));

            // ★ Bug 修复（2026-09-30）：连发节拍必须随存档导出 —— 原实现只写 deadzone，
            //   SaveText ⇄ LoadText 一趟下来连发配置全回默认值，违反"无损往返"铁律。
            //   格式 repeat:delay/interval（LoadText 有对应的解析分支），非默认值才写。
            if (RepeatDelay != RevInputLimits.DefaultRepeatDelay
                || RepeatInterval != RevInputLimits.DefaultRepeatInterval)
            {
                // 用“往返格式”保存浮点数，确保读回后仍是原来的 delay/interval；固定保留几位小数会截掉精度，存档再读就变成不同的连发节拍。
                // 同时固定使用英文小数点；不同地区可能用逗号作小数点，而本文件又用逗号分隔键位，容易把一个数字误拆成两个字段。
                parts.Add("repeat:" + RepeatDelay.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                          + "/" + RepeatInterval.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }

            // 用 invariant round-trip 格式往返保存浮点值；固定小数位会截断 Deadzone，区域小数符号也可能导致存档读回不同。
            string text = Action + " = " + string.Join(", ", parts);
            if (Deadzone != RevInputLimits.DefaultDeadzone)
                text += "  deadzone=" + Deadzone.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return text;
        }

        public override string ToString() => ToText();
    }
}
