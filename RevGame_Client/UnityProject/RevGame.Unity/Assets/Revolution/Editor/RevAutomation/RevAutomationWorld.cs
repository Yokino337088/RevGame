// ============================================================
// RevAutomationWorld —— 声明式搭场景、对象属性、看见场景。
// 【为什么】AI 搭一个场景如果一步一调用，要先建对象、拿 id、再挂组件、再连引用，几十次往返，
//          中途失败还会留下半个场景。scene.compose 把整棵对象树写成一份 JSON，一次创建、一次回滚。
//          另外 AI 看不见场景就无法判断摆得对不对，所以 scene.capture 把画面渲染成 PNG 交回去。
// 【怎么用】scene.compose 建树 → scene.capture 看结果 → scene.save 落盘。
//          节点里写 "@子路径" 可以引用同一份 compose 里的其它节点，不用等 id。
// 【边界】任何一步失败都销毁本次创建的全部对象，不留半成品；只创建对象，不执行任意代码。
//        scene.capture 只渲染相机能看到的内容，Screen Space Overlay 的 UI 不在其中，要看 UI 用 Play + runtime.screenshot。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Revolution.Editor
{
    public static class RevAutomationWorld
    {
        private const int MaxNodes = 512, MaxDepth = 32;
        private static readonly string[] Primitives = { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        private static readonly HashSet<string> NodeKeys = new HashSet<string>
        { "name", "primitive", "prefab", "ui", "position", "eulerAngles", "localScale", "rect", "active", "layer", "tag", "isStatic", "components", "children" };

        // ---------------------------------------------------------------- 对象属性

        [RevAutomationAction("gameobject.configure", Keys = new[] { "id", "objectPath", "scene", "name", "active", "layer", "tag", "isStatic" },
            Description = "一次修改对象的名称、激活、Layer、Tag、Static 并支持 Undo；Layer/Tag 必须已存在，缺失时先用 settings.layer-add/tag-add")]
        public static object Configure(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            var p = RevAutomationEditorOptions.Read(request);
            bool hasName = values.ContainsKey("name"), hasActive = values.ContainsKey("active"), hasLayer = values.ContainsKey("layer"), hasTag = values.ContainsKey("tag"), hasStatic = values.ContainsKey("isStatic");
            if (!hasName && !hasActive && !hasLayer && !hasTag && !hasStatic) { throw new ArgumentException("需要 name/active/layer/tag/isStatic 至少一项"); }
            if (hasName && string.IsNullOrWhiteSpace(p.name)) { throw new ArgumentException("name 不能为空"); }
            bool active = !hasActive || RequireBool(values["active"], "active"), isStatic = hasStatic && RequireBool(values["isStatic"], "isStatic");
            int layer = hasLayer ? RequireLayer(values["layer"] as string) : 0;
            string tag = hasTag ? RequireTag(values["tag"] as string) : null;
            GameObject go = RevAutomationEditorModel.GameObject(p);
            Undo.RecordObject(go, "Revolution Automation Configure");
            if (hasName) { go.name = p.name; }
            if (hasActive) { go.SetActive(active); }
            if (hasLayer) { go.layer = layer; }
            if (hasTag) { go.tag = tag; }
            if (hasStatic) { go.isStatic = isStatic; }
            EditorUtility.SetDirty(go);
            if (PrefabUtility.IsPartOfPrefabInstance(go)) { PrefabUtility.RecordPrefabInstancePropertyModifications(go); }
            EditorSceneManager.MarkSceneDirty(go.scene);
            return RevAutomationEditorModel.Describe(go);
        }

        private static bool RequireBool(object value, string key) => value is bool flag ? flag : throw new ArgumentException(key + " 必须是布尔值");

        private static int RequireLayer(string name)
        {
            int layer = string.IsNullOrEmpty(name) ? -1 : LayerMask.NameToLayer(name);
            if (layer < 0) { throw new ArgumentException("Layer 不存在，先 settings.layer-add：" + name); }
            return layer;
        }

        private static string RequireTag(string name)
        {
            if (string.IsNullOrEmpty(name) || !InternalEditorUtility.tags.Contains(name)) { throw new ArgumentException("Tag 不存在，先 settings.tag-add：" + name); }
            return name;
        }

        [RevAutomationAction("settings.tag-add", Keys = new[] { "name" }, Description = "向工程添加 Tag；已存在时不改动并返回现状")]
        public static object AddTag(RevAutomationRequest request)
        {
            string name = RevAutomationEditorOptions.Read(request).name;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) { throw new ArgumentException("需要合法的 Tag 名称"); }
            if (!InternalEditorUtility.tags.Contains(name)) { InternalEditorUtility.AddTag(name); AssetDatabase.SaveAssets(); }
            return new RevAutomationActions.RevAutomationRows(InternalEditorUtility.tags);
        }

        [RevAutomationAction("settings.layer-add", Keys = new[] { "name" }, Description = "把 Layer 写入第一个空槽（8..31）；已存在时不改动并返回现状")]
        public static object AddLayer(RevAutomationRequest request)
        {
            string name = RevAutomationEditorOptions.Read(request).name;
            if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("需要 Layer 名称"); }
            if (LayerMask.NameToLayer(name) < 0)
            {
                using (var manager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]))
                {
                    SerializedProperty layers = manager.FindProperty("layers");
                    int slot = Enumerable.Range(8, 24).Where(i => string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)).DefaultIfEmpty(-1).First();
                    if (slot < 0) { throw new InvalidOperationException("8..31 层已全部占用"); }
                    layers.GetArrayElementAtIndex(slot).stringValue = name;
                    manager.ApplyModifiedPropertiesWithoutUndo();
                }
                AssetDatabase.SaveAssets();
            }
            return new RevAutomationActions.RevAutomationRows(InternalEditorUtility.layers);
        }

        [RevAutomationAction("scene.save-all", Keys = new string[0], Description = "保存全部已打开且有路径的场景；存在未命名的脏场景时拒绝，先用 scene.save 指定 path")]
        public static object SaveAll(RevAutomationRequest request)
        {
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded).ToArray();
            if (scenes.Any(s => s.isDirty && string.IsNullOrEmpty(s.path))) { throw new InvalidOperationException("存在未命名的脏场景，先 scene.save 指定 path"); }
            if (!EditorSceneManager.SaveOpenScenes()) { throw new IOException("场景保存失败"); }
            return new RevAutomationActions.RevAutomationRows(scenes.Select(s => s.path));
        }

        // ---------------------------------------------------------------- 声明式建树

        private sealed class ComponentSpec { public Type Type; public Component Created; public Dictionary<string, string> Properties = new Dictionary<string, string>(); }

        private sealed class NodeSpec
        {
            public string Name, Primitive, Prefab, Ui, Tag, RelativePath;
            public Vector3? Position, Euler, Scale;
            public RevAutomationAuthoring.RectChange Rect;
            public bool? Active, IsStatic;
            public int? Layer;
            public readonly List<ComponentSpec> Components = new List<ComponentSpec>();
            public readonly List<NodeSpec> Children = new List<NodeSpec>();
            public GameObject Created;
        }

        [Serializable] public sealed class ComposedNode { public string path, id; }
        [Serializable] public sealed class ComposeResult { public string scene; public int created; public ComposedNode[] nodes; }

        [RevAutomationAction("scene.compose", Keys = new[] { "nodes", "parent", "scene" },
            Description = "用一份 JSON 一次建好对象树：节点可写 primitive/prefab/ui、变换、rect、active/layer/tag/isStatic、components（type 或脚本路径 + properties）、children；属性值写 \"@相对路径\" 可引用同批节点。先预检，任何失败都销毁本次创建的全部对象，整批一次 Undo")]
        public static object Compose(RevAutomationRequest request)
        {
            var roots = ParseRoots(request);
            var p = RevAutomationEditorOptions.Read(request);
            Transform parent = RevAutomationWorkspace.Parent(p.parent);
            Scene scene = RevAutomationWorkspace.FindScene(p.scene);
            if (parent != null && !string.IsNullOrEmpty(p.scene) && parent.gameObject.scene != scene) { throw new ArgumentException("parent 与 scene 不一致"); }
            Scene destination = parent != null ? parent.gameObject.scene : scene;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            var created = new List<GameObject>();
            List<NodeSpec> all;
            try
            {
                all = BuildGraph(roots, parent, destination, created);
                foreach (GameObject go in created) { Undo.RegisterCreatedObjectUndo(go, "Revolution Automation Compose"); }
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch
            {
                foreach (GameObject go in created) { if (go != null) { Object.DestroyImmediate(go); } }
                throw;
            }
            EditorSceneManager.MarkSceneDirty(destination);
            return new ComposeResult
            {
                scene = destination.path, created = all.Count,
                nodes = all.Select(n => new ComposedNode { path = RevAutomationEditorModel.HierarchyPath(n.Created.transform), id = RevAutomationEditorModel.Identity(n.Created) }).ToArray()
            };
        }

        [Serializable]
        private sealed class ComposedPrefab
        {
            public string path, guid;
            public int created;
        }

        /// <summary>用场景编排的同一套节点规则创建一个新资产。临时场景只持有本次对象，成功和失败都要关闭。</summary>
        [RevAutomationAction("prefab.compose", Keys = new[] { "path", "nodes" },
            Description = "从单根 JSON 对象树直接创建新 Prefab，复用 scene.compose 节点与同批引用，不改现有场景或覆盖资产")]
        public static object ComposePrefab(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            RevAutomationEditorModel.RequireNewAsset(p.path);
            if (!p.path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || File.Exists(p.path + ".meta"))
            {
                throw new ArgumentException("需要全新的 .prefab 路径（不能复用残留 .meta）");
            }
            List<NodeSpec> roots = ParseRoots(request);
            if (roots.Count != 1)
            {
                throw new ArgumentException("prefab.compose 只允许一个根节点");
            }
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw new InvalidOperationException("请先退出 Prefab 模式，避免新对象被放入当前 Prefab 编辑舞台");
            }

            Scene previous = SceneManager.GetActiveScene();
            Object[] selection = Selection.objects;
            // CreatePrimitive 和 UGUI 工厂默认使用活动场景。把空场景设为活动后，连创建的第一刻也不会修改用户的场景。
            Scene temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                if (!SceneManager.SetActiveScene(temporary))
                {
                    throw new InvalidOperationException("无法激活临时编排场景");
                }
                List<NodeSpec> nodes = BuildGraph(roots, null, temporary, null);
                // 等全部引用写好才落盘。之前抛错时没有目标资产，关闭临时场景即可回收对象。
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(roots[0].Created, p.path, out bool success);
                    if (!success)
                    {
                        throw new IOException("Prefab 保存失败：" + p.path);
                    }
                }
                catch
                {
                    // 目标在入口已验证为新路径。Unity 若留下部分文件，只回收本次写入的目标。
                    if ((File.Exists(p.path) || File.Exists(p.path + ".meta")) && !AssetDatabase.DeleteAsset(p.path))
                    {
                        throw new IOException("Prefab 保存失败且无法回收本次目标：" + p.path);
                    }
                    throw;
                }
                return new ComposedPrefab { path = p.path, guid = AssetDatabase.AssetPathToGUID(p.path), created = nodes.Count };
            }
            finally
            {
                try
                {
                    if (previous.IsValid() && previous.isLoaded)
                    {
                        SceneManager.SetActiveScene(previous);
                    }
                    EditorSceneManager.CloseScene(temporary, true);
                }
                finally
                {
                    if (!Selection.objects.SequenceEqual(selection))
                    {
                        Selection.objects = selection;
                    }
                }
            }
        }

        private static List<NodeSpec> ParseRoots(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            if (!values.TryGetValue("nodes", out object rawNodes) || !(rawNodes is List<object> rawRoots) || rawRoots.Count == 0)
            {
                throw new ArgumentException("需要非空 nodes 数组");
            }
            int count = 0;
            return rawRoots.Select(raw => ParseNode(raw, "", 0, ref count)).ToList();
        }

        /// <summary>先完整创建对象与组件，再按路径填引用，后声明的节点也能被前面的节点引用。</summary>
        private static List<NodeSpec> BuildGraph(List<NodeSpec> roots, Transform parent, Scene destination, List<GameObject> created)
        {
            var all = new List<NodeSpec>();
            foreach (NodeSpec root in roots)
            {
                Flatten(root, all);
            }
            var byPath = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (NodeSpec node in all)
            {
                if (!byPath.TryAdd(node.RelativePath, node))
                {
                    ambiguous.Add(node.RelativePath);
                }
            }
            foreach (NodeSpec root in roots)
            {
                Build(root, parent, destination, created);
            }
            foreach (NodeSpec node in all)
            {
                AddComponents(node);
            }
            foreach (NodeSpec node in all)
            {
                ApplyProperties(node, byPath, ambiguous);
            }
            return all;
        }

        private static void Flatten(NodeSpec node, List<NodeSpec> into)
        {
            into.Add(node);
            foreach (NodeSpec child in node.Children) { Flatten(child, into); }
        }

        private static NodeSpec ParseNode(object raw, string parentPath, int depth, ref int count)
        {
            if (depth >= MaxDepth) { throw new ArgumentException("节点层级不能超过 " + MaxDepth); }
            if (++count > MaxNodes) { throw new ArgumentException("单次最多创建 " + MaxNodes + " 个节点"); }
            if (!(raw is Dictionary<string, object> values)) { throw new ArgumentException("节点必须是对象"); }
            foreach (string key in values.Keys) { if (!NodeKeys.Contains(key)) { throw new ArgumentException("节点存在未知字段：" + key); } }
            string name = values.TryGetValue("name", out object rawName) ? rawName as string : null;
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) { throw new ArgumentException("节点需要不含 / 的 name"); }
            var node = new NodeSpec { Name = name, RelativePath = parentPath.Length == 0 ? name : parentPath + "/" + name };
            string where = "节点 " + node.RelativePath + "：";
            node.Primitive = Text(values, "primitive"); node.Prefab = Text(values, "prefab"); node.Ui = Text(values, "ui");
            if ((node.Primitive != null ? 1 : 0) + (node.Prefab != null ? 1 : 0) + (node.Ui != null ? 1 : 0) > 1) { throw new ArgumentException(where + "primitive/prefab/ui 只能选一个"); }
            if (node.Primitive != null && !Primitives.Contains(node.Primitive)) { throw new ArgumentException(where + "primitive 必须为 " + string.Join("/", Primitives)); }
            if (node.Ui != null && !RevAutomationAuthoring.UiKinds.Contains(node.Ui)) { throw new ArgumentException(where + "ui 必须为 " + string.Join("/", RevAutomationAuthoring.UiKinds)); }
            if (node.Prefab != null)
            {
                RevAutomationEditorModel.AssetPath(node.Prefab, false);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(node.Prefab) == null) { throw new ArgumentException(where + "Prefab 不存在：" + node.Prefab); }
            }
            node.Position = Vector(values, "position", where); node.Euler = Vector(values, "eulerAngles", where); node.Scale = Vector(values, "localScale", where);
            if (values.TryGetValue("rect", out object rawRect))
            {
                node.Rect = RevAutomationAuthoring.ParseRect(rawRect as Dictionary<string, object> ?? throw new ArgumentException(where + "rect 必须是对象"));
                if (node.Ui == null) { throw new ArgumentException(where + "rect 只能用于 ui 节点"); }
            }
            if (values.TryGetValue("active", out object active)) { node.Active = RequireBool(active, "active"); }
            if (values.TryGetValue("isStatic", out object isStatic)) { node.IsStatic = RequireBool(isStatic, "isStatic"); }
            if (values.TryGetValue("layer", out object layer)) { node.Layer = RequireLayer(layer as string); }
            if (values.TryGetValue("tag", out object tag)) { node.Tag = RequireTag(tag as string); }
            if (values.TryGetValue("components", out object rawComponents))
            {
                foreach (object rawComponent in rawComponents as List<object> ?? throw new ArgumentException(where + "components 必须是数组"))
                { node.Components.Add(ParseComponent(rawComponent, where)); }
            }
            if (values.TryGetValue("children", out object rawChildren))
            {
                foreach (object child in rawChildren as List<object> ?? throw new ArgumentException(where + "children 必须是数组"))
                { node.Children.Add(ParseNode(child, node.RelativePath, depth + 1, ref count)); }
            }
            return node;
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out object raw)) { return null; }
            return raw as string ?? throw new ArgumentException(key + " 必须是字符串");
        }

        private static Vector3? Vector(Dictionary<string, object> values, string key, string where)
        {
            if (!values.TryGetValue(key, out object raw)) { return null; }
            if (!(raw is List<object> items) || items.Count != 3) { throw new ArgumentException(where + key + " 必须为三个有限数字"); }
            var result = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!(items[i] is double value) || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
                { throw new ArgumentException(where + key + " 必须为 Single 范围内的有限数字"); }
                result[i] = (float)value;
            }
            return new Vector3(result[0], result[1], result[2]);
        }

        private static ComponentSpec ParseComponent(object raw, string where)
        {
            if (!(raw is Dictionary<string, object> values)) { throw new ArgumentException(where + "组件必须是对象"); }
            foreach (string key in values.Keys) { if (key != "type" && key != "script" && key != "properties") { throw new ArgumentException(where + "组件存在未知字段：" + key); } }
            string type = Text(values, "type"), script = Text(values, "script");
            if ((type == null) == (script == null)) { throw new ArgumentException(where + "组件需要 type 或 script 二选一"); }
            var spec = new ComponentSpec { Type = script != null ? RevAutomationScripts.ScriptClass(script) : RevAutomationEditorModel.FindType(type, typeof(Component)) };
            if (values.TryGetValue("properties", out object rawProperties))
            {
                var properties = rawProperties as Dictionary<string, object> ?? throw new ArgumentException(where + "properties 必须是对象");
                foreach (var pair in properties)
                {
                    spec.Properties[pair.Key] = pair.Value is string text ? text
                        : pair.Value is bool flag ? (flag ? "true" : "false")
                        : pair.Value is double number ? number.ToString("R", CultureInfo.InvariantCulture)
                        : pair.Value == null ? "null"
                        : throw new ArgumentException(where + pair.Key + " 的值只能是字符串、数字、布尔或 null；向量用 {\"x\":1,...} 的字符串，或拆成 .x/.y 子字段");
                }
            }
            return spec;
        }

        /// <summary>
        /// created 只收顶层节点：销毁顶层就连带销毁子树。它必须在创建后立刻登记，
        /// 否则子节点失败时顶层还没入册，回滚会漏掉它，留下半个场景。子节点递归时传 null。
        /// </summary>
        private static void Build(NodeSpec node, Transform parent, Scene scene, List<GameObject> created)
        {
            GameObject go;
            if (node.Prefab != null) { go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(node.Prefab), scene); }
            else if (node.Primitive != null) { go = GameObject.CreatePrimitive((PrimitiveType)Enum.Parse(typeof(PrimitiveType), node.Primitive)); }
            else if (node.Ui != null) { go = RevAutomationAuthoring.BuildUi(node.Ui); }
            else { go = new GameObject(); }
            node.Created = go;
            created?.Add(go);
            go.name = node.Name;
            if (parent != null) { go.transform.SetParent(parent, false); }
            else if (go.scene != scene) { SceneManager.MoveGameObjectToScene(go, scene); }
            if (node.Ui != null)
            {
                if (RevAutomationAuthoring.NeedsCanvas(node.Ui) && go.GetComponentInParent<Canvas>(true) == null)
                { throw new ArgumentException("节点 " + node.RelativePath + "：该 UI 节点必须在 Canvas 之下"); }
                RevAutomationAuthoring.FinishUi(go, parent);
            }
            if (node.Position.HasValue) { go.transform.localPosition = node.Position.Value; }
            if (node.Euler.HasValue) { go.transform.localEulerAngles = node.Euler.Value; }
            if (node.Scale.HasValue) { go.transform.localScale = node.Scale.Value; }
            if (node.Rect != null) { RevAutomationAuthoring.ApplyRect((RectTransform)go.transform, node.Rect); }
            if (node.Layer.HasValue) { go.layer = node.Layer.Value; }
            if (node.Tag != null) { go.tag = node.Tag; }
            if (node.IsStatic.HasValue) { go.isStatic = node.IsStatic.Value; }
            foreach (NodeSpec child in node.Children) { Build(child, go.transform, scene, null); }
            // 激活状态放到子节点建完之后：先隐藏父节点会让子节点的 GetComponentInParent 走不同分支。
            if (node.Active.HasValue) { go.SetActive(node.Active.Value); }
        }

        private static void AddComponents(NodeSpec node)
        {
            foreach (ComponentSpec spec in node.Components)
            {
                // 已存在就复用：primitive 自带 Collider、ui 自带 Image，AI 想改它们不应该加出第二个。
                // 不能写成 GetComponent(...) ?? AddComponent(...)：编辑器下"没找到"返回的是 Unity 的假 null，
                // C# 的 ?? 认为它不是 null，会把这个已失效的对象当成组件用。
                Component component = node.Created.GetComponent(spec.Type);
                if (component == null) { component = node.Created.AddComponent(spec.Type); }
                if (component == null) { throw new InvalidOperationException("节点 " + node.RelativePath + "：无法添加组件 " + spec.Type.FullName + "（Editor 程序集里的类不能挂载）"); }
                spec.Created = component;
            }
        }

        private static void ApplyProperties(NodeSpec node, Dictionary<string, NodeSpec> byPath, HashSet<string> ambiguous)
        {
            foreach (ComponentSpec spec in node.Components)
            {
                if (spec.Properties.Count == 0) { continue; }
                var changes = spec.Properties.Select(pair => new RevAutomationPropertyChange { property = pair.Key, value = Reference(pair.Value, node, byPath, ambiguous) }).ToArray();
                RevAutomationEditorModel.Set(spec.Created, changes, false);
            }
        }

        private static string Reference(string value, NodeSpec owner, Dictionary<string, NodeSpec> byPath, HashSet<string> ambiguous)
        {
            if (value == null || !value.StartsWith("@", StringComparison.Ordinal)) { return value; }
            string path = value.Substring(1);
            if (ambiguous.Contains(path)) { throw new ArgumentException("节点 " + owner.RelativePath + "：引用目标重名，请改名：" + path); }
            if (!byPath.TryGetValue(path, out NodeSpec target)) { throw new ArgumentException("节点 " + owner.RelativePath + "：引用目标不在本次 compose 中：" + path); }
            return RevAutomationEditorModel.Identity(target.Created);
        }

        // ---------------------------------------------------------------- 看见场景

        [Serializable] private sealed class CaptureOptions { public string id, objectPath, scene, view = "iso"; public int width = 1280, height = 720; }

        [RevAutomationAction("scene.capture", Environment = "either", Keys = new[] { "id", "objectPath", "scene", "view", "width", "height" },
            Description = "把场景渲染成临时 PNG 供 AI 查看：view 为 iso/top/front/side（自动框住全部 Renderer）、camera（用 id/objectPath 或主相机）、sceneview（当前 Scene 窗口）。不含 Screen Space Overlay UI")]
        /// <summary>渲染一张临时相机画面，释放纹理并返回临时截图位置和视角信息，调用端读完后负责删除。</summary>
        public static object Capture(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<CaptureOptions>(request.payloadJson ?? "{}");
            if (p.width < 16 || p.width > 4096 || p.height < 16 || p.height > 4096) { throw new ArgumentException("width/height 为 16..4096"); }
            var renderers = RevAutomationEditorModel.Objects().SelectMany(o => o.GetComponents<Renderer>()).Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            GameObject temporary = null;
            Camera camera;
            switch (p.view)
            {
                case "camera":
                    camera = !string.IsNullOrEmpty(p.id) || !string.IsNullOrEmpty(p.objectPath)
                        ? RevAutomationEditorModel.GameObject(new RevAutomationEditorOptions { id = p.id, objectPath = p.objectPath, scene = p.scene }).GetComponent<Camera>() : Camera.main;
                    if (camera == null) { throw new ArgumentException("没有可用相机：指定带 Camera 的对象，或把相机 Tag 设为 MainCamera"); }
                    break;
                case "sceneview":
                    camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
                    if (camera == null) { throw new InvalidOperationException("没有打开的 Scene 窗口；改用 view=iso"); }
                    break;
                case "iso": case "top": case "front": case "side":
                    temporary = new GameObject("RevAutomationCapture") { hideFlags = HideFlags.HideAndDontSave };
                    camera = temporary.AddComponent<Camera>();
                    Frame(camera, renderers, p.view, p.width / (float)p.height);
                    break;
                default: throw new ArgumentException("view 必须为 iso/top/front/side/camera/sceneview");
            }
            RenderTexture target = RenderTexture.GetTemporary(p.width, p.height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture, previousActive = RenderTexture.active;
            var image = new Texture2D(p.width, p.height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, p.width, p.height), 0, 0);
                image.Apply();
                // 写入本工单的唯一截图文件，不在 Unity 内存账本中保存 PNG。领取终态后由调用端读完即删。
                string work = RevAutomationBridge.JobDirectory(request.id);
                Directory.CreateDirectory(work);
                string path = Path.Combine(work, "scene-capture-" + Guid.NewGuid().ToString("N") + ".png");
                byte[] png = image.EncodeToPNG();
                if (png.Length > RevRuntimeScreenshot.MaxPngBytes)
                {
                    throw new InvalidOperationException("截图超过 8 MiB，请降低分辨率");
                }
                File.WriteAllBytes(path, png);
                return new Dictionary<string, object>
                {
                    ["path"] = path, ["bytes"] = png.Length, ["mimeType"] = "image/png", ["view"] = p.view, ["camera"] = camera.name,
                    ["width"] = p.width, ["height"] = p.height, ["renderers"] = renderers.Length,
                    ["limitation"] = "只含相机渲染内容，不含 Screen Space Overlay UI；画面需配合玩法断言"
                };
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                if (temporary != null) { Object.DestroyImmediate(temporary); }
            }
        }

        private static void Frame(Camera camera, Renderer[] renderers, string view, float aspect)
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            if (renderers.Length > 0)
            {
                bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) { bounds.Encapsulate(renderer.bounds); }
            }
            Vector3 direction = view == "top" ? Vector3.down : view == "front" ? Vector3.forward : view == "side" ? Vector3.left : new Vector3(-1, -1, 1).normalized;
            float radius = Mathf.Max(bounds.extents.magnitude, .5f);
            // 按较窄的一边算距离，横竖屏都能把包围球完整框进画面。
            float halfFov = Mathf.Min(camera.fieldOfView, Mathf.Rad2Deg * 2 * Mathf.Atan(Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad) * aspect)) * .5f;
            float distance = radius / Mathf.Sin(halfFov * Mathf.Deg2Rad) * 1.15f;
            camera.transform.position = bounds.center - direction * distance;
            camera.transform.rotation = Quaternion.LookRotation(direction, view == "top" ? Vector3.forward : Vector3.up);
            camera.nearClipPlane = Mathf.Max(.01f, distance - radius * 2);
            camera.farClipPlane = distance + radius * 2;
            camera.aspect = aspect;
        }

        [RevAutomationAction("sceneview.frame", Environment = "live", Keys = new[] { "id", "objectPath", "scene" }, Description = "选中对象并让 Scene 窗口框住它，方便人工查看 AI 搭好的内容")]
        public static object FrameInSceneView(RevAutomationRequest request)
        {
            GameObject go = RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request));
            SceneView view = SceneView.lastActiveSceneView ?? throw new InvalidOperationException("没有打开的 Scene 窗口");
            Selection.activeGameObject = go;
            view.FrameSelected();
            return RevAutomationEditorModel.Describe(go);
        }
    }
}
