// ============================================================
// RevAutomationAuthoring —— UI 面板与 Prefab 制作：RectTransform、UGUI 节点、Prefab 应用与解包。
// 【为什么】Revolution 的界面是"面板预制体 + [RevBind] 节点"。制作它需要排版（锚点/尺寸）、搭节点、
//          并把场景里改好的内容写回 Prefab 资产；这些以前只能手点 Inspector，AI 没有对应入口。
// 【怎么用】ui.create 搭节点 → rect.set 排版 → rect.get 读回 → prefab.create / prefab.apply 落成资产；
//          已有 Prefab 想加子节点：prefab.instantiate → 编辑实例 → prefab.apply → gameobject.delete 删实例。
// 【边界】只做 UGUI 内置控件，不依赖 TMP/Input System 等可选包；不执行任意代码。所有输入先预检，
//        坐标限制在 ±1e7 以内，避免 Unity 内部溢出出现非有限数；失败不留下半个对象。
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Revolution.Editor
{
    public static class RevAutomationAuthoring
    {
        private const double CoordinateLimit = 1e7;
        internal static readonly string[] UiKinds = { "Canvas", "EventSystem", "Panel", "Image", "RawImage", "Text", "Button", "Toggle", "Slider", "Scrollbar", "Dropdown", "InputField", "ScrollView" };

        [Serializable] public sealed class RectInfo
        {
            public RevAutomationEditorModel.ObjectInfo target;
            public string parent, canvas;
            public float[] anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta, offsetMin, offsetMax, rect, localScale;
        }

        [Serializable] public sealed class PrefabApplyInfo
        {
            public string prefabPath;
            public bool hadOverrides;
            public RevAutomationEditorModel.ObjectInfo instance;
        }

        /// <summary>解析完成、尚未写入的排版修改。先全部解析再写，保证坏输入不会留下改了一半的矩形。</summary>
        internal sealed class RectChange
        {
            public Vector2? anchorMin, anchorMax, pivot, anchored, size, offsetMin, offsetMax;
            public bool IsEmpty => !anchorMin.HasValue && !anchorMax.HasValue && !pivot.HasValue && !anchored.HasValue && !size.HasValue && !offsetMin.HasValue && !offsetMax.HasValue;
        }

        private static float[] Pair(Vector2 value) => new[] { value.x, value.y };

        private static RectTransform RequireRect(GameObject go)
        {
            return go.transform as RectTransform
                ?? throw new ArgumentException("目标没有 RectTransform，不是 UI 节点：" + RevAutomationEditorModel.HierarchyPath(go.transform));
        }

        private static RectInfo DescribeRect(RectTransform rt)
        {
            Canvas canvas = rt.GetComponentInParent<Canvas>(true);
            Rect rect = rt.rect;
            return new RectInfo
            {
                target = RevAutomationEditorModel.Describe(rt.gameObject),
                parent = rt.parent == null ? null : RevAutomationEditorModel.Identity(rt.parent.gameObject),
                canvas = canvas == null ? null : RevAutomationEditorModel.HierarchyPath(canvas.transform),
                anchorMin = Pair(rt.anchorMin), anchorMax = Pair(rt.anchorMax), pivot = Pair(rt.pivot),
                anchoredPosition = Pair(rt.anchoredPosition), sizeDelta = Pair(rt.sizeDelta),
                offsetMin = Pair(rt.offsetMin), offsetMax = Pair(rt.offsetMax),
                rect = new[] { rect.x, rect.y, rect.width, rect.height },
                localScale = new[] { rt.localScale.x, rt.localScale.y, rt.localScale.z }
            };
        }

        [RevAutomationAction("rect.get", Environment = "either", Keys = new[] { "id", "objectPath", "scene" }, Description = "读取 RectTransform 的锚点、轴心、位置、尺寸、偏移、本地矩形与所属 Canvas")]
        public static object GetRect(RevAutomationRequest request) =>
            DescribeRect(RequireRect(RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request))));

        private static Vector2? PairArgument(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var raw)) { return null; }
            if (!(raw is List<object> items) || items.Count != 2) { throw new ArgumentException(key + " 必须为两个有限数字组成的数组"); }
            var coordinates = new float[2];
            for (int i = 0; i < 2; i++)
            {
                if (!(items[i] is double value) || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > CoordinateLimit)
                { throw new ArgumentException(key + " 必须为绝对值不超过 1e7 的有限数字，不能包含字符串或布尔值"); }
                coordinates[i] = (float)value;
            }
            return new Vector2(coordinates[0], coordinates[1]);
        }

        /// <summary>从原始 JSON 解析排版修改：不能让 JsonUtility 把错误元素静默变成 0。rect.set 与 scene.compose 共用。</summary>
        internal static RectChange ParseRect(Dictionary<string, object> values)
        {
            var change = new RectChange
            {
                anchorMin = PairArgument(values, "anchorMin"), anchorMax = PairArgument(values, "anchorMax"), pivot = PairArgument(values, "pivot"),
                anchored = PairArgument(values, "anchoredPosition"), size = PairArgument(values, "sizeDelta"),
                offsetMin = PairArgument(values, "offsetMin"), offsetMax = PairArgument(values, "offsetMax")
            };
            // 两套写法描述的是同一个矩形，同时给会互相覆盖，必须由调用方选一套。
            if ((change.offsetMin.HasValue || change.offsetMax.HasValue) && (change.anchored.HasValue || change.size.HasValue))
            { throw new ArgumentException("offsetMin/offsetMax 不能与 anchoredPosition/sizeDelta 同时使用"); }
            return change;
        }

        internal static void ApplyRect(RectTransform rt, RectChange change)
        {
            if (change.anchorMin.HasValue) { rt.anchorMin = change.anchorMin.Value; }
            if (change.anchorMax.HasValue) { rt.anchorMax = change.anchorMax.Value; }
            if (change.pivot.HasValue) { rt.pivot = change.pivot.Value; }
            // sizeDelta 必须先于 anchoredPosition：后者的含义依赖当前尺寸与轴心。
            if (change.size.HasValue) { rt.sizeDelta = change.size.Value; }
            if (change.anchored.HasValue) { rt.anchoredPosition = change.anchored.Value; }
            if (change.offsetMin.HasValue) { rt.offsetMin = change.offsetMin.Value; }
            if (change.offsetMax.HasValue) { rt.offsetMax = change.offsetMax.Value; }
        }

        [RevAutomationAction("rect.set", Keys = new[] { "id", "objectPath", "scene", "anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta", "offsetMin", "offsetMax" },
            Description = "修改 RectTransform 排版并支持 Undo；offsetMin/offsetMax 与 anchoredPosition/sizeDelta 二选一；只写给出的字段，单独改锚点会让矩形实际位置变化，要保持位置请同时给偏移")]
        public static object SetRect(RevAutomationRequest request)
        {
            RectChange change = ParseRect(RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}")));
            if (change.IsEmpty) { throw new ArgumentException("需要 anchorMin/anchorMax/pivot/anchoredPosition/sizeDelta/offsetMin/offsetMax 至少一项"); }
            RectTransform rt = RequireRect(RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request)));
            Undo.RecordObject(rt, "Revolution Automation Rect");
            ApplyRect(rt, change);
            EditorUtility.SetDirty(rt);
            if (PrefabUtility.IsPartOfPrefabInstance(rt)) { PrefabUtility.RecordPrefabInstancePropertyModifications(rt); }
            EditorSceneManager.MarkSceneDirty(rt.gameObject.scene);
            return DescribeRect(rt);
        }

        private static DefaultControls.Resources UiResources() => new DefaultControls.Resources
        {
            // 与 Unity 菜单创建控件时使用的内置皮肤一致，创建出来的 Button/Panel 才不是一片纯白。
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd")
        };

        internal static GameObject BuildUi(string kind)
        {
            switch (kind)
            {
                case "Canvas":
                    var canvas = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                    canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                    return canvas;
                case "EventSystem": return new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                case "Panel": return DefaultControls.CreatePanel(UiResources());
                case "Image": return DefaultControls.CreateImage(UiResources());
                case "RawImage": return DefaultControls.CreateRawImage(UiResources());
                case "Text": return DefaultControls.CreateText(UiResources());
                case "Toggle": return DefaultControls.CreateToggle(UiResources());
                case "Slider": return DefaultControls.CreateSlider(UiResources());
                case "Scrollbar": return DefaultControls.CreateScrollbar(UiResources());
                case "Dropdown": return DefaultControls.CreateDropdown(UiResources());
                case "InputField": return DefaultControls.CreateInputField(UiResources());
                case "ScrollView": return DefaultControls.CreateScrollView(UiResources());
                default: return DefaultControls.CreateButton(UiResources());
            }
        }

        /// <summary>Canvas 与 EventSystem 是场景级根节点，其余控件脱离 Canvas 不会被渲染也收不到点击。</summary>
        internal static bool NeedsCanvas(string kind) => kind != "Canvas" && kind != "EventSystem";

        internal static void FinishUi(GameObject go, Transform parent)
        {
            int layer = parent != null ? parent.gameObject.layer : LayerMask.NameToLayer("UI");
            if (layer < 0) { layer = 5; }
            foreach (Transform child in go.GetComponentsInChildren<Transform>(true)) { child.gameObject.layer = layer; }
        }

        [RevAutomationAction("ui.create", Keys = new[] { "kind", "name", "parent", "scene" },
            Description = "创建 UGUI 节点（Canvas/EventSystem/Panel/Image/RawImage/Text/Button/Toggle/Slider/Scrollbar/Dropdown/InputField/ScrollView）并支持 Undo；除 Canvas、EventSystem 外必须放在 Canvas 之下，Text 为内置 UnityEngine.UI.Text")]
        public static object CreateUi(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            string kind = values.TryGetValue("kind", out var rawKind) ? rawKind as string : null;
            if (!UiKinds.Contains(kind)) { throw new ArgumentException("kind 必须为 " + string.Join("/", UiKinds)); }
            var p = RevAutomationEditorOptions.Read(request);
            string name = string.IsNullOrWhiteSpace(p.name) ? kind : p.name;
            Transform parent = RevAutomationWorkspace.Parent(p.parent);
            Scene scene = RevAutomationWorkspace.FindScene(p.scene);
            if (parent != null && !string.IsNullOrEmpty(p.scene) && parent.gameObject.scene != scene) { throw new ArgumentException("parent 与 scene 不一致"); }
            // UGUI 控件脱离 Canvas 不会被渲染也收不到点击；与其悄悄建出一个"看不见的按钮"，不如当场拒绝并说明做法。
            if (NeedsCanvas(kind) && (parent == null || parent.GetComponentInParent<Canvas>(true) == null))
            { throw new ArgumentException("该节点必须放在 Canvas 之下：先 ui.create kind=Canvas，再把它的 id 作为 parent"); }
            GameObject go = null;
            try
            {
                go = BuildUi(kind);
                go.name = name;
                SceneManager.MoveGameObjectToScene(go, parent != null ? parent.gameObject.scene : scene);
                if (parent != null) { go.transform.SetParent(parent, false); }
                FinishUi(go, parent);
                Undo.RegisterCreatedObjectUndo(go, "Revolution Automation UI");
                EditorSceneManager.MarkSceneDirty(go.scene);
                return RevAutomationEditorModel.Describe(go);
            }
            catch
            {
                if (go != null) { Object.DestroyImmediate(go); }
                throw;
            }
        }

        [Serializable] public sealed class UiNodeInfo
        {
            public string id, path, text;
            public bool active, raycastTarget;
            public string[] components;
            public float[] rect;
        }

        [Serializable] public sealed class UiSnapshot { public int total, offset; public bool truncated; public UiNodeInfo[] nodes; }

        [RevAutomationAction("ui.snapshot", Environment = "either", Keys = new[] { "id", "objectPath", "scene", "offset", "limit" },
            Description = "读取某个 UI 节点及全部子节点的分页快照（路径、矩形、组件、文本、是否接收射线），不需要 Play；这是几何事实，不代表真实遮挡，点击验证用 ui.dump/ui.click")]
        public static object SnapshotUi(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); p.CheckPage();
            RectTransform root = RequireRect(RevAutomationEditorModel.GameObject(p));
            var nodes = root.GetComponentsInChildren<RectTransform>(true);
            return new UiSnapshot
            {
                total = nodes.Length, offset = p.offset, truncated = nodes.Length > p.offset + p.limit,
                nodes = nodes.Skip(p.offset).Take(p.limit).Select(rt =>
                {
                    Rect rect = rt.rect;
                    // 这里不能用 ?.：编辑器下 GetComponent 没找到时返回假 null，?. 拦不住，.text 会抛异常。
                    var graphic = rt.GetComponent<Graphic>();
                    var label = rt.GetComponent<Text>();
                    return new UiNodeInfo
                    {
                        id = RevAutomationEditorModel.Identity(rt.gameObject), path = RevAutomationEditorModel.HierarchyPath(rt), active = rt.gameObject.activeInHierarchy,
                        raycastTarget = graphic != null && graphic.raycastTarget, text = label != null ? label.text : null,
                        components = rt.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray(),
                        rect = new[] { rect.x, rect.y, rect.width, rect.height }
                    };
                }).ToArray()
            };
        }

        private static GameObject InstanceRoot(GameObject go)
        {
            return PrefabUtility.GetOutermostPrefabInstanceRoot(go)
                ?? throw new ArgumentException("目标不是 Prefab 实例：" + RevAutomationEditorModel.HierarchyPath(go.transform));
        }

        [RevAutomationAction("prefab.apply", Keys = new[] { "id", "objectPath", "scene" },
            Description = "把场景中 Prefab 实例的全部覆盖写回其 Prefab 资产并保存；写盘不可 Undo，只允许 Assets 下的 Prefab")]
        public static object ApplyPrefab(RevAutomationRequest request)
        {
            GameObject root = InstanceRoot(RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request)));
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            // 第二个参数 true = 要写：Packages 里的只读 Prefab 在这里就被拒绝，而不是写到一半才失败。
            RevAutomationEditorModel.AssetPath(path, true);
            bool hadOverrides = PrefabUtility.HasPrefabInstanceAnyOverrides(root, false);
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.AutomatedAction);
            AssetDatabase.SaveAssets();
            return new PrefabApplyInfo { prefabPath = path, hadOverrides = hadOverrides, instance = RevAutomationEditorModel.Describe(root) };
        }

        [RevAutomationAction("prefab.unpack", Keys = new[] { "id", "objectPath", "scene", "unpackMode" },
            Description = "解除场景中 Prefab 实例与资产的关联，不支持 Undo（需要时用 prefab.instantiate 重建）；unpackMode 为 root（默认，仅最外层）或 completely（含嵌套）")]
        public static object UnpackPrefab(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            string mode = values.TryGetValue("unpackMode", out var rawMode) ? rawMode as string : "root";
            if (mode != "root" && mode != "completely") { throw new ArgumentException("unpackMode 必须为 root 或 completely"); }
            GameObject root = InstanceRoot(RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request)));
            // 有意使用 AutomatedAction：它不弹确认框（模态框会把自动化桥卡死），代价是不记录 Undo，所以描述里明确写了不支持 Undo。
            PrefabUtility.UnpackPrefabInstance(root, mode == "completely" ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            EditorSceneManager.MarkSceneDirty(root.scene);
            return RevAutomationEditorModel.Describe(root);
        }

        [RevAutomationAction("prefab.revert", Keys = new[] { "id", "objectPath", "scene" },
            Description = "丢弃场景中 Prefab 实例的全部覆盖，回到资产原样；不可 Undo，需要 confirm", Confirm = true)]
        public static object RevertPrefab(RevAutomationRequest request)
        {
            GameObject root = InstanceRoot(RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request)));
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.AutomatedAction);
            EditorSceneManager.MarkSceneDirty(root.scene);
            return RevAutomationEditorModel.Describe(root);
        }

        [RevAutomationAction("prefab.variant", Keys = new[] { "path", "destination" },
            Description = "基于现有 Prefab 创建 Prefab Variant 资产，不覆盖已有目标，也不改动当前场景")]
        public static object CreateVariant(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            RevAutomationEditorModel.AssetPath(p.path, false);
            if (!p.path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("path 需要 .prefab 资产"); }
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(p.path) ?? throw new ArgumentException("Prefab 不存在：" + p.path);
            RevAutomationEditorModel.RequireNewAsset(p.destination);
            if (!p.destination.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("destination 需要 .prefab 后缀"); }
            // 变体必须从实例保存才会带上"继承自母体"的关系。放进预览场景做，当前场景不会变脏。
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, preview);
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, p.destination, out bool success);
                if (!success || variant == null) { throw new InvalidOperationException("Prefab Variant 保存失败"); }
                return RevAutomationEditorModel.Describe(variant);
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }
    }
}
