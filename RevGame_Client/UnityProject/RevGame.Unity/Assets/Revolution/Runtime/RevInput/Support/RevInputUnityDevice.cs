// ============================================================
// RevInputUnityDevice.cs —— 默认设备：键盘 / 鼠标 / 触屏 / 手柄（全模块唯一读 Input.* 的地方）
//
// 位置：Runtime\RevInput\Support\
//
// 【要解决的问题】
//   把 Unity 的三套输入（键鼠、触摸、摇杆轴）压成**同一份快照**，内核只认快照。
//   于是换引擎输入方案（比如换成 Input System 包）只需改这一个文件。
//
// 【三条铁律】
//   ① **键位靠名字映射**：`RevKey` 的成员名与 Unity `KeyCode` 一致，启动时用 `Enum.TryParse` 建表，
//      不手抄上百行数值（抄错一位就是"跳跃绑到退出"的线上事故）。
//   ② **只轮询"可能用到"的键**：每帧只测"绑定表用到的键 ∪ 上一帧按住的键"，
//      而不是无脑测 100+ 个键（省掉大半无用查询，也不丢抬起事件）。
//   ③ **未知轴名不刷屏**：`Input.GetAxis` 遇到没配的轴名会抛异常 —— 这里缓存"坏名字"，
//      只报警一次，之后按 0 处理。
//
// 【顺带一个方便】
//   Unity 编辑器默认 `Input.simulateMouseWithTouches = true`：用鼠标拖拽会**生成触摸事件**，
//   所以手势（点击 / 滑动 / 捏合）不插手机也能在编辑器里调。
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>默认输入设备：读 Unity 的旧输入 API（不依赖 Input System 包）。</summary>
    internal sealed class RevInputUnityDevice
    {
        /// <summary>采集源名字（自检输出里显示）。</summary>
        internal string Name => "Unity(KeyMouseTouchPad)";

        private readonly KeyCode[] _keyCodes = new KeyCode[RevInputCodes.KeyCount];
        private readonly HashSet<string> _badAxisNames = new HashSet<string>();

        private RevKeyMask _tracked;      // 本帧要测的键
        private RevKeyMask _lastHeld;     // 上一帧按住的键（保证抬起事件不丢）
        private RevInputDeviceKind _lastKind = RevInputDeviceKind.Unknown;

        private float _lastMouseX;
        private float _lastMouseY;
        private bool _lastMouseValid;

        internal RevInputUnityDevice()
        {
            BuildKeyTable();
        }

        internal void ResetForNewSession()
        {
            // 关闭 Domain Reload 时，Unity 不会重新创建这个设备采集器；上一局记录的“哪些键还按着”、上次设备类型和鼠标位置会留在内存。
            // 新一局若沿用这些值，可能误报按键仍按住、鼠标第一帧位移异常，或把新输入识别成上一局的设备类型，所以全部重置。
            _tracked.ClearAll();
            _lastHeld.ClearAll();
            _lastKind = RevInputDeviceKind.Unknown;
            _lastMouseX = _lastMouseY = 0f;
            _lastMouseValid = false;
            _badAxisNames.Clear();
        }

        /// <summary>建"RevKey 名字 → Unity KeyCode"映射表（启动一次）。</summary>
        private void BuildKeyTable()
        {
            int unknown = 0;
            for (int i = 0; i < _keyCodes.Length; i++)
            {
                var key = (RevKey)i;
                if (key == RevKey.None)
                {
                    _keyCodes[i] = KeyCode.None;
                    continue;
                }

                if (Enum.TryParse(key.ToString(), out KeyCode code))
                {
                    _keyCodes[i] = code;
                }
                else
                {
                    _keyCodes[i] = KeyCode.None;
                    unknown++;
                }
            }

            if (unknown > 0)
                RevInputLog.W("[RevInput] 有 " + unknown + " 个键位名在 Unity KeyCode 里不存在（会被跳过；键位名必须与 KeyCode 一致）");
        }

        /// <summary>读命名轴（内核通过 <c>AxisProvider</c> 调它；纯 C# 环境下这个委托为 null）。</summary>
        internal float ReadAxis(string axisName)
        {
            if (string.IsNullOrEmpty(axisName)) return 0f;
            if (_badAxisNames.Contains(axisName)) return 0f;     // 只报一次，之后按 0

            try
            {
                return Input.GetAxis(axisName);
            }
            catch (Exception)
            {
                _badAxisNames.Add(axisName);
                RevInputLog.W("[RevInput] InputManager 里没有轴名 \"" + axisName + "\"（按 0 处理）——"
                              + "命名轴要在 Project Settings → Input Manager 里配好");
                return 0f;
            }
        }

        /// <summary>采集一帧，写进快照（内核通过采集委托每帧调一次）。</summary>
        internal void Poll(RevInputSnapshot snapshot)
        {
            RevInputCore core = RevInput.Core;

            // ① 只测"绑定用到的键 ∪ 上一帧按住的键"
            RevKeyMask bound = core.Actions.BoundKeyUnion();
            _tracked.Or(bound);
            _tracked.Or(_lastHeld);

            RevKeyMask held = default;
            bool keyboardActive = false;
            bool gamepadButtonActive = false;

            for (int word = 0; word < RevKeyMask.Words; word++)
            {
                ulong bits = Word(_tracked, word);
                while (bits != 0UL)
                {
                    int bit = TrailingZeros(bits);
                    bits &= bits - 1UL;
                    int index = word * 64 + bit;
                    if (index >= _keyCodes.Length) continue;

                    KeyCode code = _keyCodes[index];
                    if (code == KeyCode.None) continue;

                    // JoystickButtonN 属于手柄活动，不能误计为键盘输入，否则仅按手柄按钮会被识别成键盘设备。
                    // JoystickButtonN 是手柄按钮，必须与 keyboardActive 分开，否则只按手柄键会被识别成键盘活动。
                    bool gamepadButton = index >= (int)RevKey.JoystickButton0 && index <= (int)RevKey.JoystickButton19;
                    if (Input.GetKey(code))
                    {
                        held.Set(index);
                        if (gamepadButton) gamepadButtonActive = true;
                        else keyboardActive = true;
                    }
                    if (Input.GetKeyDown(code))
                    {
                        snapshot.KeyDown.Set(index);
                        if (gamepadButton) gamepadButtonActive = true;
                        else keyboardActive = true;
                    }
                    if (Input.GetKeyUp(code))
                    {
                        snapshot.KeyUp.Set(index);
                        if (gamepadButton) gamepadButtonActive = true;
                        else keyboardActive = true;
                    }
                }
            }

            snapshot.KeyHeld.Or(held);
            _lastHeld = held;
            _tracked.ClearAll();      // 下一帧重新按"绑定 ∪ 按住"组装

            // ② 鼠标（位置 / 位移 / 滚轮 / 五个键）
            float mx = Input.mousePosition.x;
            float my = Input.mousePosition.y;
            snapshot.MouseX = mx;
            snapshot.MouseY = my;
            if (_lastMouseValid)
            {
                snapshot.MouseDeltaX = mx - _lastMouseX;
                snapshot.MouseDeltaY = my - _lastMouseY;
            }
            _lastMouseX = mx;
            _lastMouseY = my;
            _lastMouseValid = true;

            Vector2 scroll = Input.mouseScrollDelta;
            snapshot.ScrollX = scroll.x;
            snapshot.ScrollY = scroll.y;

            // 用户滚动滚轮时也属于正在使用鼠标。若这里不把滚轮计入 mouseActive，后面的设备识别会以为本帧没有鼠标操作，
            // 从而保留旧设备类型，或被手柄轴探测误判成正在使用手柄。
            bool mouseActive = Math.Abs(snapshot.MouseDeltaX) > 0.01f || Math.Abs(snapshot.MouseDeltaY) > 0.01f
                               || Math.Abs(snapshot.ScrollX) > 0.01f || Math.Abs(snapshot.ScrollY) > 0.01f;
            for (int b = 0; b <= (int)RevMouseButton.Forward; b++)
            {
                byte bit = (byte)(1 << b);
                if (Input.GetMouseButtonDown(b)) { snapshot.MouseDown |= bit; mouseActive = true; }
                if (Input.GetMouseButton(b)) { snapshot.MouseHeld |= bit; mouseActive = true; }
                if (Input.GetMouseButtonUp(b)) { snapshot.MouseUp |= bit; mouseActive = true; }
            }

            // ③ 触摸（多点）
            bool touchActive = false;
            int touchCount = Input.touchCount;
            for (int i = 0; i < touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                RevPointerPhase phase;
                switch (touch.phase)
                {
                    case TouchPhase.Began: phase = RevPointerPhase.Began; break;
                    case TouchPhase.Moved: phase = RevPointerPhase.Moved; break;
                    case TouchPhase.Stationary: phase = RevPointerPhase.Stationary; break;
                    case TouchPhase.Ended: phase = RevPointerPhase.Ended; break;
                    default: phase = RevPointerPhase.Canceled; break;
                }

                var sample = new RevPointerSample(touch.fingerId,
                    touch.position.x, touch.position.y,
                    touch.deltaPosition.x, touch.deltaPosition.y, phase);

                if (!snapshot.AddPointer(sample))
                    core.Fail(RevInputErrorReason.TooManyPointers,
                        "本帧触摸数 " + touchCount + " 超过上限 " + RevInputLimits.MaxPointers);

                touchActive = true;
            }

            // ④ 手柄：按钮已经在键位表里（JoystickButtonN）；这里只看"有没有摇杆/扳机在动"
            // 仅在本帧其他设备均无活动时探测手柄轴，避免轴读数覆盖本帧真实的键鼠/触屏分类。
            bool gamepadActive = gamepadButtonActive;
            if (!touchActive && !keyboardActive && !mouseActive && !gamepadButtonActive)
            {
                for (int i = 0; i < _joyAxes.Length; i++)
                {
                    float value = ReadAxis(_joyAxes[i]);
                    if (value > 0.35f || value < -0.35f)
                    {
                        gamepadActive = true;
                        break;
                    }
                }
            }

            // ⑤ 设备识别：只认"本帧真的有输入"的那类；都没有就沿用上一次（避免抖动）
            if (touchActive) _lastKind = RevInputDeviceKind.Touch;
            else if (keyboardActive || mouseActive) _lastKind = RevInputDeviceKind.KeyboardMouse;
            else if (gamepadActive) _lastKind = RevInputDeviceKind.Gamepad;
            snapshot.Device = _lastKind;
        }

        private static readonly string[] _joyAxes =
        {
            "Horizontal", "Vertical", "Mouse X", "Mouse Y", "Mouse ScrollWheel",
        };

        private static ulong Word(in RevKeyMask mask, int word)
        {
            switch (word)
            {
                case 0: return mask.W0;
                case 1: return mask.W1;
                case 2: return mask.W2;
                default: return mask.W3;
            }
        }

        private static int TrailingZeros(ulong value)
        {
            int n = 0;
            while ((value & 1UL) == 0UL && n < 64)
            {
                value >>= 1;
                n++;
            }
            return n;
        }
    }
}
