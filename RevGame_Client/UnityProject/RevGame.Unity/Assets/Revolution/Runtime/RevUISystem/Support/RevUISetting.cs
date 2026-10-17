// ============================================================
// RevUISetting.cs —— UI 系统的全局配置与日志出口
//
// 位置：Runtime\RevUISystem\Support\
//
// 【为什么配置和日志放一个文件】
//   两者都是"整个系统唯一的那个口子"，且都很小；拆两个文件只会多一次跳转。
//   （框架里同样的处理：RevPool 的配置与日志口就放在一起。）
//
// 【只改这里，不要改框架代码】
//   参考分辨率、排序基准、默认缓存策略、同一面板最多缓存几个实例……
//   都做成静态可写字段，业务在启动时设一次即可（和 RevPool.MaxIdlePerPool 一个套路）。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>根 Canvas 使用哪套设计方向；只选择 UI 缩放方案，设备屏幕方向由项目/平台决定。</summary>
    public enum RevUICanvasOrientation
    {
        /// <summary>跟随实际屏幕宽高；在手机旋转和编辑器 Game 视图切换时自动调整。</summary>
        Auto,
        /// <summary>固定使用横屏设计尺寸，不随屏幕旋转改变。</summary>
        Landscape,
        /// <summary>固定使用竖屏设计尺寸，不随屏幕旋转改变。</summary>
        Portrait,
        /// <summary>完全使用 Canvas 预制体（或代码兜底）的原始 CanvasScaler 配置。</summary>
        UseDesign,
    }

    /// <summary>UI 系统的全局配置（启动时设一次；不设就用这里的默认值）</summary>
    public static class RevUISetting
    {
        /// <summary>设计分辨率（默认 1920×1080）—— 只在"框架自己创建 Canvas"时生效</summary>
        public static Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        /// <summary>CanvasScaler 的宽高匹配权重（0 = 按宽、1 = 按高，0.5 = 折中，默认）</summary>
        public static float MatchWidthOrHeight = 0.5f;

        /// <summary>
        /// 选择 UI 设计方向（默认 Auto：按实际宽高适配）。项目可以在首次打开/预加载 UI 前
        /// 显式设为 Landscape 或 Portrait；UseDesign 表示不改预制体的 CanvasScaler。
        /// 这只控制 UI 设计尺寸，不修改 Screen.orientation（原生应用/小游戏由各自平台设置）。
        /// </summary>
        public static RevUICanvasOrientation CanvasOrientation = RevUICanvasOrientation.Auto;

        /// <summary>从横屏设计切到竖屏时的匹配值（0 = 按宽，默认）；原设计方向保持预制体的匹配值。</summary>
        public static float PortraitMatchWidthOrHeight = 0f;

        /// <summary>UI Canvas 的排序基准（默认 100，避免和别的 Canvas 抢 0）</summary>
        public static int SortOrderBase = 100;

        /// <summary>
        /// （★ 预留参数，当前架构未接入）层与层的上下关系现在靠"画布内子节点顺序"决定，
        /// 三 Canvas 的子画布排序用固定的根−1/根−2 —— 调整这个值不会有任何效果。
        /// 接入前请勿依赖它。
        /// </summary>
        public static int LayerStep = 50;

        /// <summary>面板没声明 CacheMode 时用的默认策略（默认 KeepAlive：关闭进池复用）</summary>
        public static RevUICacheMode DefaultCacheMode = RevUICacheMode.KeepAlive;

        /// <summary>
        /// 同一个面板最多缓存几个实例（默认 1）。
        /// ★ 一个面板同时只会开一份，缓存 1 个就够了；调大只在"确实要预创建多份"的特殊场景有意义。
        /// </summary>
        public static int MaxCachedPanels = 1;

        /// <summary>点击弹窗遮罩时，是否自动关掉最上面的弹窗（默认 true）</summary>
        public static bool ClickMaskClosesTop = true;

        /// <summary>
        /// 按住多久算"长按"（秒，默认 0.5）—— <c>[RevButtonLongPress]</c> 用它判定。
        /// ★ 计时用 <c>Time.unscaledTime</c>：暂停（timeScale = 0）时也照常判定。
        /// </summary>
        public static float ButtonLongPressSeconds = 0.5f;

        /// <summary>
        /// 绑定失败（找不到节点 / 组件类型对不上）时是否按错误处理（默认 true）。
        /// 关掉后只告警 —— 不建议关：这类问题在真机上表现为"按钮没反应"，极难定位。
        /// </summary>
        public static bool BindFailureIsError = true;

        /// <summary>是否输出诊断信息（打开/关闭/缓存命中等，默认关；排查时打开）</summary>
        public static bool VerboseLog;

        /// <summary>
        /// UI 动画总开关（默认开）。关掉后：面板 / Part 的显示隐藏动画与控件反馈**直接写终态**，
        /// 业务代码一行都不用改（做"关闭动效"的无障碍选项 / 低端机降级时用它）。
        /// </summary>
        public static bool UIAnimationsEnabled = true;

        // ============================================================
        // Canvas / 相机（渲染模式）—— 文档 4.9、4.11
        // ============================================================

        /// <summary>
        /// UI 根 Canvas 的渲染模式（**默认 <see cref="RevUICanvasMode.ScreenSpaceOverlay"/>**：
        /// 不需要相机、UI 永远最上层，绝大多数项目要的就是这个）。
        /// ★ 要在 UI 上摆 3D 模型 / 粒子、或要把 UI 渲进 RenderTexture，就设成 ScreenSpaceCamera
        ///   （配合下面的 CanvasPrefabPath / UICameraPrefabPath / UICamera）。
        /// ★ Auto = 跟随 Canvas 预制体；想"在预制体里调渲染模式"就用 Auto。
        /// </summary>
        public static RevUICanvasMode CanvasMode = RevUICanvasMode.ScreenSpaceOverlay;

        /// <summary>
        /// Canvas 架构（默认且推荐 <see cref="RevUICanvasArchitecture.Single"/>：所有面板一个 Canvas）。
        /// ★ 先在目标设备上用 Profiler 确认 UI 合批已成为瓶颈、常规优化仍不达标，才考虑启用 Split；
        ///   不要仅因为有动态 UI 就提前切换。Split = 三 Canvas 动静分离：常用（根 Canvas，所有层级）/ 静态（根 − 2）/ 动态（根 − 1），
        ///   面板用 <c>[RevUIPanel(..., CanvasType = RevUICanvasType.Dynamic)]</c> 选画布（静态 / 动态只收 Scene 层）。
        /// ★ 在**第一次打开面板之前**设（根节点创建时读一次）；运行中要换，先 <c>RevUI.ShutdownAll()</c> 再开。
        /// </summary>
        public static RevUICanvasArchitecture CanvasArchitecture = RevUICanvasArchitecture.Single;

        /// <summary>
        /// Canvas 预制体在 <c>Resources</c> 下的路径（**默认 "RevUIPrefab/RevUICanvas"**，框架自带一份）。
        /// ★ 默认就用它来渲染（Overlay 模式 / 1920×1080 / match 0.5 / sortingOrder 100）；
        ///   载不到（缺失或路径不对）时会自动**代码兜底建一个**，并给一条 Warning —— 不会让整屏 UI 起不来。
        /// ★ 置空 = 完全用代码建。预制体里**不要**放六个层级节点（框架会自己建），放你自己的背景层之类的没问题。
        /// </summary>
        public static string CanvasPrefabPath = "RevUIPrefab/RevUICanvas";

        /// <summary>
        /// UI 相机预制体在 <c>Resources</c> 下的路径（**默认 "RevUIPrefab/RevUICamera"**，框架自带一份）。
        /// ★ 只在 ScreenSpaceCamera 模式下才会被加载（Overlay 模式用不到相机，零开销）。
        /// ★ 如果这里和 <see cref="UICamera"/> 都拿不到，框架会按标准参数兜底建一台。
        /// </summary>
        public static string UICameraPrefabPath = "RevUIPrefab/RevUICamera";

        /// <summary>显式指定的 UI 相机（优先级最高；ScreenSpaceCamera 模式下用）</summary>
        public static Camera UICamera;

        /// <summary>Camera 模式下 Canvas 平面到相机的距离（默认 100，必须落在相机近/远裁剪面之间）</summary>
        public static float CanvasPlaneDistance = 100f;

        /// <summary>
        /// UI 对象统一使用的图层名（默认 "UI" = Unity 内置第 5 号图层）。
        /// ★ Camera 模式下相机的 Culling Mask **必须**包含它，否则 UI 一个都渲染不出来 ——
        ///   框架会把根节点 / 六层挂点 / 每个面板实例 / 每个 Part / 遮罩都设成这个图层，你只要保证相机认它；
        /// ★ 设成空字符串 = 框架不改图层（用你在预制体里设的）。
        /// </summary>
        public static string UILayerName = "UI";

        /// <summary>
        /// 场景里一个 EventSystem 都没有时，框架是否兜底建一个（默认 true）。
        /// ★ 关掉它的场合：项目自己全权管 EventSystem（自带 InputActionAsset 的预制体、或必须在特定时机才创建）。
        ///   关掉后框架一行都不碰 —— 但请保证你自己的 EventSystem **早于第一次打开面板**存在（见文档 4.10）。
        /// </summary>
        public static bool AutoCreateEventSystem = true;
    }

    /// <summary>
    /// 统一日志出口：框架里所有报错都从这里出，业务可以接管（例如转成自己的日志系统）。
    /// 另有 <see cref="Guard"/>：执行业务钩子并隔离异常 ——
    /// 一个面板的 OnRefreshView 抛异常，不该把管理器的打开流程带崩（和事件系统的异常隔离同一原则）。
    /// </summary>
    internal static class RevUILog
    {
        /// <summary>错误出口（默认走框架日志系统 RevLog，tag = UI）</summary>
        public static Action<string> Error = msg => RevLog.Error(msg, "UI");

        /// <summary>告警出口（同上）</summary>
        public static Action<string> Warning = msg => RevLog.Warn(msg, "UI");

        public static void Info(string msg)
        {
            if (RevUISetting.VerboseLog) RevLog.Info(msg, "UI");   // 开关：RevUISetting.VerboseLog
        }

        /// <summary>执行一个业务钩子：抛异常只报错，不影响调用方流程</summary>
        public static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Error($"{what} 抛异常（已隔离，不影响其它流程）：{e}");
            }
        }
    }
}
