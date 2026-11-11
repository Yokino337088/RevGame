// ============================================================
// GameMobileLayout.cs —— 旧版横屏布局兼容组件，默认不再改写预制体。
// RectTransform 锚点与框架 CanvasScaler 是正式界面的布局来源，编辑结果即运行时基准。
// 旧算法需在 Inspector 明确开启；保留它不代表正常界面会自动重排。
// ============================================================
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
#endif

namespace RevGame.UI
{
    public sealed class GameMobileLayout : MonoBehaviour
    {
        [Tooltip("对应预制体名称，由编辑器装配工具填写；不依赖运行时 Clone 名称。")]
        [SerializeField] private string panelName;
        /// <summary>默认以预制体为唯一布局来源；旧算法只用于主动选择的旧版布局演示。</summary>
        [Tooltip("旧版代码重排（默认关闭）。开启后会覆盖预制体坐标，新建按钮也不会自动加入旧算法。正常界面请使用 RectTransform 锚点与 CanvasScaler 适配。")]
        [SerializeField] private bool useLegacyLayout;
        private Vector2 _lastSize;
        private Rect _lastSafe;
        private bool _applying;
        private bool _boardFrame;
        private float _frameScale;
        private SidebarNode[] _leftSidebar;
        private SidebarNode[] _rightSidebar;
        private FullBleedNode[] _fullBleed;
        private SafeHudNode[] _sceneHud;
        public bool HasBoardFrame => _boardFrame;

        private struct SidebarNode
        {
            public RectTransform Transform;
            public Vector2 DesignPosition;
        }

        private struct SafeHudNode
        {
            public RectTransform Transform;
            public Vector2 AnchorMin, AnchorMax, OffsetMin, OffsetMax;
        }

        private struct FullBleedNode
        {
            public RectTransform Transform;
            public RawImage Artwork;
            public Rect SourceUv;
        }

        /// <summary>菜单、弹窗和全屏过场共用同一个设计框。指定的背景或遮罩穿过设计框，覆盖整个 UI 层。</summary>
        public static void EnableSafePanel(Transform panel, params string[] fullBleedPaths)
        {
            if (!UsesMobilePreview())
            {
                return;
            }
            GameMobileLayout layout = GetLayout(panel);
            layout._boardFrame = true;
            layout._lastSize = Vector2.zero;
            layout._fullBleed = new FullBleedNode[fullBleedPaths.Length];
            for (int i = 0; i < fullBleedPaths.Length; i++)
            {
                var node = panel.Find(fullBleedPaths[i]) as RectTransform;
                RawImage artwork = node == null ? null : node.GetComponent<RawImage>();
                layout._fullBleed[i] = new FullBleedNode
                {
                    Transform = node, Artwork = artwork, SourceUv = artwork == null ? default : artwork.uvRect
                };
            }
            layout.ApplyLayout();
        }

        /// <summary>场景 Canvas 保留全屏，决斗的按钮和状态条分别使用原始锚点映射到安全设计框。</summary>
        public static void EnableSceneHud(Transform canvas, params string[] paths)
        {
            if (!UsesMobilePreview())
            {
                return;
            }
            GameMobileLayout layout = GetLayout(canvas);
            layout._lastSize = Vector2.zero;
            layout._sceneHud = new SafeHudNode[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                if (canvas.Find(paths[i]) is RectTransform node)
                {
                    layout._sceneHud[i] = new SafeHudNode
                    {
                        Transform = node, AnchorMin = node.anchorMin, AnchorMax = node.anchorMax,
                        OffsetMin = node.offsetMin, OffsetMax = node.offsetMax
                    };
                }
            }
            layout.ApplyLayout();
        }

        /// <summary>在安全区内以等比设计框显示决斗 HUD，背景和角色继续使用完整画幅。</summary>
        internal static void EnableDuelHud(RectTransform hud)
        {
            // 以项目统一设计框等比布局，超宽屏、刘海屏上的触控区域仍是圆形。
            GameMobileLayout layout = GetLayout(hud);
            layout._boardFrame = true;
            // 宽屏多出的安全空间优先留给两手的控制区，顶部状态条保持对称。
            layout._leftSidebar = ReadSidebar(hud, "MoveStick", "Jump");
            layout._rightSidebar = ReadSidebar(hud, "Jab", "Skill", "SkillTwo", "Ultimate");
            layout._lastSize = Vector2.zero;
            layout.ApplyLayout();
        }

