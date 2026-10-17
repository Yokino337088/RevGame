// ============================================================
// RevStripGuard.cs —— 代码裁剪自检：启动时回答一句"关键类型还在吗"
//
// 位置：Runtime\RevStripGuard\
//
// 【★ 先说前提：本框架的主推方案是"把代码裁剪关掉"】
//   见 Revolution.Document/代码裁剪/代码裁剪使用说明.md（讲清了代价怎么自己测、被裁了会是什么症状）。
//   本模块是【万一你确实要开裁剪】时的最后一道保险 —— 把"沉默的故障"变成"启动时一条明确的错误"。
//
// 【为什么需要它】
//   Unity 的托管代码裁剪（Player Settings → Managed Stripping Level ≥ Medium）会删掉
//   "编译期看起来没人用"的类。而**原生模块**（物理 / UI / 动画 / 音频 / 粒子）的真实现
//   在引擎原生侧（C++），托管层只剩一层薄薄的 wrapper：
//
//     · 业务代码没直接 new Rigidbody  → wrapper 被裁
//     · 但场景 / Prefab 上挂着的原生组件还好好地在那儿（那半边在 native 侧）
//     · 于是真机上的表现是：Rigidbody 变"空壳"、Physics.Raycast 恒返回 false、
//       Animator 不动、AudioSource 不出声 —— **不报错、只是不工作**
//
//   这是最难查的一类故障：编辑器里一切正常（编辑器不裁），真机上玩法彻底瘫掉，
//   而十有八九想不到是代码裁剪干的。
//
// 【它做什么】
//   进游戏（首个场景之前）逐个问引擎："这个类型还在吗？"（Type.GetType 返回 null = 已被裁）
//   缺了就用 RevLog.Error 报出【缺什么 + 什么现象 + 怎么修】，而不是让你在真机上瞎猜。
//
//   —— 说白了：把"沉默的故障"变成"启动时一条明确的错误"。
//
// 【★ 关键前提：自检本身也依赖"诚实的答案"】
//   本文件只用字符串去问 Type.GetType，不直接引用任何被检查的类型 ——
//   一旦直接引用，编译器就会把那个类型当成"有人用"，裁剪器就不会裁它，自检也就永远"通过"了。
//   （这是这类自检最容易写错的地方：检查代码本身会破坏检查的前提。）
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>代码裁剪自检（零配置：进游戏自动跑一次）。</summary>
    public static class RevStripGuard
    {
        /// <summary>被裁掉的类型：类型全名 → 缺了会出现的症状（人话）。</summary>
        private static readonly KeyValuePair<string, string>[] WatchedTypes =
        {
            // ---------- Unity 原生模块：最高频的一批坑 ----------
            new KeyValuePair<string, string>("UnityEngine.Rigidbody, UnityEngine.PhysicsModule", "物理刚体：重力 / 碰撞 / AddForce 全走它。被裁 → 物体变\"空壳\"，不报错就是不响应"),
            new KeyValuePair<string, string>("UnityEngine.Physics, UnityEngine.PhysicsModule", "Physics 静态 API（Raycast / OverlapSphere 等）：被裁 → 射线与范围检测恒返回空"),
            new KeyValuePair<string, string>("UnityEngine.Collider, UnityEngine.PhysicsModule", "碰撞体：被裁 → 点击拾取 / 触发器判定失效"),
            new KeyValuePair<string, string>("UnityEngine.Rigidbody2D, UnityEngine.Physics2DModule", "2D 物理：同样一裁就全失效"),
            new KeyValuePair<string, string>("UnityEngine.Canvas, UnityEngine.UIModule", "UI 根节点：框架 UI 系统直接依赖它，被裁 → 面板全部不显示"),
            new KeyValuePair<string, string>("UnityEngine.Animator, UnityEngine.AnimationModule", "动画：被裁 → Animator 不驱动任何动画"),
            new KeyValuePair<string, string>("UnityEngine.AudioSource, UnityEngine.AudioModule", "音频播放通道：RevSound 靠它，被裁 → 全游戏没声音"),
            new KeyValuePair<string, string>("UnityEngine.ParticleSystem, UnityEngine.ParticleSystemModule", "粒子特效：被裁 → 特效静默不播"),

            // ---------- 框架自己：靠反射工作的那些（业务没直接引用就可能被裁）----------
            new KeyValuePair<string, string>("Revolution.RevUIPanel, Revolution.Runtime", "框架 UI 面板基类：被裁 → 所有面板打不开（RevUIPanelMeta 解析不到特性）"),
            new KeyValuePair<string, string>("Revolution.RevUIPart, Revolution.Runtime", "框架 UI Part 基类：被裁 → Part 全部装配失败"),
            new KeyValuePair<string, string>("Revolution.RevUIWidgetEvents, Revolution.Runtime", "控件事件反射层：被裁 → 按钮点了没反应"),
            new KeyValuePair<string, string>("UnityEngine.KeyCode, UnityEngine.CoreModule", "键位枚举：被裁 → 键位名对不上，全部输入静默失效（只留一条警告）"),
        };

        private static readonly List<string> _missing = new List<string>();

        /// <summary>最近一次检查发现被裁掉的类型（空 = 全部在位）。</summary>
        public static IReadOnlyList<string> MissingTypes { get { return _missing; } }

        /// <summary>最近一次检查是否通过（true = 关键类型全部在位）。</summary>
        public static bool IsHealthy { get { return _missing.Count == 0; } }

        /// <summary>
        /// 进游戏时自动跑一次（早于首个场景加载，业务在 Awake 里出问题时已经能看到这条错误）。
        /// 编辑器下不跑 —— 编辑器不裁剪，检查只会得到"全都好"的假结论。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoCheck()
        {
#if !UNITY_EDITOR
            Check();
#endif
        }

        /// <summary>
        /// 手动跑一次自检（排查时用；返回被裁掉的类型数量，0 = 通过）。
        /// <para>★ 也建议在真机上主动调一次：出包配置换了、升级了 Unity、加了新原生模块，
        /// 都可以主动验一次，比"等玩法瘫了再查"快得多。</para>
        /// </summary>
        public static int Check()
        {
            _missing.Clear();

            for (int i = 0; i < WatchedTypes.Length; i++)
            {
                if (Type.GetType(WatchedTypes[i].Key) == null) { _missing.Add(WatchedTypes[i].Key + "  →  " + WatchedTypes[i].Value); }
            }

            if (_missing.Count == 0)
            {
                RevLog.Info("[裁剪自检] 关键类型全部在位（已查 " + WatchedTypes.Length + " 项）", LogTag);
                return 0;
            }

            Report();
            return _missing.Count;
        }

        /// <summary>自检结果摘要（可直接打到日志或崩溃报告里）。</summary>
        public static string Dump()
        {
            if (_missing.Count == 0) { return "裁剪自检：全部在位"; }
            return "裁剪自检：缺 " + _missing.Count + " 项 —— " + string.Join("；", _missing.ToArray());
        }

        private const string LogTag = "Strip";

        private static void Report()
        {
            var sb = new System.Text.StringBuilder(1024);
            sb.AppendLine("[裁剪自检] 发现 " + _missing.Count + " 个关键类型被代码裁剪掉了 —— 症状是【编辑器一切正常、真机上相关功能静默失效】：");

            for (int i = 0; i < _missing.Count; i++) { sb.AppendLine("  · " + _missing[i]); }

            sb.AppendLine("修法（按顺序做；第 1 步是本框架的【主推方案】）：");
            sb.AppendLine("  1) 【推荐】Project Settings → Player → Other Settings → Managed Stripping Level 改成 Disabled（或至少 Minimal），然后重新出包");
            sb.AppendLine("     —— 代码裁剪省的是包体，赔的是「不报错的功能失效」；而关掉的代价只有包体，3 分钟就能测准。");
            sb.AppendLine("  2) 若确实要保留裁剪：菜单「Revolution.Tools/平台/生成 IL2CPP 裁剪保护 (link.xml)」生成 Assets/link.xml");
            sb.AppendLine("     （会扫出你用到的原生模块与面板 / 单例类型），并确认框架自带的 Assets/Revolution/link.xml 还在");
            sb.AppendLine("  3) 改完必须重新出包 —— 这是构建期行为，改代码不生效。");
            sb.AppendLine("  完整说明（含代价实测方法、症状反查表）：Revolution.Document/代码裁剪/代码裁剪使用说明.md");

            RevLog.Error(sb.ToString(), LogTag);
        }
    }
}
