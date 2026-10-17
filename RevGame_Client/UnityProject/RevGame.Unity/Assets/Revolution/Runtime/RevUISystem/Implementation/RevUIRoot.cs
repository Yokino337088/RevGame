// ============================================================
// RevUIRoot.cs —— UI 根节点（Canvas + 各层级挂点 + 每帧维护）
//
// 位置：Runtime\RevUISystem\Implementation\
//
// 【根节点从哪来（两条路，默认走第一条）】
//   ① 代码建（默认）：Canvas + CanvasScaler + GraphicRaycaster + 每层一个空节点 ——
//      零资源依赖、零配置，业务侧只需要关心自己的面板预制体；
//   ② 用你在 Resources 里的 Canvas 预制体（RevUISetting.CanvasPrefabPath，示例 "RevUIPrefab/RevUICanvas"）：
//      预制体提供 Canvas 上的可视化配置（缩放、sortingOrder、你自己的背景层…），
//      框架负责挂六层节点、按需应用渲染模式、并把 UI 相机接上。
//
// 【渲染模式可配（RevUISetting.CanvasMode）】
//   默认 ScreenSpaceOverlay（不需要相机、永远最上层，绝大多数项目要的就是它）；
//   设 Auto = 跟随 Canvas 预制体里选的模式（没预制体也是 Overlay）。
//   选 ScreenSpaceCamera 时需要一台 UI 相机 —— 优先级：
//     RevUISetting.UICamera → UICameraPrefabPath 预制体 → 场景里叫 UICamera 的 → 代码兜底建一台。
//   这条路换来的是"UI 可被 3D 遮挡 / 能进 RenderTexture / 3D 模型与粒子可渲染在 UI 之上"。
//
// 【图层：Camera 模式的隐形前提】
//   相机的 Culling Mask 只认 RevUISetting.UILayerName（默认 "UI"），所以框架创建的一切
//   （根节点 / 六层挂点 / 面板实例 / Part / 遮罩）都会被设到这个图层 ——
//   否则症状是"面板明明开了，但屏幕上一个都没有"。
//
// 【层级是怎么定的】
//   每个 RevUILayer 一个子节点（名字就是枚举名，方便在 Hierarchy 里看）：
//     [RevUIRoot]      ← Canvas（DontDestroyOnLoad，切场景不丢）
//       ├ Scene / Normal / Popup / Toast / Guide / Top   ← 面板按层级往这些节点下挂
//   层与层之间靠 Canvas 内部的渲染顺序（子节点顺序）决定，所以"上层一定盖住下层"是结构保证的，
//   不需要每个面板自己去设 sortingOrder（这也是原框架最容易被改乱的地方）。
//
// 【两种 Canvas 架构（RevUISetting.CanvasArchitecture）】
//   Single（默认）：就是上面那张图 —— 所有面板都在根 Canvas 里。
//   Split（三 Canvas 动静分离）：根 Canvas 本身就是"常用画布"（六层挂点不变），再多建两个子画布：
//     [RevUIRoot]            ← 常用画布（sortingOrder = 根）
//       ├ [StaticCanvas]     ← 静态画布（override，根 − 2）：Scene 层里内容基本不变的面板
//       ├ [DynamicCanvas]    ← 动态画布（override，根 − 1）：Scene 层里每帧 / 每秒在变的面板
//       └ Scene / Normal / Popup / Toast / Guide / Top
//   ★ 常用画布必须在最上面：弹窗遮罩、引导、断线重连要盖住（并挡住）一切，包括 HUD 上的摇杆。
//   ★ 两个子画布各自带 GraphicRaycaster：UGUI 的 Graphic 只登记到离自己最近的 Canvas，没有它就点不到。
//
// 【每帧只做两件轻活】
//   ① Update：把"关闭待销毁"的面板真正销毁（延迟一帧：避免在遍历/事件回调里销毁对象）；
//   ② LateUpdate：让管理器处理"层内排序"脏标记（打开/关闭时只打标记，不立即重排 —— 王者文档里点过这条）。
//      放在 LateUpdate：本帧 Update / 点击回调里开关的面板，都在**同一帧渲染前**排好。
//   除此之外完全不做事（没有每帧遍历、没有每帧分配）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>UI 根节点：Canvas、层级挂点、遮罩、延迟销毁</summary>
    [DisallowMultipleComponent]
    internal sealed class RevUIRoot : MonoBehaviour
    {
        private Canvas _canvas;
        private bool _fromPrefab;                       // Canvas 来自预制体？（决定要不要用 RevUISetting 的值覆盖它的配置）
        private CanvasScaler _scaler;
        private Vector2 _designResolution;              // 保存预制体/代码创建时的原始设计尺寸，旋转回去必须恢复它
        private float _designMatch;                     // 同上：横屏原有的宽高匹配权重不能被竖屏设置覆盖
        private bool _designPortrait;
        private bool? _appliedPortrait;                 // 上一次应用的方向；未应用为 null
        private Camera _uiCamera;                       // Camera 模式用的 UI 相机（Overlay / WorldSpace 时为 null）
        private readonly Dictionary<RevUILayer, RectTransform> _layers = new Dictionary<RevUILayer, RectTransform>();
        private readonly Dictionary<RevUICanvasType, Canvas> _splitCanvases = new Dictionary<RevUICanvasType, Canvas>();
        private readonly List<RevUIPanel> _pendingRelease = new List<RevUIPanel>();
        private readonly RevUIPopupMask _mask = new RevUIPopupMask();

        /// <summary>UI Canvas（业务要加自定义 Canvas 时可以用它当父节点）；三 Canvas 架构下它就是"常用画布"</summary>
        public Canvas Canvas => _canvas;

        /// <summary>这个根节点用的 Canvas 架构（创建时从 RevUISetting 读一次，之后不变）</summary>
        public RevUICanvasArchitecture Architecture { get; private set; } = RevUICanvasArchitecture.Single;

        /// <summary>取某个画布（常用 = 根 Canvas；静态 / 动态只在三 Canvas 架构下存在，否则返回 null）</summary>
        public Canvas GetCanvas(RevUICanvasType type)
        {
            if (type == RevUICanvasType.Common) return _canvas;
            return _splitCanvases.TryGetValue(type, out Canvas c) && c != null ? c : null;
        }

        /// <summary>这个根节点用的是不是"预制体方案"（CanvasPrefabPath 载到并实例化了）</summary>
        public bool FromPrefab => _fromPrefab;

        /// <summary>当前使用的 UI 相机（仅 ScreenSpaceCamera 模式有值）</summary>
        public Camera UICamera => _uiCamera;

        /// <summary>遮罩管理器</summary>
        public RevUIPopupMask Mask => _mask;

        // ============================================================
        // 创建
        // ============================================================

        /// <summary>创建 UI 根节点（框架在第一次打开面板时调用，业务不需要手动建）</summary>
        public static RevUIRoot Create()
        {
            GameObject go = null;
            bool fromPrefab = false;

            // ① 配了 Canvas 预制体就用它（可视化调好的缩放 / 排序 / 自定义子节点都在里面）
            string canvasPath = RevUISetting.CanvasPrefabPath;
            if (!string.IsNullOrEmpty(canvasPath))
            {
                GameObject prefab = Resources.Load<GameObject>(canvasPath);
                if (prefab != null)
                {
                    go = Instantiate(prefab);
                    go.name = "[RevUIRoot]";                // 名字统一，方便在 Hierarchy 里一眼认出它
                    fromPrefab = true;
                }
                else
                {
                    // ★ 兜底：预制体缺失（没入库 / 路径改了 / 被裁掉）也不能让整屏 UI 起不来 ——
                    //   照常走下面的"代码建"，但把原因说清，免得业务怀疑"我配了预制体怎么没生效"。
                    RevUILog.Warning(
                        $"Canvas 预制体载不到：Resources/{canvasPath}（缺失或路径不对）→ 已改为代码兜底创建" +
                        "（功能等价，只是少了你预制体里的自定义）。检查路径是否写对、文件是否真在 Resources 下；" +
                        "不想要预制体就把 RevUISetting.CanvasPrefabPath 置空。");
                }
            }

            // ② 没预制体（或载不到）：代码建 —— 零资源依赖
            if (go == null)
            {
                go = new GameObject("[RevUIRoot]",
                    typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(RevUIRoot));
            }

            DontDestroyOnLoad(go);                          // 切场景不丢：UI 根节点必须常驻

            RevUIRoot root = go.GetComponent<RevUIRoot>();
            if (root == null) root = go.AddComponent<RevUIRoot>();    // 预制体里不会挂这个 internal 脚本，这里补
            root._fromPrefab = fromPrefab;
            root.Build();
            return root;
        }

        private void Build()
        {
            // ① Canvas 与它的两个必需组件：预制体里可能已配好，缺什么补什么
            //    （预制体只用 Unity 内置组件，所以不存在"脚本丢了"的问题；真缺了就补一份默认的）
            _canvas = GetComponent<Canvas>();
            if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
            _scaler = GetComponent<CanvasScaler>();
            if (_scaler == null) _scaler = gameObject.AddComponent<CanvasScaler>();
            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

            // ② 渲染模式（Auto：预制体里选了什么就用什么；没预制体就是 Overlay）
            ApplyRenderMode();

            // ③ 缩放与排序基准：只有"代码建"时才用 RevUISetting 的值 ——
            //    预制体存在的意义就是让你在 Inspector 里直接调这些，所以不覆盖它。
            if (!_fromPrefab)
            {
                _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                _scaler.referenceResolution = RevUISetting.ReferenceResolution;
                _scaler.matchWidthOrHeight = RevUISetting.MatchWidthOrHeight;

                _canvas.sortingOrder = RevUISetting.SortOrderBase;
            }

            // ★ 不改预制体的 Inspector 配置：以它（或代码兜底）的初始值为设计基准。
            //   原版是 1920×1080：竖屏换位到 1080×1920，返回横屏再恢复 1920×1080 / match 0.5。
            _designResolution = _scaler.referenceResolution;
            _designMatch = _scaler.matchWidthOrHeight;
            _designPortrait = _designResolution.y > _designResolution.x;
            ApplyScreenOrientation();

            // ④ 每个层级一个空节点（名字用枚举名：看 Hierarchy 就知道哪层有哪些面板）
            foreach (RevUILayer layer in RevUILayerUtil.All)
            {
                var layerGo = new GameObject(layer.ToString(), typeof(RectTransform));
                RectTransform rt = (RectTransform)layerGo.transform;
                rt.SetParent(transform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                _layers[layer] = rt;
            }

            // ⑤ 三 Canvas 架构：再建静态 / 动态两个子画布（根 Canvas 自己就是常用画布）
            Architecture = RevUISetting.CanvasArchitecture;
            if (Architecture == RevUICanvasArchitecture.Split)
            {
                CreateSplitCanvas(RevUICanvasType.Static);
                CreateSplitCanvas(RevUICanvasType.Dynamic);
            }

            // ⑥ 统一图层（含刚建好的六层节点与子画布）—— Camera 模式下这是"能不能被渲染出来"的前提
            ApplyUILayer(gameObject);

            EnsureEventSystem();
            RevUILog.Info($"UI 根节点已创建（{_canvas.renderMode}" +
                          (_fromPrefab ? "，来自 Canvas 预制体" : "，代码建") +
                          (Architecture == RevUICanvasArchitecture.Split ? "，三 Canvas 动静分离" : "，单 Canvas") +
                          " + 六层挂点）");
        }

        /// <summary>
        /// 建一个子画布（静态 / 动态）：override 排序（根 − 2 / 根 − 1，所以整体排在常用画布之下）+ 自己的 GraphicRaycaster。
        /// ★ 先挂到根下再加 Canvas：嵌套 Canvas 的 overrideSorting 以"已有父 Canvas"为前提。
        /// </summary>
        private void CreateSplitCanvas(RevUICanvasType type)
        {
            var go = new GameObject($"[{type}Canvas]", typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(transform, false);
            rt.SetSiblingIndex(RevUILayerUtil.CanvasStackIndex(type));   // Hierarchy 里按"从下到上"排在六层挂点前面
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Canvas canvas = go.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingLayerID = _canvas.sortingLayerID;
            canvas.sortingOrder = RevUILayerUtil.CanvasSortingOrder(_canvas.sortingOrder, type);
            canvas.additionalShaderChannels = _canvas.additionalShaderChannels;   // TMP 等要用的通道与根一致

            go.AddComponent<GraphicRaycaster>();
            _splitCanvases[type] = canvas;
        }

        /// <summary>
        /// 面板挂在哪：静态 / 动态画布（三 Canvas 架构）或常用画布里对应层级的挂点。
        /// </summary>
        public Transform GetPanelParent(RevUILayer layer, RevUICanvasType type)
        {
            Canvas split = type == RevUICanvasType.Common ? null : GetCanvas(type);
            return split != null ? split.transform : GetLayer(layer);
        }

        /// <summary>
        /// 兜底创建 EventSystem：**仅当场景里一个都没有、且 <see cref="RevUISetting.AutoCreateEventSystem"/> 打开时**才建。
        /// ★ 只建是为了"新工程忘了摆 EventSystem 就整个 UI 点不动"这个常见事故；
        ///   项目里已有自己的（比如挂了新输入系统的模块），这里不会插手。
        ///
        /// ★ 输入模块的选法（这里最容易踩坑）：
        ///   优先新输入系统的 <c>InputSystemUIInputModule</c>，找不到它才退回旧版 <c>StandaloneInputModule</c>。
        ///   原因：Active Input Handling = "Input System Package (New)" 的工程里，旧模块每帧读 <c>Input.mousePosition</c>
        ///   会抛 InvalidOperationException —— 症状是"UI 完全点不动 + 控制台刷异常"，很难往输入模块上想。
        /// </summary>
        private static void EnsureEventSystem()
        {
            if (!RevUISetting.AutoCreateEventSystem)
            {
                RevUILog.Info("AutoCreateEventSystem = false：框架不建 EventSystem（由项目自己管）。");
                return;
            }

            // 必须包含未激活对象：场景里已有但暂时禁用的 EventSystem 后续可能启用，
            // 此时再造一个 DontDestroyOnLoad EventSystem 就会产生重复输入系统。
            if (FindObjectOfType<EventSystem>(true) != null) return;

            var go = new GameObject("[RevUIEventSystem]", typeof(EventSystem));
            DontDestroyOnLoad(go);

            string module = AddInputModule(go);
            RevUILog.Info($"场景里没有 EventSystem，已自动创建（输入模块：{module}）");
        }

        /// <summary>
        /// 给 EventSystem 挂输入模块：优先新输入系统，退回旧版。返回值只用于日志（用得是哪个）。
        ///
        /// ★ 为什么用反射而不是直接写类型：新输入系统是**可选包**，
        ///   直接 <c>using UnityEngine.InputSystem</c> 会让"没装这个包的工程"编译不过。
        /// </summary>
        private static string AddInputModule(GameObject go)
        {
            Type modern = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule", "Unity.InputSystem");
            if (modern != null)
            {
                try
                {
                    // ★ AddComponent 就够了：该模块会在自己的 OnEnable 里自动分配 DefaultInputActions
                    //   （官方文档原话：programmatically added → automatically receive the default actions as part of its OnEnable）
                    if (go.AddComponent(modern) != null)
                        return "InputSystemUIInputModule（新输入系统，已自动分配默认操作）";
                }
                catch (Exception e)
                {
                    RevUILog.Warning($"挂 InputSystemUIInputModule 失败（{e.GetType().Name}）→ 退回旧版输入模块。");
                }
            }

            go.AddComponent<StandaloneInputModule>();

#if !ENABLE_LEGACY_INPUT_MANAGER
            RevUILog.Warning(
                "工程没有启用旧版 Input（Active Input Handling = Input System Package (New)），" +
                "也没找到新输入系统的 UI 模块 —— 这个 EventSystem 会每帧抛异常、UI 点不动。二选一：" +
                "① Player Settings → Active Input Handling 改成 Both / Input Manager (Old)；" +
                "② 自己摆一个带 InputSystemUIInputModule 的 EventSystem，并把 RevUISetting.AutoCreateEventSystem 关掉。");
#endif
            return "StandaloneInputModule（旧版 Input）";
        }

        /// <summary>
        /// 按"完整类型名 + 程序集名"找类型；找不到就扫一遍已加载的程序集（程序集名随版本变过）。
        /// ★ 专供可选包使用：装了就自动用上，没装也不会让工程编译不过。
        /// </summary>
        private static Type FindType(string fullName, string assemblyName)
        {
            Type type = Type.GetType($"{fullName}, {assemblyName}");
            if (type != null) return type;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                if ((type = asm.GetType(fullName)) != null) return type;

            return null;
        }

        // ============================================================
        // 渲染模式与相机（RevUISetting.CanvasMode）
        // ============================================================

        /// <summary>
        /// 应用渲染模式。
        /// ★ Camera 模式要接的线其实只有两条：<c>canvas.worldCamera</c> 和 <c>canvas.planeDistance</c>。
        ///   点击**不需要额外接线** —— GraphicRaycaster 的 <c>eventCamera</c> 是**只读**的，
        ///   它自动取 <c>canvas.worldCamera</c>（ugui 源码：<c>m_EventCamera ?? canvas.worldCamera</c>），
        ///   所以"把 worldCamera 设对"这一件事同时管住了**显示**与**点击**。
        ///   （只有当工程里另加了 PhysicsRaycaster / 自定义 Raycaster 时，才需要单独关心它们的 eventCamera。）
        /// </summary>
        private void ApplyRenderMode()
        {
            RevUICanvasMode mode = RevUISetting.CanvasMode;

            if (mode == RevUICanvasMode.Auto)
            {
                if (!_fromPrefab) return;                  // 代码建的 Canvas 默认就是 Overlay，什么都不用做
                mode = _canvas.renderMode == RenderMode.ScreenSpaceCamera ? RevUICanvasMode.ScreenSpaceCamera
                     : _canvas.renderMode == RenderMode.WorldSpace      ? RevUICanvasMode.WorldSpace
                     : RevUICanvasMode.ScreenSpaceOverlay;
                RevUILog.Info($"渲染模式跟随 Canvas 预制体：{_canvas.renderMode}");
            }

            switch (mode)
            {
                case RevUICanvasMode.WorldSpace:
                    _canvas.renderMode = RenderMode.WorldSpace;
                    _canvas.worldCamera = null;
                    break;

                case RevUICanvasMode.ScreenSpaceCamera:
                {
                    Camera cam = ResolveUICamera();
                    if (cam == null)
                    {
                        // 拿不到相机 → 退回 Overlay：宁可"不是你要的模式"，也不能"整屏 UI 全不见"
                        RevUILog.Error(
                            "CanvasMode = ScreenSpaceCamera，但一台 UI 相机都找不到（UICamera / UICameraPrefabPath / " +
                            "场景里名为 UICamera 的相机都没有）→ 已退回 ScreenSpaceOverlay。");
                        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                        _canvas.worldCamera = null;
                        break;
                    }

                    _canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    _canvas.worldCamera = cam;
                    _canvas.planeDistance = RevUISetting.CanvasPlaneDistance;
                    _uiCamera = cam;

                    // 点击这条线不用管：GraphicRaycaster.eventCamera 是只读的，自动取 canvas.worldCamera
                    // （ugui 源码：m_EventCamera ?? canvas.worldCamera）—— worldCamera 设对了，点击就对。

                    RevUILog.Info($"UI 使用 ScreenSpaceCamera：{cam.name}" +
                                  $"（planeDistance={_canvas.planeDistance}，CullingMask={cam.cullingMask}）");
                    break;
                }

                default:
                    _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    _canvas.worldCamera = null;
                    break;
            }
        }

        /// <summary>
        /// 找 UI 相机，按优先级：
        ///   ① <see cref="RevUISetting.UICamera"/>（代码显式指定）
        ///   ② <c>Resources</c> 里的相机预制体（<see cref="RevUISetting.UICameraPrefabPath"/>，示例 "RevUIPrefab/RevUICamera"）
        ///   ③ 场景里名字是 <c>UICamera</c> / <c>[RevUICamera]</c> 的相机
        ///   ④ 按标准参数代码建一台（正交 / Depth clear / depth 100 / 只渲染 UI 图层）
        /// </summary>
        private static Camera ResolveUICamera()
        {
            if (RevUISetting.UICamera != null) return RevUISetting.UICamera;

            Camera fromPrefab = LoadCameraFromPrefab();
            if (fromPrefab != null) return fromPrefab;

            foreach (Camera cam in FindObjectsOfType<Camera>())
                if (cam != null && (cam.name == "UICamera" || cam.name == "[RevUICamera]")) return cam;

            return CreateFallbackCamera();
        }

        private static Camera LoadCameraFromPrefab()
        {
            string path = RevUISetting.UICameraPrefabPath;
            if (string.IsNullOrEmpty(path)) return null;

            GameObject prefab = Resources.Load<GameObject>(path);
            if (prefab == null)
            {
                RevUILog.Warning($"UI 相机预制体载不到：Resources/{path} → 继续找场景里的相机。");
                return null;
            }

            GameObject go = Instantiate(prefab);
            go.name = "[RevUICamera]";
            DontDestroyOnLoad(go);

            Camera cam = go.GetComponentInChildren<Camera>(true);
            if (cam == null)
            {
                RevUILog.Warning($"UI 相机预制体 Resources/{path} 里没有 Camera 组件 → 已忽略它。");
                DestroyObject(go);
                return null;
            }

            RevUILog.Info($"UI 相机来自预制体：Resources/{path}（{cam.name}）");
            return cam;
        }

        /// <summary>
        /// 兜底建一台标准 UI 相机。
        /// ★ 这几个参数都不是随便写的：正交（否则 UI 会有透视变形）、Depth clear（叠在主相机之上、不覆盖画面）、
        ///   depth 100（排在主相机之后）、只渲染 UI 图层（不把 3D 场景再画一遍）。
        /// </summary>
        private static Camera CreateFallbackCamera()
        {
            var go = new GameObject("[RevUICamera]", typeof(Camera));

            Camera cam = go.GetComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.clearFlags = CameraClearFlags.Depth;
            cam.depth = 100f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.cullingMask = UILayerMask();
            cam.transform.position = new Vector3(0f, 0f, -RevUISetting.CanvasPlaneDistance);

            DontDestroyOnLoad(go);
            RevUILog.Warning(
                "没有可用的 UI 相机，已按标准参数兜底建了一台（正交 / Depth clear / depth 100 / 只渲染 UI 图层）。" +
                "建议换成你自己的相机：RevUISetting.UICamera = cam，或配 RevUISetting.UICameraPrefabPath。");
            return cam;
        }

        // ============================================================
        // 图层（Camera 模式的隐形前提）
        // ============================================================

        /// <summary>UI 图层索引；-1 = 没配（空字符串）或工程里没有这个图层</summary>
        internal static int UILayerIndex()
        {
            string name = RevUISetting.UILayerName;
            if (string.IsNullOrEmpty(name)) return -1;

            int layer = LayerMask.NameToLayer(name);
            if (layer < 0)
                RevUILog.Warning($"图层 \"{name}\" 在工程里不存在 → UI 保持各自原有的图层" +
                                 "（Camera 模式下相机认不到就渲染不出来）。");
            return layer;
        }

        /// <summary>UI 图层掩码（给相机的 Culling Mask 用）；没配图层时返回"全部"</summary>
        internal static int UILayerMask()
        {
            int layer = UILayerIndex();
            return layer < 0 ? ~0 : 1 << layer;
        }

        /// <summary>
        /// 把 UI 对象（含其所有子节点）设到 <see cref="RevUISetting.UILayerName"/> 图层。
        /// ★ 面板 / Part / 遮罩实例化后都要调它 —— 相机的 Culling Mask 只认这个图层。
        /// </summary>
        internal static void ApplyUILayer(GameObject go)
        {
            int layer = UILayerIndex();
            if (layer >= 0) SetLayerRecursively(go, layer);
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;

            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)      // 用下标遍历：避免 foreach 的枚举器分配
                SetLayerRecursively(t.GetChild(i).gameObject, layer);
        }

        /// <summary>取某层的挂点（面板都挂在对应层节点下）</summary>
        public RectTransform GetLayer(RevUILayer layer)
            => _layers.TryGetValue(layer, out RectTransform rt) && rt != null ? rt : (RectTransform)transform;

        // ============================================================
        // 延迟销毁
        // ============================================================

        /// <summary>登记"关闭后要销毁"的面板（下一帧才真销毁，避免在遍历/回调里删对象）</summary>
        public void AddPendingRelease(RevUIPanel panel)
        {
            if (panel != null) _pendingRelease.Add(panel);
        }

        /// <summary>立刻处理完待销毁队列（退出 / 切大版本时用，免得还有一帧没跑到）</summary>
        public void FlushPendingRelease() => ProcessPendingRelease();

        private void ProcessPendingRelease()
        {
            if (_pendingRelease.Count == 0) return;

            for (int i = 0; i < _pendingRelease.Count; i++)
            {
                RevUIPanel panel = _pendingRelease[i];
                if (panel == null) continue;

                panel.InternalRelease();

                // ★ 与创建时的资源租约配对：实例销毁 = 归还一份引用。
                //   管理器以 instance ID 保留路径快照，即使外部已使 Unity 引用变成假 null 也不会漏/重复释放。
                RevUIManager.Instance.ReleasePanelResource(panel);

                DestroyObject(panel.gameObject);
            }

            _pendingRelease.Clear();
        }

        /// <summary>销毁 GameObject：运行中用 Destroy，编辑器（非运行）用 DestroyImmediate</summary>
        internal static void DestroyObject(GameObject go)
        {
            if (go == null) return;

            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        // ============================================================
        // 每帧
        // ============================================================

        private void Update()
        {
            ProcessPendingRelease();
            ApplyScreenOrientation();
        }

        /// <summary>
        /// 按 RevUISetting.CanvasOrientation 选择 UI 设计方向：Auto 才读取屏幕宽高，
        /// Landscape / Portrait 由项目显式指定；UseDesign 保留预制体原值。仅在方向变化时写 CanvasScaler。
        /// ★ 只处理缩放，不擅自修改 Screen.orientation 或面板锚点；全屏遮罩仍挂在全屏 Canvas 下。
        /// </summary>
        private void ApplyScreenOrientation()
        {
            if (_scaler == null ||
                _scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize ||
                _scaler.screenMatchMode != CanvasScaler.ScreenMatchMode.MatchWidthOrHeight ||
                _designResolution.x <= 0f || _designResolution.y <= 0f) return;

            RevUICanvasOrientation mode = RevUISetting.CanvasOrientation;
            bool portrait;
            switch (mode)
            {
                case RevUICanvasOrientation.Portrait:
                    portrait = true;
                    break;
                case RevUICanvasOrientation.Landscape:
                    portrait = false;
                    break;
                case RevUICanvasOrientation.UseDesign:
                    portrait = _designPortrait;
                    break;
                default:
                    // 编辑器 Game 视图也可测试；宽高暂未准备好或正方形时沿用上一次方向。
                    if (Screen.width <= 0 || Screen.height <= 0 || Screen.width == Screen.height) return;
                    portrait = Screen.height > Screen.width;
                    break;
            }
            if (_appliedPortrait == portrait) return;

            // 屏幕方向与原始设计方向一致时恢复 Inspector 原值；不一致时才换位。
            _scaler.referenceResolution = portrait == _designPortrait
                ? _designResolution
                : new Vector2(_designResolution.y, _designResolution.x);
            _scaler.matchWidthOrHeight = portrait && !_designPortrait
                ? RevUISetting.PortraitMatchWidthOrHeight : _designMatch;
            _appliedPortrait = portrait;
        }

        // 层内排序 / 遮罩 / 被盖住通知（脏标记驱动，多半是空转）；LateUpdate = 本帧所有开关都已发生、渲染还没开始
        private void LateUpdate() => RevUIManager.Instance.OnRootTick();

        /// <summary>整个根节点销毁（回登录 / 切大版本；管理器会调）</summary>
        public void DestroySelf()
        {
            FlushPendingRelease();
            _mask.Clear();
            _layers.Clear();
            _splitCanvases.Clear();
            DestroyObject(gameObject);
        }
    }
}