        /// <summary>将指定背景或引导节点还原为覆盖父画布。</summary>
        public static void StretchToParent(RectTransform root)
        {
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
        }

        private static bool UsesMobilePreview()
        {
#if UNITY_ANDROID || UNITY_IOS
            return true;
#else
            return UnityEngine.Device.Application.isMobilePlatform;
#endif
        }

        private static GameMobileLayout GetLayout(Transform panel)
        {
            return panel.TryGetComponent(out GameMobileLayout layout) ? layout : panel.gameObject.AddComponent<GameMobileLayout>();
        }

        /// <summary>只给战斗控制面板装上 Android 设计框，背景和相机仍使用完整屏幕。</summary>
        public static void EnableBoardFrame(Transform panel, string[] leftNodes = null, string[] rightNodes = null)
        {
#if UNITY_ANDROID
            GameMobileLayout layout = GetLayout(panel);
            layout._boardFrame = true;
            layout._lastSize = Vector2.zero;
            // 装配时只读取一次预制体坐标，后续换设备都从设计原点重算，不累加偏移。
            if (leftNodes != null || rightNodes != null)
            {
                layout._leftSidebar = ReadSidebar(panel, leftNodes);
                layout._rightSidebar = ReadSidebar(panel, rightNodes);
            }
            else if (layout.panelName == "PreparePanel")
            {
                layout._leftSidebar = ReadSidebar(panel, "cards", "hintPlate", "hintText");
                layout._rightSidebar = ReadSidebar(panel, "btnReady", "btnRefresh", "btnEye", "btnSkip");
            }
            else if (layout.panelName == "BattleHudPanel")
            {
                layout._leftSidebar = ReadSidebar(panel, "scoreFrame", "assistPlate", "assistText");
                layout._rightSidebar = ReadSidebar(panel, "infoPlate", "btnHome", "btnSettings");
            }
            layout.ApplyLayout();
#endif
        }

