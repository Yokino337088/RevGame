// ============================================================
// RevAutomationWorkspace —— 把 Hierarchy、Inspector、Project 与 Prefab 操作变成工单。
// 【为什么】Agent 不能依赖窗口按钮位置；这里把操作接回 Unity 官方编辑接口。
// 【怎么用】先查真实 id/属性，再执行；场景变脏要显式保存，删除需要 confirm。
// 【边界】不执行任意私有方法，不猜同名对象；Prefab 加载后无论成功失败都卸载。
// ============================================================
// 场景、Hierarchy、Inspector、Project 与 Prefab 的可调用入口。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Revolution.Editor
{
    public static class RevAutomationWorkspace
    {
        [Serializable] private sealed class SceneInfo { public string path, name; public int handle, roots; public bool loaded, dirty, active; }
        [Serializable] private sealed class SceneList { public SceneInfo[] scenes; }
        [Serializable] private sealed class AssetInfo { public string path, guid, type; public bool folder; }
        [Serializable] private sealed class AssetPage { public int total, offset; public bool truncated; public AssetInfo[] items; }

        private static SceneInfo DescribeScene(Scene scene) => new SceneInfo { path = scene.path, name = scene.name, handle = scene.handle,
            roots = scene.rootCount, loaded = scene.isLoaded, dirty = scene.isDirty, active = scene == SceneManager.GetActiveScene() };
        internal static Scene FindScene(string path)
        {
            if (string.IsNullOrEmpty(path)) { return SceneManager.GetActiveScene(); }
            var scene = SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded) { throw new ArgumentException("场景未加载：" + path); }
            return scene;
        }
        private static void RequireCleanScenes()
        {
            if (Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty))
            { throw new InvalidOperationException("存在未保存场景，请先 scene.save；自动化不会丢弃场景修改"); }
        }
        private static string ScenePath(string path)
        {
            RevAutomationEditorModel.AssetPath(path, true);
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("场景路径必须以 .unity 结尾"); }
            return path;
        }

        [RevAutomationAction("scene.list", Environment = "either", Keys = new string[0], Description = "已加载场景、活动场景及未保存状态")]
        public static object Scenes(RevAutomationRequest request) => new SceneList
        { scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => DescribeScene(SceneManager.GetSceneAt(i))).ToArray() };

        [RevAutomationAction("scene.hierarchy", Environment = "either", Keys = new[] { "scene", "query", "offset", "limit", "includeInactive" }, Description = "包含未激活对象的分页场景层级")]
        public static object Hierarchy(RevAutomationRequest request) => Find(request);

        [RevAutomationAction("scene.open", Keys = new[] { "path", "additive" }, Description = "打开现有场景，替换前检查未保存内容")]
        public static object Open(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            ScenePath(p.path);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(p.path) == null) { throw new ArgumentException("场景不存在：" + p.path); }
            if (!p.additive) { RequireCleanScenes(); }
            return DescribeScene(EditorSceneManager.OpenScene(p.path, p.additive ? OpenSceneMode.Additive : OpenSceneMode.Single));
        }

        [RevAutomationAction("scene.new", Keys = new[] { "additive" }, Description = "创建空场景（通过 scene.save 落盘）")]
        public static object NewScene(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            if (!p.additive) { RequireCleanScenes(); }
            return DescribeScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, p.additive ? NewSceneMode.Additive : NewSceneMode.Single));
        }

        [RevAutomationAction("scene.save", Keys = new[] { "scene", "path" }, Description = "保存指定或活动场景；新场景必须提供 path")]
        public static object SaveScene(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            Scene scene = FindScene(p.scene);
            string path = ScenePath(string.IsNullOrEmpty(p.path) ? scene.path : p.path);
            if (path != scene.path) { RevAutomationEditorModel.RequireNewAsset(path); }
            if (!EditorSceneManager.SaveScene(scene, path)) { throw new IOException("场景保存失败：" + path); }
            return DescribeScene(scene);
        }

        [RevAutomationAction("scene.close", Keys = new[] { "scene" }, Description = "关闭已保存场景，拒绝丢弃未保存内容")]
        public static object CloseScene(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            Scene scene = FindScene(p.scene);
            if (scene.isDirty) { throw new InvalidOperationException("场景尚未保存，请先 scene.save"); }
            if (SceneManager.sceneCount <= 1) { throw new InvalidOperationException("不能关闭最后一个场景，请先 scene.new 或 scene.open"); }
            if (!EditorSceneManager.CloseScene(scene, true)) { throw new InvalidOperationException("关闭场景失败"); }
            return Scenes(request);
        }

        [RevAutomationAction("scene.active", Keys = new[] { "scene" }, Description = "切换已加载的活动场景")]
        public static object ActiveScene(RevAutomationRequest request)
        {
            Scene scene = FindScene(RevAutomationEditorOptions.Read(request).scene);
            if (!SceneManager.SetActiveScene(scene)) { throw new InvalidOperationException("切换活动场景失败"); }
            return DescribeScene(scene);
        }

        [RevAutomationAction("gameobject.find", Environment = "either", Keys = new[] { "scene", "query", "type", "offset", "limit", "includeInactive" }, Description = "分页查找场景对象；返回当前会话的 id")]
        public static object Find(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            Type type = string.IsNullOrEmpty(p.type) ? null : RevAutomationEditorModel.FindType(p.type, typeof(Component));
            return RevAutomationEditorModel.Page(RevAutomationEditorModel.Objects().Where(o =>
                (p.includeInactive || o.activeInHierarchy) && (string.IsNullOrEmpty(p.scene) || o.scene.path == p.scene)
                && (string.IsNullOrEmpty(p.query) || RevAutomationEditorModel.HierarchyPath(o.transform).IndexOf(p.query, StringComparison.OrdinalIgnoreCase) >= 0)
                && (type == null || o.GetComponent(type) != null)).OrderBy(o => o.scene.path).ThenBy(o => RevAutomationEditorModel.HierarchyPath(o.transform)), p);
        }

        [RevAutomationAction("gameobject.create", Keys = new[] { "name", "parent", "scene" }, Description = "创建场景对象并支持 Undo")]
        public static object CreateObject(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            if (string.IsNullOrWhiteSpace(p.name)) { throw new ArgumentException("需要 name"); }
            Transform parent = Parent(p.parent);
            Scene scene = FindScene(p.scene);
            if (parent != null && !string.IsNullOrEmpty(p.scene) && parent.gameObject.scene != scene) { throw new ArgumentException("parent 与 scene 不一致"); }
            var go = new GameObject(p.name);
            SceneManager.MoveGameObjectToScene(go, parent != null ? parent.gameObject.scene : scene);
            if (parent != null) { go.transform.SetParent(parent, false); }
            Undo.RegisterCreatedObjectUndo(go, "Revolution Automation Create");
            EditorSceneManager.MarkSceneDirty(go.scene);
            return RevAutomationEditorModel.Describe(go);
        }

        [Serializable] public sealed class TransformInfo
        {
            public RevAutomationEditorModel.ObjectInfo target;
            public string parent;
            public float[] position, localPosition, eulerAngles, localEulerAngles, rotation, localRotation, localScale, lossyScale;
        }

        private static float[] Coordinates(Vector3 value) => new[] { value.x, value.y, value.z };
        private static float[] Coordinates(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
        private static TransformInfo DescribeTransform(Transform transform) => new TransformInfo
        {
            target = RevAutomationEditorModel.Describe(transform.gameObject),
            parent = transform.parent == null ? null : RevAutomationEditorModel.Identity(transform.parent.gameObject),
            position = Coordinates(transform.position), localPosition = Coordinates(transform.localPosition),
            eulerAngles = Coordinates(transform.eulerAngles), localEulerAngles = Coordinates(transform.localEulerAngles),
            rotation = Coordinates(transform.rotation), localRotation = Coordinates(transform.localRotation),
            localScale = Coordinates(transform.localScale), lossyScale = Coordinates(transform.lossyScale)
        };

        [RevAutomationAction("transform.get", Environment = "either", Keys = new[] { "id", "objectPath", "scene" }, Description = "读取对象的世界与局部位置、旋转、缩放和父节点身份")]
        public static object GetTransform(RevAutomationRequest request) => DescribeTransform(RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request)).transform);

        private static Vector3? VectorArgument(System.Collections.Generic.Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var raw)) { return null; }
            if (!(raw is System.Collections.Generic.List<object> items) || items.Count != 3)
            { throw new ArgumentException(key + " 必须为三个有限数字组成的数组"); }
            var coordinates = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!(items[i] is double value) || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
                { throw new ArgumentException(key + " 必须为 Single 范围内的有限数字，不能包含字符串或布尔值"); }
                coordinates[i] = (float)value;
            }
            return new Vector3(coordinates[0], coordinates[1], coordinates[2]);
        }

        private static void RequireFinite(Vector3 value)
        {
            if (float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z)
                || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z))
            { throw new ArgumentException("父级变换使目标局部位置溢出，不能写入非有限坐标"); }
        }

        [RevAutomationAction("transform.set", Keys = new[] { "id", "objectPath", "scene", "space", "position", "eulerAngles", "localScale" }, Description = "修改三维变换并支持 Undo；space 为 local/world，localScale 始终是局部缩放")]
        public static object SetTransform(RevAutomationRequest request)
        {
            // 先验证原始 JSON，不能让 JsonUtility 把错误的数组元素静默转成零；所有坐标都通过后才写入。
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            Vector3? position = VectorArgument(values, "position"), euler = VectorArgument(values, "eulerAngles"), scale = VectorArgument(values, "localScale");
            if (!position.HasValue && !euler.HasValue && !scale.HasValue) { throw new ArgumentException("需要 position、eulerAngles 或 localScale 至少一项"); }
            var p = RevAutomationEditorOptions.Read(request);
            if (p.space != "local" && p.space != "world") { throw new ArgumentException("space 必须为 local 或 world"); }
            Transform transform = RevAutomationEditorModel.GameObject(p).transform;
            Vector3 targetPosition = position ?? transform.localPosition;
            Quaternion targetRotation = euler.HasValue ? Quaternion.Euler(euler.Value) : transform.localRotation;
            if (p.space == "world" && transform.parent != null)
            {
                if (position.HasValue)
                {
                    // 零缩放父节点无法逆变换世界位置。拒绝而不是让 Unity 写出错误的局部坐标。
                    float determinant = transform.parent.localToWorldMatrix.determinant;
                    if (determinant == 0 || float.IsNaN(determinant) || float.IsInfinity(determinant))
                    { throw new ArgumentException("父节点变换不可逆，无法设置世界位置"); }
                    targetPosition = transform.parent.InverseTransformPoint(position.Value);
                }
                if (euler.HasValue) { targetRotation = Quaternion.Inverse(transform.parent.rotation) * targetRotation; }
            }
            if (position.HasValue) { RequireFinite(targetPosition); }
            if (euler.HasValue && Coordinates(targetRotation).Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            { throw new ArgumentException("旋转计算溢出，不能写入非有限四元数"); }
            Undo.RecordObject(transform, "Revolution Automation Transform");
            if (position.HasValue) { transform.localPosition = targetPosition; }
            if (euler.HasValue) { transform.localRotation = targetRotation; }
            if (scale.HasValue) { transform.localScale = scale.Value; }
            EditorUtility.SetDirty(transform);
            if (PrefabUtility.IsPartOfPrefabInstance(transform)) { PrefabUtility.RecordPrefabInstancePropertyModifications(transform); }
            EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);
            return DescribeTransform(transform);
        }

        [RevAutomationAction("gameobject.primitive", Keys = new[] { "type", "name", "parent", "scene" }, Description = "创建 Cube/Sphere/Capsule/Cylinder/Plane/Quad 原始体，保留网格、Collider 和 Undo")]
        public static object CreatePrimitive(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            // 只允许六个公开名称，拒绝数字枚举和组合字符串；预检失败不会留下半个对象。
            if (!new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" }.Contains(p.type))
            { throw new ArgumentException("type 必须为 Cube/Sphere/Capsule/Cylinder/Plane/Quad"); }
            if (string.IsNullOrWhiteSpace(p.name)) { throw new ArgumentException("需要 name"); }
            Transform parent = Parent(p.parent);
            Scene scene = FindScene(p.scene);
            if (parent != null && !string.IsNullOrEmpty(p.scene) && parent.gameObject.scene != scene)
            { throw new ArgumentException("parent 与 scene 不一致"); }
            GameObject go = GameObject.CreatePrimitive((PrimitiveType)Enum.Parse(typeof(PrimitiveType), p.type));
            try
            {
                go.name = p.name;
                SceneManager.MoveGameObjectToScene(go, parent != null ? parent.gameObject.scene : scene);
                if (parent != null) { go.transform.SetParent(parent, false); }
                Undo.RegisterCreatedObjectUndo(go, "Revolution Automation Primitive");
                EditorSceneManager.MarkSceneDirty(go.scene);
                return RevAutomationEditorModel.Describe(go);
            }
            catch
            {
                Object.DestroyImmediate(go);
                throw;
            }
        }

        internal static Transform Parent(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            return RevAutomationEditorModel.GameObject(new RevAutomationEditorOptions { id = id }).transform;
        }

        [RevAutomationAction("gameobject.inspect", Environment = "either", Keys = new[] { "id", "objectPath", "scene", "property", "offset", "limit" }, Description = "读取 GameObject Inspector 序列化属性")]
        public static object InspectObject(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            return RevAutomationEditorModel.Inspect(RevAutomationEditorModel.GameObject(p), p);
        }

        [RevAutomationAction("gameobject.set", Keys = new[] { "id", "objectPath", "scene", "changes" }, Description = "修改 GameObject 序列化属性并支持 Undo")]
        public static object SetObject(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var go = RevAutomationEditorModel.GameObject(p);
            RevAutomationEditorModel.Set(go, p.changes);
            return RevAutomationEditorModel.Describe(go);
        }

        [RevAutomationAction("gameobject.parent", Keys = new[] { "id", "objectPath", "scene", "parent", "worldPositionStays" }, Description = "调整父子关系；parent 为空移到场景根")]
        public static object Reparent(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var go = RevAutomationEditorModel.GameObject(p);
            Transform parent = Parent(p.parent);
            if (parent != null && (parent == go.transform || parent.IsChildOf(go.transform))) { throw new ArgumentException("不能设置自身或后代为父节点"); }
            if (parent != null && parent.gameObject.scene != go.scene) { throw new ArgumentException("跨场景移动请先在目标场景创建对象"); }
            Undo.SetTransformParent(go.transform, parent, p.worldPositionStays, "Revolution Automation Parent");
            return RevAutomationEditorModel.Describe(go);
        }

        [RevAutomationAction("gameobject.duplicate", Keys = new[] { "id", "objectPath", "scene", "name" }, Description = "复制场景对象并保留父节点")]
        public static object Duplicate(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var source = RevAutomationEditorModel.GameObject(p);
            var go = Object.Instantiate(source, source.transform.parent);
            if (go.transform.parent == null) { SceneManager.MoveGameObjectToScene(go, source.scene); }
            if (!string.IsNullOrEmpty(p.name)) { go.name = p.name; }
            Undo.RegisterCreatedObjectUndo(go, "Revolution Automation Duplicate");
            return RevAutomationEditorModel.Describe(go);
        }

        [RevAutomationAction("gameobject.delete", Keys = new[] { "id", "objectPath", "scene" }, Confirm = true, Description = "删除场景对象并支持 Undo")]
        public static object DeleteObject(RevAutomationRequest request)
        {
            var go = RevAutomationEditorModel.GameObject(RevAutomationEditorOptions.Read(request));
            var info = RevAutomationEditorModel.Describe(go);
            Undo.DestroyObjectImmediate(go);
            return info;
        }

        [RevAutomationAction("component.list", Environment = "either", Keys = new[] { "id", "objectPath", "scene", "offset", "limit" }, Description = "枚举对象组件及组件 id")]
        public static object Components(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            return RevAutomationEditorModel.Page(RevAutomationEditorModel.GameObject(p).GetComponents<Component>().Where(c => c != null), p);
        }

        private static Component Component(RevAutomationEditorOptions p)
        {
            if (!string.IsNullOrEmpty(p.id) && RevAutomationEditorModel.Resolve(p.id) is Component direct) { return direct; }
            var go = RevAutomationEditorModel.GameObject(p);
            Type type = RevAutomationEditorModel.FindType(p.type, typeof(Component));
            var components = go.GetComponents(type);
            if (p.index < 0 || p.index >= components.Length) { throw new ArgumentException("组件 index 超出范围"); }
            return components[p.index];
        }

        [RevAutomationAction("component.get", Environment = "either", Keys = new[] { "id", "objectPath", "scene", "type", "index", "property", "offset", "limit" }, Description = "读取组件属性、属性类型与可写状态")]
        public static object GetComponent(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            return RevAutomationEditorModel.Inspect(Component(p), p);
        }

        [RevAutomationAction("component.set", Keys = new[] { "id", "objectPath", "scene", "type", "index", "changes" }, Description = "经 SerializedObject 修改组件属性，保留 Prefab override 与 Undo")]
        public static object SetComponent(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            Component component = Component(p);
            if (EditorUtility.IsPersistent(component)) { throw new ArgumentException("Prefab 资产修改使用 prefab.modify"); }
            RevAutomationEditorModel.Set(component, p.changes);
            return RevAutomationEditorModel.Describe(component);
        }

        [RevAutomationAction("component.add", Keys = new[] { "id", "objectPath", "scene", "type" }, Description = "添加组件并返回组件 id")]
        public static object AddComponent(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var go = RevAutomationEditorModel.GameObject(p);
            Component component = Undo.AddComponent(go, RevAutomationEditorModel.FindType(p.type, typeof(Component)));
            if (component == null) { throw new InvalidOperationException("组件添加失败"); }
            return RevAutomationEditorModel.Describe(component);
        }

        [RevAutomationAction("component.remove", Keys = new[] { "id", "objectPath", "scene", "type", "index" }, Confirm = true, Description = "移除组件并支持 Undo")]
        public static object RemoveComponent(RevAutomationRequest request)
        {
            Component component = Component(RevAutomationEditorOptions.Read(request));
            if (component is Transform || EditorUtility.IsPersistent(component)) { throw new ArgumentException("不能移除 Transform 或直接修改 Prefab 资产"); }
            var info = RevAutomationEditorModel.Describe(component);
            Undo.DestroyObjectImmediate(component);
            return info;
        }

        [RevAutomationAction("component.types", Environment = "either", Keys = new[] { "query", "offset", "limit" }, Description = "发现可添加组件类型，消除类型名猜测")]
        public static object Types(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); p.CheckPage();
            return new RevAutomationActions.RevAutomationRows(TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && (string.IsNullOrEmpty(p.query) || t.FullName.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(t => t.FullName).OrderBy(n => n).Skip(p.offset).Take(p.limit));
        }

        [RevAutomationAction("asset.find", Environment = "either", Keys = new[] { "query", "folders", "offset", "limit" }, Description = "按 Unity 搜索语法查询资产，例如 t:Prefab")]
        public static object Assets(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); p.CheckPage();
            if (p.folders != null) { foreach (string folder in p.folders) { RevAutomationEditorModel.AssetPath(folder, false); if (!AssetDatabase.IsValidFolder(folder)) { throw new ArgumentException("目录不存在：" + folder); } } }
            var paths = AssetDatabase.FindAssets(p.query ?? "", p.folders ?? new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(s => s).ToArray();
            return new AssetPage { total = paths.Length, offset = p.offset, truncated = paths.Length > p.offset + p.limit,
                items = paths.Skip(p.offset).Take(p.limit).Select(path => new AssetInfo { path = path, guid = AssetDatabase.AssetPathToGUID(path),
                    type = AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName, folder = AssetDatabase.IsValidFolder(path) }).ToArray() };
        }

        [RevAutomationAction("asset.inspect", Environment = "either", Keys = new[] { "path", "property", "offset", "limit" }, Description = "读取资产 Inspector；Shader、材质、ScriptableObject 通用")]
        public static object InspectAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var asset = AssetDatabase.LoadMainAssetAtPath(RevAutomationEditorModel.AssetPath(p.path, false));
            if (asset == null) { throw new ArgumentException("资产不存在：" + p.path); }
            return RevAutomationEditorModel.Inspect(asset, p);
        }

        [RevAutomationAction("asset.set", Keys = new[] { "path", "changes" }, Description = "修改并保存资产序列化属性")]
        public static object SetAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var asset = AssetDatabase.LoadMainAssetAtPath(RevAutomationEditorModel.AssetPath(p.path, true));
            if (asset == null || asset is GameObject || asset is MonoScript) { throw new ArgumentException("资产不存在或应使用 prefab.modify/脚本文件编辑"); }
            RevAutomationEditorModel.Set(asset, p.changes);
            return RevAutomationEditorModel.Describe(asset);
        }

        [RevAutomationAction("asset.mkdir", Keys = new[] { "path" }, Description = "创建资产目录，父目录必须存在")]
        public static object MakeFolder(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); RevAutomationEditorModel.RequireNewAsset(p.path);
            string guid = AssetDatabase.CreateFolder(Path.GetDirectoryName(p.path).Replace('\\', '/'), Path.GetFileName(p.path));
            if (string.IsNullOrEmpty(guid)) { throw new IOException("创建目录失败"); }
            return new RevAutomationActions.RevAutomationText(AssetDatabase.GUIDToAssetPath(guid));
        }

        [RevAutomationAction("asset.copy", Keys = new[] { "path", "destination" }, Description = "复制资产，不覆盖既有目标")]
        public static object CopyAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            RevAutomationEditorModel.AssetPath(p.path, false); RevAutomationEditorModel.RequireNewAsset(p.destination);
            if (!AssetDatabase.CopyAsset(p.path, p.destination)) { throw new IOException("资产复制失败"); }
            return new RevAutomationActions.RevAutomationText(p.destination);
        }

        [RevAutomationAction("asset.move", Keys = new[] { "path", "destination" }, Description = "移动或重命名资产，保留 GUID 且不覆盖")]
        public static object MoveAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            RevAutomationEditorModel.AssetPath(p.path, true); RevAutomationEditorModel.RequireNewAsset(p.destination);
            string error = AssetDatabase.ValidateMoveAsset(p.path, p.destination);
            if (!string.IsNullOrEmpty(error)) { throw new ArgumentException(error); }
            error = AssetDatabase.MoveAsset(p.path, p.destination);
            if (!string.IsNullOrEmpty(error)) { throw new IOException(error); }
            return new RevAutomationActions.RevAutomationText(p.destination);
        }

        [RevAutomationAction("asset.delete", Keys = new[] { "path" }, Confirm = true, Description = "删除明确指定的资产或资产目录")]
        public static object DeleteAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); RevAutomationEditorModel.AssetPath(p.path, true);
            if (AssetDatabase.IsValidFolder(p.path) && Directory.GetDirectories(p.path, "*", SearchOption.AllDirectories)
                .Any(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)) { throw new ArgumentException("待删除目录包含目录链接"); }
            if (!AssetDatabase.DeleteAsset(p.path)) { throw new IOException("资产不存在或删除失败：" + p.path); }
            return new RevAutomationActions.RevAutomationText(p.path);
        }

        [RevAutomationAction("asset.import", Keys = new[] { "path" }, Description = "重新导入资产并应用 Importer 修改")]
        public static object ImportAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); RevAutomationEditorModel.AssetPath(p.path, true);
            if (!File.Exists(p.path) && !Directory.Exists(p.path)) { throw new ArgumentException("资产不存在"); }
            AssetDatabase.ImportAsset(p.path, ImportAssetOptions.ForceUpdate);
            return new RevAutomationActions.RevAutomationText("已请求导入；编译屏障使用 editor.wait");
        }

        [RevAutomationAction("asset.create", Keys = new[] { "path", "type" }, Description = "创建指定 ScriptableObject 类型的 .asset")]
        public static object CreateAsset(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); RevAutomationEditorModel.RequireNewAsset(p.path);
            if (!p.path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("需要 .asset 后缀"); }
            var obj = ScriptableObject.CreateInstance(RevAutomationEditorModel.FindType(p.type, typeof(ScriptableObject)));
            try { AssetDatabase.CreateAsset(obj, p.path); AssetDatabase.SaveAssets(); return RevAutomationEditorModel.Describe(obj); }
            catch { if (!EditorUtility.IsPersistent(obj)) { Object.DestroyImmediate(obj); } throw; }
        }

        [RevAutomationAction("asset.importer", Environment = "either", Keys = new[] { "path", "property", "offset", "limit" }, Description = "读取纹理/模型/音频等 Importer 设置")]
        public static object Importer(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var importer = AssetImporter.GetAtPath(RevAutomationEditorModel.AssetPath(p.path, false));
            if (importer == null) { throw new ArgumentException("Importer 不存在"); }
            return RevAutomationEditorModel.Inspect(importer, p);
        }

        [RevAutomationAction("asset.importer-set", Keys = new[] { "path", "changes" }, Description = "修改 Importer 并 SaveAndReimport")]
        public static object SetImporter(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var importer = AssetImporter.GetAtPath(RevAutomationEditorModel.AssetPath(p.path, true));
            if (importer == null) { throw new ArgumentException("Importer 不存在"); }
            RevAutomationEditorModel.Set(importer, p.changes, false);
            importer.SaveAndReimport();
            return new RevAutomationActions.RevAutomationText(p.path);
        }

        [Serializable] public sealed class ShaderInfo { public string name, path; public bool supported, findable; }
        [Serializable] public sealed class ShaderPage { public int total, offset; public bool truncated; public string limitation; public ShaderInfo[] items; }
        [Serializable] public sealed class MaterialPropertyInfo
        {
            public string name, type, texturePath;
            public float number;
            public int integer;
            public float[] vector, textureScale, textureOffset;
        }
        [Serializable] public sealed class MaterialInfo
        {
            public RevAutomationEditorModel.ObjectInfo target;
            public string shader;
            public int renderQueue;
            public string[] keywords;
            public MaterialPropertyInfo[] properties;
        }

        [RevAutomationAction("shader.list", Environment = "either", Keys = new[] { "query", "offset", "limit" }, Description = "分页查询资产与当前已加载 Shader 的真实名称、路径和支持状态，不宣称列全未加载内置 Shader")]
        public static object ListShaders(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); p.CheckPage();
            if (p.limit > 500) { throw new ArgumentException("Shader 查询 limit 为 1..500"); }
            var shaders = AssetDatabase.FindAssets("t:Shader").Select(guid => AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid)))
                .Concat(Resources.FindObjectsOfTypeAll<Shader>()).Where(shader => shader != null).Distinct()
                .Where(shader => string.IsNullOrEmpty(p.query) || shader.name.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(shader => shader.name, StringComparer.Ordinal).ThenBy(shader => AssetDatabase.GetAssetPath(shader), StringComparer.Ordinal).ToArray();
            return new ShaderPage { total = shaders.Length, offset = p.offset, truncated = shaders.Length > p.offset + p.limit,
                limitation = "包含资产与当前已加载 Shader；未加载内置 Shader 不保证列全。findable 表示 Shader.Find 能解析该名称；supported 不替代目标平台编译与视觉验证",
                items = shaders.Skip(p.offset).Take(p.limit).Select(shader => new ShaderInfo { name = shader.name,
                    path = AssetDatabase.GetAssetPath(shader), supported = shader.isSupported, findable = Shader.Find(shader.name) != null }).ToArray() };
        }

        private static string TextArgument(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var value) || !(value is string text) || string.IsNullOrWhiteSpace(text))
            { throw new ArgumentException(key + " 需要非空字符串"); }
            return text;
        }
        private static Material LoadMaterial(string path, bool write)
        {
            RevAutomationEditorModel.AssetPath(path, write);
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("需要 .mat 材质资产"); }
            return AssetDatabase.LoadAssetAtPath<Material>(path) ?? throw new ArgumentException("材质不存在：" + path);
        }
        private static Shader FindShader(string name) => Shader.Find(name) ?? throw new ArgumentException("Shader 名称无法解析；先 shader.list 选择 findable=true 的名称：" + name);
        private static void NewMaterialPath(string path)
        {
            RevAutomationEditorModel.RequireNewAsset(path);
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("需要 .mat 后缀"); }
        }

        private static MaterialInfo DescribeMaterial(Material material)
        {
            Shader shader = material.shader;
            if (shader == null) { throw new ArgumentException("材质没有有效 Shader"); }
            int count = shader.GetPropertyCount();
            if (count > 512) { throw new ArgumentException("Shader 属性超过 512 项输出边界"); }
            var properties = new List<MaterialPropertyInfo>();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                var property = new MaterialPropertyInfo { name = name, type = type.ToString() };
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range: property.number = material.GetFloat(name); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int: property.integer = material.GetInteger(name); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        Color color = material.GetColor(name); property.vector = new[] { color.r, color.g, color.b, color.a }; break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: Vector4 vector = material.GetVector(name); property.vector = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        Texture texture = material.GetTexture(name); property.texturePath = texture == null ? null : AssetDatabase.GetAssetPath(texture);
                        Vector2 scale = material.GetTextureScale(name), offset = material.GetTextureOffset(name);
                        property.textureScale = new[] { scale.x, scale.y }; property.textureOffset = new[] { offset.x, offset.y }; break;
                }
                properties.Add(property);
            }
            return new MaterialInfo { target = RevAutomationEditorModel.Describe(material), shader = shader.name, renderQueue = material.renderQueue,
                keywords = material.enabledKeywords.Select(keyword => keyword.name).OrderBy(name => name, StringComparer.Ordinal).ToArray(), properties = properties.ToArray() };
        }

        [RevAutomationAction("material.inspect", Environment = "either", Keys = new[] { "path" }, Description = "读取材质 Shader 声明的属性名、类型与当前值、关键字和渲染队列")]
        public static object InspectMaterial(RevAutomationRequest request) => DescribeMaterial(LoadMaterial(RevAutomationEditorOptions.Read(request).path, false));

        private static object PersistNewMaterial(Material material, string path)
        {
            try
            {
                material.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(material, path);
                AssetDatabase.SaveAssetIfDirty(material);
                return RevAutomationEditorModel.Describe(material);
            }
            catch { if (!EditorUtility.IsPersistent(material)) { Object.DestroyImmediate(material); } throw; }
        }

        [RevAutomationAction("material.create", Keys = new[] { "path", "shader" }, Description = "按真实 Shader 名创建新 .mat；拒绝覆盖，磁盘资产创建不属于 Undo")]
        public static object CreateMaterial(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            string path = TextArgument(values, "path"); NewMaterialPath(path);
            Shader shader = FindShader(TextArgument(values, "shader"));
            return PersistNewMaterial(new Material(shader), path);
        }

        [RevAutomationAction("material.clone", Keys = new[] { "path", "destination" }, Description = "克隆材质到新 .mat，保留源与其 GUID、拒绝覆盖；磁盘资产创建不属于 Undo")]
        public static object CloneMaterial(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            Material source = LoadMaterial(p.path, false); NewMaterialPath(p.destination);
            return PersistNewMaterial(new Material(source), p.destination);
        }

        private static float MaterialNumber(object raw)
        {
            if (!(raw is double value) || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
            { throw new ArgumentException("材质数值必须为 Single 范围内的有限数字"); }
            return (float)value;
        }
        private static Vector4 MaterialVector(object raw)
        {
            if (!(raw is List<object> values) || values.Count != 4) { throw new ArgumentException("Color/Vector 属性需要四个有限数字组成的数组"); }
            return new Vector4(MaterialNumber(values[0]), MaterialNumber(values[1]), MaterialNumber(values[2]), MaterialNumber(values[3]));
        }
        private static string[] MaterialKeywords(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var raw)) { return new string[0]; }
            if (!(raw is List<object> items) || items.Count > 256 || items.Any(item => !(item is string text) || string.IsNullOrWhiteSpace(text) || text.Length > 128))
            { throw new ArgumentException(key + " 需要最多 256 个非空关键字，每个最多 128 字符"); }
            return items.Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        }

        private static void ApplyMaterialProperty(Material candidate, string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128) { throw new ArgumentException("材质属性名需要 1..128 字符的非空字符串"); }
            int index = candidate.shader.FindPropertyIndex(name);
            if (index < 0) { throw new ArgumentException("Shader 未声明属性：" + name); }
            switch (candidate.shader.GetPropertyType(index))
            {
                case UnityEngine.Rendering.ShaderPropertyType.Float:
                case UnityEngine.Rendering.ShaderPropertyType.Range: candidate.SetFloat(name, MaterialNumber(value)); break;
                case UnityEngine.Rendering.ShaderPropertyType.Int:
                    if (!(value is double integer) || integer < int.MinValue || integer > int.MaxValue || Math.Truncate(integer) != integer)
                    { throw new ArgumentException(name + " 需要 Int32 整数"); }
                    candidate.SetInteger(name, (int)integer); break;
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                    Vector4 color = MaterialVector(value); candidate.SetColor(name, new Color(color.x, color.y, color.z, color.w)); break;
                case UnityEngine.Rendering.ShaderPropertyType.Vector: candidate.SetVector(name, MaterialVector(value)); break;
                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    Texture texture = null;
                    if (value != null)
                    {
                        if (!(value is string path)) { throw new ArgumentException(name + " 纹理需要资产路径字符串或 null"); }
                        texture = AssetDatabase.LoadAssetAtPath<Texture>(RevAutomationEditorModel.AssetPath(path, false));
                        if (texture == null) { throw new ArgumentException("纹理不存在或类型错误：" + path); }
                        var dimension = candidate.shader.GetPropertyTextureDimension(index);
                        if (dimension != UnityEngine.Rendering.TextureDimension.Any && dimension != texture.dimension)
                        { throw new ArgumentException(name + " 纹理维度应为 " + dimension + "，实际为 " + texture.dimension); }
                    }
                    candidate.SetTexture(name, texture); break;
                default: throw new ArgumentException("尚未支持的 Shader 属性类型：" + name);
            }
        }

        [RevAutomationAction("material.set", Keys = new[] { "path", "shader", "properties", "renderQueue", "enableKeywords", "disableKeywords" }, Description = "完整预检后修改材质 Shader 属性、关键字与队列，支持 Undo；properties 为名称到 typed 值的对象")]
        public static object SetMaterial(RevAutomationRequest request)
        {
            var values = RevAutomationJson.Object(RevAutomationJson.Parse(request.payloadJson ?? "{}"));
            Material source = LoadMaterial(TextArgument(values, "path"), true);
            var properties = values.TryGetValue("properties", out var raw) ? RevAutomationJson.Object(raw) : new Dictionary<string, object>();
            if (properties.Count > 512) { throw new ArgumentException("properties 最多 512 项"); }
            string[] enable = MaterialKeywords(values, "enableKeywords"), disable = MaterialKeywords(values, "disableKeywords");
            if (enable.Intersect(disable, StringComparer.Ordinal).Any()) { throw new ArgumentException("同一个关键字不能同时启用和禁用"); }
            if (properties.Count == 0 && !values.ContainsKey("shader") && !values.ContainsKey("renderQueue") && enable.Length == 0 && disable.Length == 0)
            { throw new ArgumentException("material.set 需要至少一项修改"); }
            // 所有试写发生在不持久化的候选材质上。后面的坏属性/坏纹理/坏关键字绝不会先改动源资产。
            var candidate = new Material(source);
            try
            {
                if (values.ContainsKey("shader")) { candidate.shader = FindShader(TextArgument(values, "shader")); }
                if (candidate.shader == null) { throw new ArgumentException("材质没有有效 Shader"); }
                foreach (var property in properties) { ApplyMaterialProperty(candidate, property.Key, property.Value); }
                if (values.TryGetValue("renderQueue", out var queue))
                {
                    if (!(queue is double number) || Math.Truncate(number) != number || number < -1 || number > 5000)
                    { throw new ArgumentException("renderQueue 需要 -1..5000 整数，-1 表示继承 Shader"); }
                    candidate.renderQueue = (int)number;
                }
                foreach (string keyword in enable.Concat(disable))
                {
                    var local = candidate.shader.keywordSpace.FindKeyword(keyword);
                    if (!local.isValid) { throw new ArgumentException("Shader 未声明关键字：" + keyword); }
                    candidate.SetKeyword(local, enable.Contains(keyword));
                }
                DescribeMaterial(candidate); // 输出上限也在修改源资产前检查。
                Undo.RecordObject(source, "Revolution Automation Material");
                source.shader = candidate.shader;
                source.CopyPropertiesFromMaterial(candidate);
                EditorUtility.SetDirty(source);
                AssetDatabase.SaveAssetIfDirty(source);
                return DescribeMaterial(source);
            }
            finally { Object.DestroyImmediate(candidate); }
        }

        private static GameObject Prefab(string path)
        {
            RevAutomationEditorModel.AssetPath(path, false);
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("需要 .prefab 资产"); }
            return AssetDatabase.LoadAssetAtPath<GameObject>(path) ?? throw new ArgumentException("Prefab 不存在：" + path);
        }

        [RevAutomationAction("prefab.instantiate", Keys = new[] { "path", "scene", "parent" }, Description = "将 Prefab 实例化到指定场景")]
        public static object InstantiatePrefab(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var source = Prefab(p.path);
            Transform parent = Parent(p.parent);
            Scene scene = FindScene(p.scene);
            if (parent != null && !string.IsNullOrEmpty(p.scene) && parent.gameObject.scene != scene) { throw new ArgumentException("parent 与 scene 不一致"); }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(source, parent != null ? parent.gameObject.scene : scene);
            if (parent != null) { go.transform.SetParent(parent, false); }
            Undo.RegisterCreatedObjectUndo(go, "Revolution Automation Prefab");
            return RevAutomationEditorModel.Describe(go);
        }

        [RevAutomationAction("prefab.create", Keys = new[] { "id", "objectPath", "scene", "path" }, Description = "将场景对象保存为新 Prefab")]
        public static object CreatePrefab(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request); RevAutomationEditorModel.RequireNewAsset(p.path);
            if (!p.path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("需要 .prefab 后缀"); }
            var prefab = PrefabUtility.SaveAsPrefabAsset(RevAutomationEditorModel.GameObject(p), p.path, out bool success);
            if (!success || prefab == null) { throw new IOException("Prefab 保存失败"); }
            return RevAutomationEditorModel.Describe(prefab);
        }

        [RevAutomationAction("prefab.inspect", Keys = new[] { "path", "objectPath", "type", "index", "property", "offset", "limit" }, Description = "加载 Prefab 内容读取子节点或组件属性，随后卸载")]
        public static object InspectPrefab(RevAutomationRequest request) => WithPrefab(request, false);
        [RevAutomationAction("prefab.modify", Keys = new[] { "path", "objectPath", "type", "index", "changes" }, Description = "修改 Prefab 子节点或组件属性并保存，不需打开窗口")]
        public static object ModifyPrefab(RevAutomationRequest request) => WithPrefab(request, true);
        private static object WithPrefab(RevAutomationRequest request, bool write)
        {
            var p = RevAutomationEditorOptions.Read(request);
            Prefab(p.path);
            if (write) { RevAutomationEditorModel.AssetPath(p.path, true); }
            var root = PrefabUtility.LoadPrefabContents(p.path);
            try
            {
                var matches = root.GetComponentsInChildren<Transform>(true).Where(t =>
                    string.IsNullOrEmpty(p.objectPath) ? t == root.transform : AnimationUtility.CalculateTransformPath(t, root.transform) == p.objectPath).ToArray();
                if (matches.Length != 1) { throw new ArgumentException("Prefab 子路径不存在或不唯一：" + p.objectPath); }
                Object target = matches[0].gameObject;
                if (!string.IsNullOrEmpty(p.type))
                {
                    var components = matches[0].GetComponents(RevAutomationEditorModel.FindType(p.type, typeof(Component)));
                    if (p.index < 0 || p.index >= components.Length) { throw new ArgumentException("Prefab 组件 index 超出范围"); }
                    target = components[p.index];
                }
                if (!write) { var result = RevAutomationEditorModel.Inspect(target, p); result.target.id = null; result.target.instanceId = 0; return result; }
                RevAutomationEditorModel.Set(target, p.changes, false);
                PrefabUtility.SaveAsPrefabAsset(root, p.path, out bool success);
                if (!success) { throw new IOException("Prefab 保存失败"); }
                return new RevAutomationActions.RevAutomationText(p.path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static Object Settings(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("ProjectSettings/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\') || !path.EndsWith(".asset", StringComparison.Ordinal))
            { throw new ArgumentException("需要 ProjectSettings/*.asset 路径"); }
            return AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault() ?? throw new ArgumentException("项目设置不存在：" + path);
        }

        [RevAutomationAction("settings.get", Keys = new[] { "path", "property", "offset", "limit" }, Description = "读取 Player/Tags/Layers/Input/Graphics 等项目设置")]
        public static object GetSettings(RevAutomationRequest request)
        { var p = RevAutomationEditorOptions.Read(request); return RevAutomationEditorModel.Inspect(Settings(p.path), p); }
        [RevAutomationAction("settings.set", Keys = new[] { "path", "changes" }, Description = "经序列化接口修改并保存项目设置")]
        public static object SetSettings(RevAutomationRequest request)
        { var p = RevAutomationEditorOptions.Read(request); var target = Settings(p.path); RevAutomationEditorModel.Set(target, p.changes); return RevAutomationEditorModel.Inspect(target, p); }

        [RevAutomationAction("editor.selection", Environment = "live", Keys = new string[0], Description = "读取编辑器当前选择")]
        public static object SelectionGet(RevAutomationRequest request) => RevAutomationEditorModel.Page(Selection.objects, new RevAutomationEditorOptions());
        [RevAutomationAction("editor.select", Environment = "live", Keys = new[] { "id" }, Description = "选择场景对象、组件或资产")]
        public static object Select(RevAutomationRequest request)
        { Selection.activeObject = RevAutomationEditorModel.Resolve(RevAutomationEditorOptions.Read(request).id); return SelectionGet(request); }
        [RevAutomationAction("editor.undo", Environment = "live", Keys = new string[0], Description = "撤销一次编辑器操作")]
        public static object UndoOnce(RevAutomationRequest request)
        { RevAutomationActions.RequireEditMode(); Undo.PerformUndo(); return new RevAutomationActions.RevAutomationText("已撤销"); }
        [RevAutomationAction("editor.redo", Environment = "live", Keys = new string[0], Description = "重做一次编辑器操作")]
        public static object RedoOnce(RevAutomationRequest request)
        { RevAutomationActions.RequireEditMode(); Undo.PerformRedo(); return new RevAutomationActions.RevAutomationText("已重做"); }
    }
}
