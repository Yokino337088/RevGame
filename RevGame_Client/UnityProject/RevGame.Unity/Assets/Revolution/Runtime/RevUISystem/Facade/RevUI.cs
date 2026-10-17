// ============================================================
// RevUI.cs —— UI 系统静态门面（**业务的唯一入口**）
//
// 位置：Runtime\RevUISystem\Facade\
//
// 【为什么业务只该认这个类】
//   管理器（RevUIManager）负责的是"怎么做"，门面负责"怎么说"。业务调门面，
//   将来换加载方式、换池策略、换层级实现，都不用动业务代码 —— 和框架里其它模块一个套路。
//
// 【最常用的三件事】
//     RevUI.OpenAsync<BagPanel>();                       // 打开（推荐：真机首次必须异步）
//     RevUI.Open<BagPanel>(panel => panel.SetData(d));   // 打开 + 拿到实例后做事
//     RevUI.Close<BagPanel>();                           // 关闭（也可以面板内 CloseSelf()）
//
// 【带数据的界面】
//     public sealed class BagPanel : RevUIPanel<BagData>
//     RevUI.Open<BagPanel, BagData>(data, panel => ...);   // 强类型传数据，不装箱
//
// 【不想写绑定字段？】直接重写 OnClick(节点名) / OnToggleChanged(节点名, 值)… 就行：
//     protected override void OnClick(string nodeName)
//     {
//         switch (nodeName)
//         {
//             case "btnClose":  CloseSelf(); break;
//             case "btnSort":   Sort();      break;
//         }
//     }
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>UI 系统门面：打开 / 关闭 / 查询 / 预热 / 诊断</summary>
    public static class RevUI
    {
        // ============================================================
        // 打开
        // ============================================================

        /// <summary>
        /// 同步打开：**只有"已经打开过"或"实例池里有"才能立刻拿到实例**（这条路径不加载任何资源）。
        /// ★ 需要加载时会返回 null 并报一条明确错误，请改用下面两个异步入口。
        ///   想让"第一次打开也同步成功"，先 <see cref="Preload{T}"/>（把预制体加载进缓存）。
        /// </summary>
        public static T Open<T>() where T : RevUIPanel
        {
            RevUIPanel panel = RevUIManager.Instance.OpenImmediate(typeof(T));

            if (panel == null)
                RevUILog.Error(
                    $"{typeof(T).Name} 无法同步打开：它还没被加载过。\n" +
                    $"  用这三种之一：① RevUI.Open<{typeof(T).Name}>(p => ...)（回调式，推荐）" +
                    $"② await RevUI.OpenAsync<{typeof(T).Name}>() ③ 先 RevUI.Preload<{typeof(T).Name}>() 再同步打开。\n" +
                    $"  （真机上预制体在 AB 包里，首次打开必然要异步 —— 这一点和资源系统一致。）");

            return panel as T;
        }

        /// <summary>回调式打开（推荐）。面板打开完成后回调；失败时回调收到 null。</summary>
        public static void Open<T>(Action<T> onOpened) where T : RevUIPanel
        {
            var callbacks = new List<Action<RevUIPanel>>(1);
            if (onOpened != null) callbacks.Add(panel => onOpened(panel as T));

            RevUIManager.Instance.OpenCore(typeof(T), null, callbacks);
        }

        /// <summary>带数据的回调式打开（带数据的面板用这个，数据强类型传递、不装箱）</summary>
        public static void Open<T, TData>(TData data, Action<T> onOpened = null) where T : RevUIPanel<TData>
        {
            var callbacks = new List<Action<RevUIPanel>>(1);
            if (onOpened != null) callbacks.Add(panel => onOpened(panel as T));

            RevUIManager.Instance.OpenCore(typeof(T), data, callbacks);
        }

        /// <summary>await 式打开（框架自带 RevTask，不依赖 async/await 之外的任何东西）</summary>
        public static RevTask<T> OpenAsync<T>() where T : RevUIPanel
        {
            RevTaskCompletionSource<T> source = RevTask<T>.CreateSource();

            var callbacks = new List<Action<RevUIPanel>>(1) { panel => Complete(source, panel) };
            RevUIManager.Instance.OpenCore(typeof(T), null, callbacks);

            return source.Task;
        }

        /// <summary>await 式打开（带数据）</summary>
        public static RevTask<T> OpenAsync<T, TData>(TData data) where T : RevUIPanel<TData>
        {
            RevTaskCompletionSource<T> source = RevTask<T>.CreateSource();

            var callbacks = new List<Action<RevUIPanel>>(1) { panel => Complete(source, panel) };
            RevUIManager.Instance.OpenCore(typeof(T), data, callbacks);

            return source.Task;
        }

        private static void Complete<T>(RevTaskCompletionSource<T> source, RevUIPanel panel) where T : RevUIPanel
        {
            if (panel is T typed)
            {
                source.SetResult(typed);
                return;
            }

            // 失败也走异常：调用方用 try/catch（或 RevTask 的错误回调）统一处理
            source.SetException(new InvalidOperationException(
                $"打开 {typeof(T).Name} 失败：资源加载不到、或预制体上没有该面板组件。" +
                $"（很可能就是资源路径/AB 标记的问题 —— 见打包工具「检查」页签）"));
        }

        // ============================================================
        // 关闭
        // ============================================================

        /// <summary>关闭指定面板（回池还是销毁由面板的 CacheMode 决定）</summary>
        public static bool Close(RevUIPanel panel) => RevUIManager.Instance.Close(panel);

        /// <summary>按类型关闭</summary>
        public static bool Close<T>() where T : RevUIPanel => RevUIManager.Instance.Close(typeof(T));

        /// <summary>关闭所有（可按层级）。切场景、回大厅时用。</summary>
        public static int CloseAll(RevUILayer? layer = null) => RevUIManager.Instance.CloseAll(layer);

        /// <summary>关闭某个互斥组当前占用的面板</summary>
        public static int CloseGroup(string group) => RevUIManager.Instance.CloseGroup(group);

        /// <summary>返回上一层（关掉最晚打开、且参与返回栈的面板）；配合 Android 返回键用</summary>
        public static bool Back() => RevUIManager.Instance.Back();

        /// <summary>全部清空：所有面板 + 实例池 + 根节点（回登录界面 / 切大版本）</summary>
        public static int ShutdownAll() => RevUIManager.Instance.ShutdownAll();

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>取已打开的面板（没开着返回 null）</summary>
        public static T Get<T>() where T : RevUIPanel => RevUIManager.Instance.Get<T>();

        /// <summary>这个面板现在开着吗</summary>
        public static bool IsOpen<T>() where T : RevUIPanel => RevUIManager.Instance.IsOpen<T>();

        /// <summary>某层最上面那个面板（判断"当前在哪个界面"用）</summary>
        public static RevUIPanel TopOf(RevUILayer layer) => RevUIManager.Instance.TopOf(layer);

        // ============================================================
        // 预热与扩展
        // ============================================================

        /// <summary>预热：只把预制体加载进资源缓存（不创建实例）。战斗/大场景前铺一下，进去后打开就是"零加载"。</summary>
        public static void Preload<T>(Action onLoaded = null) where T : RevUIPanel
            => RevUIManager.Instance.Preload(typeof(T), onLoaded);

        /// <summary>
        /// 把自定义控件接进"按节点名分发"。
        /// TMP_InputField、长按按钮、自研控件都用它 —— 注册一次，之后所有面板生效。
        ///
        ///     RevUI.RegisterAutoEvent&lt;LongPressButton&gt;((dispatch, btn) =&gt;
        ///     {
        ///         btn.onShortClick.AddListener(dispatch.Click);
        ///         btn.onLongPress.AddListener(dispatch.Click);
        ///     });
        /// </summary>
        public static void RegisterAutoEvent<T>(Action<RevUIEventDispatch, T> bind) where T : Component
            => RevUIBinder.RegisterAutoEvent(bind);

        /// <summary>一句话快照 + 明细（打开的界面、在途加载、实例池）</summary>
        public static string DumpStats() => RevUIManager.Instance.DumpStats();
    }
}
