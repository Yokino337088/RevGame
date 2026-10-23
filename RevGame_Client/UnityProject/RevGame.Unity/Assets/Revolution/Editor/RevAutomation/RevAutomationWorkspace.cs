// ============================================================
// RevAutomationWorkspace —— 把 Hierarchy、Inspector、Project 与 Prefab 操作变成工单。
// 【为什么】Agent 不能依赖窗口按钮位置；这里把操作接回 Unity 官方编辑接口。
// 【怎么用】先查真实 id/属性，再执行；场景变脏要显式保存，删除需要 confirm。
// 【边界】不执行任意私有方法，不猜同名对象；Prefab 加载后无论成功失败都卸载。
// ============================================================
// 场景、Hierarchy、Inspector、Project 与 Prefab 的可调用入口。
using System;
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
        private static Scene FindScene(string path)
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

        private static Transform Parent(string id)
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
