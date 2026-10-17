// ============================================================
// RevResBootstrap.cs —— 资源系统启动装配
//
// 位置：Runtime\资源加载\
//
// 【它负责三件事】
//   ① 决定注册哪些策略、以什么顺序（顺序 = 优先级）；
//   ② 读取打包工具生成的 ResMap 映射表；
//   ③ 提供切场景时的统一清理入口。
//
// 【两种模式】
//   · 开发模式（默认）：编辑器下只注册 RevEditorResPolicy，
//     所有资源一律 AssetDatabase 直读，AB / Resources 完全不参与。
//   · AB 模式：真机自动使用；编辑器下需手动开启
//     （菜单 Revolution.Tools/资源/AB 加载模式（编辑器），或代码设 UseABInEditor = true）。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    public class RevResBootstrap : RevSingleton<RevResBootstrap>
    {
        // 单例：构造函数写 private 只是"防止外部 new"的可选加固，不写也能正常工作
        private RevResBootstrap() { }

        private RevABLoader _abLoader;

#if UNITY_EDITOR
        // ============================================================
        // 编辑器专用开关：是否强制走 AB 模式
        //   用 EditorPrefs 持久化 —— 关掉 Unity 再打开仍记得上次的选择。
        //   默认 false：开发期一律走编辑器直读。
        // ============================================================
        private const string UseABInEditorKey = "Revolution.Res.UseABInEditor";
        private const string MenuPath = "Revolution.Tools/资源/AB 加载模式（编辑器）";

        public static bool UseABInEditor
        {
            get => UnityEditor.EditorPrefs.GetBool(UseABInEditorKey, false);
            set
            {
                if (UseABInEditor == value) return;
                UnityEditor.EditorPrefs.SetBool(UseABInEditorKey, value);
                Instance.Init();          // 切换后立即重建策略，无需重启编辑器
            }
        }

        [UnityEditor.MenuItem(MenuPath, false, 100)]
        private static void ToggleABMode() => UseABInEditor = !UseABInEditor;

        [UnityEditor.MenuItem(MenuPath, true)]
        private static bool ToggleABModeValidate()
        {
            UnityEditor.Menu.SetChecked(MenuPath, UseABInEditor);   // 菜单上显示勾号
            return true;
        }
#endif

        // ============================================================
        // 映射表覆盖钩子（RevHotUpdate 热更包使用；本框架自身永不设置它）
        // ============================================================

        /// <summary>
        /// ResMap 映射表的覆盖来源：热更包用它把"逻辑名 → 包名|资源名"换成热更版本 ——
        /// 否则"新增资源 / 改包"永远加载不到（表在包体的 Resources 里，运行时只读）。
        /// <para>★ 返回 null → 只用内置表。不设置时（默认 null）行为与从前完全一致 —— 零影响。</para>
        /// <para>★ 语义是"覆盖合并"：热更表覆盖内置表的同名键，内置表独有的键保留 ——
        /// 新资源能热更、老资源在热更表残缺时仍有内置表兜底（两边都要）。</para>
        /// </summary>
        public static Func<Dictionary<string, string>> ResMapOverride { get; set; }

        // ==================== 初始化 ====================

        public void Init()
        {
            // Init 可能是编辑器 AB 开关触发的热切换，也可能在关闭 Domain Reload 后重复执行；
            // 先终止旧请求并用旧策略释放缓存，再替换策略，避免缓存继续返回旧来源资源。
            RevAsyncLoadPump.CancelAll();
            RevResPreloader.ReleaseAll();
            RevResManager.UnloadAll();
            _abLoader?.ReleaseAll();
            _abLoader = null;
            RevResManager.ClearAllPolicies();

            // 启动"自动卸载门卫"（开发模式 / AB 模式都需要，所以放在策略注册之前）
            if (RevResAutoUnloader.AutoStart) RevResAutoUnloader.EnsureRunning();

#if UNITY_EDITOR
            // ================== 开发模式（默认）==================
            // 只注册编辑器策略 → 所有资源一律 AssetDatabase 直读；
            // 直接 return，AB / Resources 策略根本不注册。
            if (!UseABInEditor)
            {
                RevResManager.RegisterPolicy(new RevEditorResPolicy());
                return;
            }
            // 开启 AB 模式后不注册 RevEditorResPolicy，继续走下面的运行时注册
#endif

            // ================== AB 模式（运行时 / 编辑器手动开启）==================
            // ① AB 策略（主方案）：除 "Res/" 前缀外全部接管；失败可兜底
            _abLoader = new RevABLoader();
            Dictionary<string, string> map = LoadResMap();
            RevResManager.RegisterPolicy(new RevABResPolicy(map, _abLoader));

            // ② Resources 策略（链尾兜底）：接住 "Res/" 特殊资源 + AB 加载失败兜底
            RevResManager.RegisterPolicy(new RevResourcesResPolicy());

            // 注册顺序 = 优先级 → AB > Resources
        }

        /// <summary>读取打包工具生成的映射表（每行：逻辑名|包名|资源名）</summary>
        private Dictionary<string, string> LoadResMap()
        {
            var map = new Dictionary<string, string>();

            // ★ 路径规则：ResMap.txt 放在框架自己的 Resources 文件夹里
            //   （Assets/Revolution/Resources/ResourceSystem/ResMap.txt）。
            //   而 Resources.Load 的路径是"相对任意 Resources 文件夹"、且不带扩展名，
            //   所以这里写 "ResourceSystem/ResMap"。
            //   ★ 与 ABBuildSetting.MapAssetPath 是一对，改一个必须改另一个。
            TextAsset ta = Resources.Load<TextAsset>("ResourceSystem/ResMap");
            // 内置表可能不存在（纯远端首包）：此时仍须继续读取热更覆盖表。
            // 如果这里直接 return，客户端即使已下载 ResMap，也会把所有资源误判为缺失。
            if (ta != null)
            {
                foreach (string raw in ta.text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    string[] p = line.Split('|');
                    if (p.Length != 3) continue;

                    map[p[0]] = p[1] + "|" + p[2];
                }
            }

            // ★ 热更表覆盖内置表（合并而不是替换）：新资源能热更；热更表残缺时内置表仍是兜底。
            //   钩子先取快照再调用 —— 与 RevABLoader.BundlePathResolver 同一条纪律（防并发清空）。
            Func<Dictionary<string, string>> overrideSource = ResMapOverride;
            if (overrideSource != null)
            {
                Dictionary<string, string> hot = overrideSource();
                if (hot != null)
                {
                    foreach (KeyValuePair<string, string> kv in hot) map[kv.Key] = kv.Value;
                }
            }

            return map;
        }

        // ==================== 清理 ====================

        /// <summary>
        /// 切场景 / 退出战斗：取消该组任务 → 归还该组预加载引用 → 按组卸载 → 清理未使用表（取消令牌按任务独立，不需要全局复位）。
        ///
        /// 【关于 force】（配合 RevResManager.UnloadGroup 的语义）
        ///   true（默认）：连"仍被引用的也一并清账" —— 适合"整个业务域一次性销毁"，
        ///                 不必要求业务把每个资源都 Release 一遍。
        ///   false       ：只卸载引用已归零的 —— 安全，但业务若忘了 Release，资源会残留。
        ///
        /// 【force = true 会不会误伤别的域？】
        ///   会 —— 前提是"该分组里混进了跨域共享的资源"。
        ///   对这类资源请打 RevResInstanceFlag.Resident 标志，它永不参与分组卸载（force 也不行）。
        /// </summary>
        /// <param name="group">要清理的业务分组</param>
        /// <param name="force">是否解除"还有人在用"的保护（默认 true）</param>
        public void Shutdown(RevResGroup group, bool force = true)
        {
            RevAsyncLoadPump.CancelGroup(group);       // ① 只中断该业务组的等待/在途加载
            RevResPreloader.Release(group);             // ② 归还该组的"预加载持有"引用
            RevResManager.UnloadGroup(group, force);    // ③ 按业务域批量卸载
            RevResManager.FlushUnused();                // ④ 真正释放未使用资源
        }

        /// <summary>全部释放（退出 / 回登录）</summary>
        public void ShutdownAll()
        {
            RevAsyncLoadPump.CancelAll();               // ① 中断所有等待/在途加载
            RevResPreloader.ReleaseAll();               // ② 归还全部预加载引用
            RevResManager.UnloadAll();                  // ③ 在旧策略仍有效时清缓存并释放包级引用
            _abLoader?.ReleaseAll();                    // ④ 最后释放主包/Manifest 与剩余包
        }
    }
}