        private static SidebarNode[] ReadSidebar(Transform panel, params string[] names)
        {
            if (names == null)
            {
                return null;
            }
            var nodes = new SidebarNode[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (panel.Find(names[i]) is RectTransform node)
                {
                    nodes[i] = new SidebarNode { Transform = node, DesignPosition = node.anchoredPosition };
                }
            }
            return nodes;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>读取正在显示的面板设计框和全屏遮罩，验证实际落屏边界。</summary>
        public static object InspectMobilePanels()
        {
            var panels = new Dictionary<string, object>();
            var screen = new Rect(0f, 0f, UnityEngine.Device.Screen.width, UnityEngine.Device.Screen.height);
            Rect safe = GameBattleLayout.SafeViewport();
            var safePixels = new Rect(Vector2.Scale(safe.position, screen.size), Vector2.Scale(safe.size, screen.size));
            var corners = new Vector3[4];
            foreach (var panel in FindObjectsOfType<Revolution.RevUIPanel>())
            {
                if (!panel.TryGetComponent(out GameMobileLayout layout) || !layout.HasBoardFrame)
                {
                    continue;
                }
                bool coversScreen = true;
                int covers = 0;
                if (layout._fullBleed != null)
                {
                    foreach (FullBleedNode backdrop in layout._fullBleed)
                    {
                        covers++;
                        coversScreen &= backdrop.Transform != null && Contains(ScreenBounds(backdrop.Transform, corners), screen);
                    }
                }
                panels[panel.GetType().Name] = new Dictionary<string, object>
                {
                    ["frameInSafeArea"] = Contains(safePixels, ScreenBounds((RectTransform)panel.transform, corners)),
                    ["fullBleedCount"] = covers,
                    ["fullBleedCoversScreen"] = coversScreen
                };
            }
            foreach (GameMobileLayout layout in FindObjectsOfType<GameMobileLayout>())
            {
                if (layout._sceneHud == null)
                {
                    continue;
                }
                int checkedCount = 0;
                bool inSafeArea = true;
                foreach (SafeHudNode node in layout._sceneHud)
                {
                    if (node.Transform != null && node.Transform.gameObject.activeInHierarchy)
                    {
                        checkedCount++;
                        inSafeArea &= Contains(safePixels, ScreenBounds(node.Transform, corners));
                    }
                }
                panels[layout.name] = new Dictionary<string, object>
                {
                    ["frameInSafeArea"] = inSafeArea,
                    ["controlsChecked"] = checkedCount
                };
            }
            return panels;
        }

        private static Rect ScreenBounds(RectTransform rect, Vector3[] corners)
        {
            rect.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            const float Tolerance = 1f;
            return outer.xMin <= inner.xMin + Tolerance && outer.xMax >= inner.xMax - Tolerance &&
                outer.yMin <= inner.yMin + Tolerance && outer.yMax >= inner.yMax - Tolerance;
        }

        /// <summary>用实际控件角点、相机投影与设备安全区验收，不用布局公式替代落屏证据。</summary>
        public static object InspectBoardControls(Transform panel, Camera camera, Rect boardWorld, params string[] paths)
        {
            if (!(panel is RectTransform root) || camera == null || !panel.TryGetComponent(out GameMobileLayout layout) || !layout.HasBoardFrame)
            {
                return null;
            }

            const float Tolerance = 1f;
            Vector3 low = camera.WorldToScreenPoint(boardWorld.min);
            Vector3 high = camera.WorldToScreenPoint(boardWorld.max);
            Rect board = Rect.MinMaxRect(low.x + Tolerance, low.y + Tolerance, high.x - Tolerance, high.y - Tolerance);
            var pixels = new Vector2(camera.pixelWidth, camera.pixelHeight);
            Rect safe = GameBattleLayout.SafeViewport();
            Rect safePixels = new Rect(Vector2.Scale(safe.position, pixels), Vector2.Scale(safe.size, pixels));
            var collisions = new List<string>();
            var outside = new List<string>();
            var corners = new Vector3[4];
            int checkedCount = 0;
            foreach (string path in paths)
            {
                if (!(panel.Find(path) is RectTransform control) || !control.gameObject.activeInHierarchy)
                {
                    continue;
                }
                checkedCount++;
                control.GetWorldCorners(corners);
                Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
                Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
                Rect bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                if (board.Overlaps(bounds))
                {
                    collisions.Add(path);
                }
                if (bounds.xMin < safePixels.xMin - Tolerance || bounds.xMax > safePixels.xMax + Tolerance ||
                    bounds.yMin < safePixels.yMin - Tolerance || bounds.yMax > safePixels.yMax + Tolerance)
                {
                    outside.Add(path);
                }
            }
            return new Dictionary<string, object>
            {
                ["designWidth"] = root.rect.width,
                ["designHeight"] = root.rect.height,
                ["controlsChecked"] = checkedCount,
                ["controlOverlaps"] = collisions.ToArray(),
                ["outsideSafeArea"] = outside.ToArray(),
                ["sidebarOffset"] = GameBattleLayout.SidebarOffsets(safe, GameBattleLayout.BoardViewport(pixels)),
                ["mobilePreview"] = UnityEngine.Device.Application.isMobilePlatform
            };
        }
#endif

        public void Configure(string value)
        {
            panelName = value;
            _lastSize = Vector2.zero;
            ApplyLayout();
        }
        private void OnEnable()
        {
            _lastSize = Vector2.zero;
            ApplyLayout();
        }
        private void OnRectTransformDimensionsChange()
        {
            ApplyLayout();
        }
        // 刘海/模拟器旋转可能只改变安全区，不改变 RectTransform 尺寸，因此还要轻量比较一次。
        private void LateUpdate()
        {
            ApplyLayout();
        }
        /// <summary>只在尺寸变化时重排；防止设置子节点尺寸触发递归排版。</summary>
        private void ApplyLayout()
        {
            RectTransform root = transform as RectTransform;
            if (_sceneHud != null)
            {
                ApplySceneHud(root);
                return;
            }
            if (_boardFrame)
            {
                ApplyBoardFrame(root);
                return;
            }

            // 不按节点名重新摆放：新增控件、字体行高与手工坐标必须一起来自预制体。
            // 画布等比缩放和预制体锚点仍正常生效；安全区布局需要在预制体中显式设计。
            if (!useLegacyLayout || _applying || root == null || string.IsNullOrEmpty(panelName))
            {
                return;
            }
            Vector2 size = root.rect.size;
            Rect safe = GameBattleLayout.SafeViewport();

            if (size.x < 10f || size.y < 10f || (size == _lastSize && safe == _lastSafe))
            {
                return;
            }
            _applying = true;
            _lastSize = size;
            _lastSafe = safe;

            // 两个战斗面板的预制体已按 1920×1080 无刘海布局保存；在设计分辨率下
            // 直接使用可编辑的预制体坐标，只有设备尺寸/安全区变化时才重新适配。
            if ((panelName == "PreparePanel" || panelName == "BattleHudPanel")
                && Mathf.Abs(size.x - GameBattleLayout.DesignResolution.x) < 0.01f
                && Mathf.Abs(size.y - GameBattleLayout.DesignResolution.y) < 0.01f
                && safe == new Rect(0f, 0f, 1f, 1f))
            {
                _applying = false;
                return;
            }
            float width = size.x;

            if (panelName == "PreparePanel")
            {
                LayoutHand(GameBattleLayout.Calculate(size, safe));
            }
            else

                if (panelName == "BattleHudPanel")
                {
                    LayoutHud(GameBattleLayout.Calculate(size, safe));
                }
                else

                    if (panelName == "HomePanel")
                    {
                        Place("contentPlate", 0.5f, 0.5f, 0, 0, 1140, 800);
                        Place("title", 0.5f, 0.5f, 0, 230, width * 0.85f, 130);
                        Place("subtitle", 0.5f, 0.5f, 0, 100, width * 0.8f, 90);
                        Place("btnPlay", 0.5f, 0.5f, 0, -70, 420, 130);
                        Place("hint", 0.5f, 0.5f, 0, -270, width * 0.75f, 110);
                    }
                    else

                        if (panelName == "LevelSelectPanel")
                        {
                            Place("contentPlate", 0.5f, 0.5f, 0, 0, 1660, 960);
                            Place("title", 0.5f, 0.5f, 0, 345, width * 0.8f, 100);
                            for (int i = 0; i < 6; i++)
                            {
                                float x = (i % 3 - 1) * 400;
                                float y = 130 - i / 3 * 220;
                                Place("chapter" + (i + 1), 0.5f, 0.5f, x, y, 350, 190);
                            }
                            Place("hint", 0.5f, 0.5f, 0, -290, width * 0.8f, 100);
                            Place("btnHome", 0.5f, 0.5f, 0, -385, 340, 100);
                        }
                        else

                            if (panelName == "SettlePanel")
                            {
                                Place("background", 0.5f, 0.5f, 0, 0, 1000, 670);
                                Place("titleText", 0.5f, 0.5f, 0, 160, 800, 100);
                                Place("detailText", 0.5f, 0.5f, 0, 20, 800, 150);
                                Place("btnContinue", 0.5f, 0.5f, 0, -180, 350, 110);
                            }
                            else

                                if (panelName == "RewardPanel")
                                {
                                    Place("background", 0.5f, 0.5f, 0, 0, 1380, 778);
                                    Place("heading", 0.5f, 0.5f, 0, 245, width * 0.85f, 100);
                                    for (int i = 0; i < 3; i++)
                                    {
                                        float cardWidth = 340f;
                                        Place("choice" + i, 0.5f, 0.5f, (i - 1) * (cardWidth + 20), -30, cardWidth, 350);
                                        Place("choice" + i + "/label", 0.5f, 0.5f, 0, -70, cardWidth - 26, 190);
                                    }
                                }

            _applying = false;
        }

        /// <summary>固定预制体设计坐标，只移动及等比缩放面板根。父级仍是覆盖全屏的 Revolution UI 层。</summary>
        private void ApplyBoardFrame(RectTransform root)
        {
            // 弹窗转场会临时改缩放；等原有动画结束后才应用新的设计框，避免互相覆盖。
            if (_applying || root == null || !(root.parent is RectTransform parent) || Revolution.RevUIAnim.IsPlaying(this))
            {
                return;
            }
            Vector2 size = parent.rect.size;
            Rect safe = GameBattleLayout.SafeViewport();
            if (size.x <= 0f || size.y <= 0f ||
                (size == _lastSize && safe == _lastSafe && Mathf.Approximately(root.localScale.x, _frameScale)))
            {
                return;
            }

            Rect frame = GameBattleLayout.FitBoardViewport(size, safe);
            _applying = true;
            _lastSize = size;
            _lastSafe = safe;
            root.anchorMin = root.anchorMax = frame.center;
            root.pivot = Vector2.one * .5f;
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = GameBattleLayout.DesignResolution;
            _frameScale = frame.height * size.y / GameBattleLayout.DesignResolution.y;
            root.localScale = Vector3.one * _frameScale;
            Vector2 sidebar = GameBattleLayout.SidebarOffsets(safe, frame);
            MoveSidebar(_leftSidebar, sidebar.x);
            MoveSidebar(_rightSidebar, sidebar.y);
            if (_fullBleed != null)
            {
                // 从全屏坐标反算设计框内锚点，背景仍覆盖刘海外侧与宽屏留边。
                Vector2 min = new Vector2(-frame.xMin / frame.width, -frame.yMin / frame.height);
                Vector2 max = new Vector2((1f - frame.xMin) / frame.width, (1f - frame.yMin) / frame.height);
                foreach (FullBleedNode backdrop in _fullBleed)
                {
                    if (backdrop.Transform != null)
                    {
                        backdrop.Transform.anchorMin = min;
                        backdrop.Transform.anchorMax = max;
                        backdrop.Transform.offsetMin = backdrop.Transform.offsetMax = Vector2.zero;
                    }
                    CropArtwork(backdrop, size.x / size.y);
                }
            }
            _applying = false;
        }

        /// <summary>图片使用原始 UV 等比裁切，重复旋转时不会在已经裁过的范围上继续裁。</summary>
        private static void CropArtwork(FullBleedNode backdrop, float viewportAspect)
        {
            Texture texture = backdrop.Artwork == null ? null : backdrop.Artwork.texture;
            Rect uv = backdrop.SourceUv;
            if (texture == null || uv.width <= 0f || uv.height <= 0f)
            {
                return;
            }
            float aspect = texture.width * uv.width / (texture.height * uv.height);
            float width = uv.width * Mathf.Min(1f, viewportAspect / aspect);
            float height = uv.height * Mathf.Min(1f, aspect / viewportAspect);
            backdrop.Artwork.uvRect = new Rect(uv.center - new Vector2(width, height) * .5f, new Vector2(width, height));
        }

        private void ApplySceneHud(RectTransform root)
        {
            if (_applying || root == null)
            {
                return;
            }
            Vector2 size = root.rect.size;
            Rect safe = GameBattleLayout.SafeViewport();
            if (size.x <= 0f || size.y <= 0f || (size == _lastSize && safe == _lastSafe))
            {
                return;
            }

            Rect frame = GameBattleLayout.FitBoardViewport(size, safe);
            float scale = frame.height * size.y / GameBattleLayout.DesignResolution.y;
            _applying = true;
            _lastSize = size;
            _lastSafe = safe;
            foreach (SafeHudNode node in _sceneHud)
            {
                if (node.Transform == null)
                {
                    continue;
                }
                node.Transform.anchorMin = frame.position + Vector2.Scale(node.AnchorMin, frame.size);
                node.Transform.anchorMax = frame.position + Vector2.Scale(node.AnchorMax, frame.size);
                node.Transform.offsetMin = node.OffsetMin * scale;
                node.Transform.offsetMax = node.OffsetMax * scale;
            }
            _applying = false;
        }

        private static void MoveSidebar(SidebarNode[] nodes, float offset)
        {
            if (nodes == null)
            {
                return;
            }
            foreach (SidebarNode node in nodes)
            {
                if (node.Transform != null)
                {
                    node.Transform.anchoredPosition = node.DesignPosition + Vector2.right * offset;
                }
            }
        }
        private void LayoutHand(GameBattleLayout.Regions areas)
        {
            Transform row = transform.Find("cards");

            if (row != null)
            {
                // 自己计算每个槽位，避免 HorizontalLayoutGroup 在横屏侧栏里又把牌挤回一行。
                HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();

                if (layout != null)
                {
                    layout.enabled = false;
                }
                Rect hand = GameBattleLayout.InRegion(areas.Left, 0.50f, 1f, 0.65f);
                PlaceAbsolute("cards", hand);
                for (int i = 0; i < row.childCount; i++)
                {
                    Rect card = GameBattleLayout.HandCard(hand, areas.Safe.height, i);
                    // HandCard 返回面板坐标，子节点却以 cards 左下角为原点，因此减去手牌区域起点。
                    // 每次从区域重新计算，不能在上一次位置上追加间距，否则缩放窗口后两列会不断向外漂。
                    Vector2 center = card.center - hand.position;
                    Place("cards/" + row.GetChild(i).name, 0f, 0f, center.x, center.y, card.width, card.height);
                }
            }

            InRegion("hintText", areas.Left, 0.92f, 0.94f, 0.12f);
            InRegion("hintPlate", areas.Left, 0.92f, 1f, 0.15f);
            InRegion("btnReady", areas.Right, 0.06f, 0.94f, 0.10f);
            InRegion("btnRefresh", areas.Right, 0.18f, 0.94f, 0.10f);
            InRegion("btnEye", areas.Right, 0.30f, 0.88f, 0.095f);
            InRegion("btnSkip", areas.Right, 0.42f, 0.88f, 0.095f);
        }
        private void LayoutHud(GameBattleLayout.Regions areas)
        {
            Rect header = areas.Header;
            InRegion("roundText", header, 0.66f, 0.94f, 0.52f);
            InRegion("btnHome", areas.Right, 0.95f, 0.94f, 0.08f);
            // 加成文字和主将血条现在以信息牌为父级，重排时随牌整体移动。
            InRegion("infoPlate", areas.Right, 0.78f, 1f, 0.22f);
            InRegion("assistText", areas.Left, 0.065f, 0.94f, 0.10f);
            InRegion("assistPlate", areas.Left, 0.065f, 1f, 0.125f);
            // 计分板只在战斗/结算显示；准备阶段左栏留给六张手牌。统一坐标避免挤进棋盘。
            Place("scoreFrame", 0f, 0f, areas.Left.center.x, areas.Left.yMin + areas.Left.height * .51f, 330f, 310f);
        }
        private void InRegion(string path, Rect area, float y, float width, float height)
        {
            PlaceAbsolute(path, GameBattleLayout.InRegion(area, y, width, height));
        }
        /// <summary>每次由原始区域计算绝对位置，不在上次位置上继续加偏移，反复缩放不会越移越远。</summary>
        private void PlaceAbsolute(string path, Rect area)
        {
            Place(path, 0f, 0f, area.center.x, area.center.y, area.width, area.height);
            Transform node = transform.Find(path);
            Text text = node == null ? null : node.GetComponent<Text>();

            if (text != null)
            {
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 14;
                text.resizeTextMaxSize = text.fontSize;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
            }
        }

        /// <summary>统一设置锚点和中心坐标；不改字体/美术引用，换屏幕后仍可复用同一预制体。</summary>
        private void Place(string path, float ax, float ay, float x, float y, float width, float height)
        {
            RectTransform rect = transform.Find(path) as RectTransform;

            if (rect == null)
            {
                return;
            }
            rect.anchorMin = rect.anchorMax = new Vector2(ax, ay);
            rect.pivot = Vector2.one * 0.5f;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
