// 结构化编辑器参数与对象身份；只通过 Unity 的序列化/Undo 接口修改 Inspector。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Revolution.Editor
{
    [Serializable] public sealed class RevAutomationPropertyChange { public string property; public string value; }
    [Serializable] public sealed class RevAutomationSceneEntry { public string path; public bool enabled = true; }
    [Serializable]
    public sealed class RevAutomationEditorOptions
    {
        public string id, objectPath, path, destination, query, type, name, parent, scene, property, value, severity;
        public string target, mode = "Single", text;
        public int offset, limit = 200, index, port = 18766;
        public string runtimeAction, argumentsJson;
        public bool additive, includeInactive = true, worldPositionStays = true;
        public bool submit, development;
        public string[] folders, testNames, categories, assemblies;
        public RevAutomationPropertyChange[] changes;
        public RevAutomationSceneEntry[] scenes;
        public static RevAutomationEditorOptions Read(RevAutomationRequest request) =>
            JsonUtility.FromJson<RevAutomationEditorOptions>(request.payloadJson ?? "{}") ?? throw new ArgumentException("参数必须是对象");
        public void CheckPage()
        {
            if (offset < 0 || limit < 1 || limit > 2000) { throw new ArgumentException("offset >= 0，limit 为 1..2000"); }
            if (severity != null && severity != "error" && severity != "warning" && severity != "log")
            { throw new ArgumentException("severity 为 error/warning/log"); }
        }
    }

    public static class RevAutomationEditorModel
    {
        // instance:<数字> 是本次 Unity 会话内的临时对象身份。删除、重载、换场景后必须重查。
        // objectPath 适合人阅读，但同名层级可能不唯一；因此 Resolve/查找会拒绝模糊匹配。
        // Inspector 修改走 SerializedObject + Undo，让 Unity 的脏标记和撤销机制正常工作。
        [Serializable] public sealed class ObjectInfo
        {
            public string id, name, type, path, scene;
            public int instanceId;
            public bool active, persistent;
        }
        [Serializable] public sealed class ObjectPage { public int total, offset; public bool truncated; public ObjectInfo[] items; }
        [Serializable] public sealed class PropertyInfo { public string property, type, value; public bool editable; }
        [Serializable] public sealed class PropertyPage { public ObjectInfo target; public int total, offset; public bool truncated; public PropertyInfo[] properties; }

        public static IEnumerable<GameObject> Objects() => Resources.FindObjectsOfTypeAll<GameObject>().Where(o =>
            o.scene.IsValid() && o.scene.isLoaded && !EditorUtility.IsPersistent(o) && !EditorSceneManager.IsPreviewScene(o.scene)
            && (o.hideFlags & HideFlags.HideAndDontSave) == 0);

        public static string HierarchyPath(Transform t) => t.parent == null ? t.name : HierarchyPath(t.parent) + "/" + t.name;
        public static string Identity(Object obj) => "instance:" + obj.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        public static ObjectInfo Describe(Object obj)
        {
            GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
            return new ObjectInfo { id = Identity(obj), instanceId = obj.GetInstanceID(), name = obj.name, type = obj.GetType().FullName,
                persistent = EditorUtility.IsPersistent(obj), path = go != null ? HierarchyPath(go.transform) : AssetDatabase.GetAssetPath(obj),
                scene = go != null ? go.scene.path : null, active = go != null && go.activeInHierarchy };
        }

        public static Object Resolve(string id)
        {
            Object obj = null;
            if (id != null && id.StartsWith("instance:", StringComparison.Ordinal) && int.TryParse(id.Substring(9), out int instance))
            { obj = EditorUtility.InstanceIDToObject(instance); }
            else if (id != null && id.StartsWith("Assets/", StringComparison.Ordinal))
            { AssetPath(id, false); obj = AssetDatabase.LoadMainAssetAtPath(id); }
            else if (GlobalObjectId.TryParse(id ?? "", out GlobalObjectId global)) { obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(global); }
            if (obj == null) { throw new ArgumentException("对象不存在或身份已过期，请重新查询：" + id); }
            return obj;
        }

        public static GameObject GameObject(RevAutomationEditorOptions p)
        {
            if (!string.IsNullOrEmpty(p.id))
            {
                Object obj = Resolve(p.id);
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null || EditorUtility.IsPersistent(go)) { throw new ArgumentException("需要已加载场景中的对象"); }
                return go;
            }
            if (string.IsNullOrEmpty(p.objectPath)) { throw new ArgumentException("需要 id 或 objectPath"); }
            var matches = Objects().Where(o => HierarchyPath(o.transform) == p.objectPath
                && (string.IsNullOrEmpty(p.scene) || o.scene.path == p.scene)).Take(2).ToArray();
            if (matches.Length != 1) { throw new ArgumentException("路径不存在或有重名，请用查询返回的 id：" + p.objectPath); }
            return matches[0];
        }

        public static Type FindType(string name, Type baseType)
        {
            var types = TypeCache.GetTypesDerivedFrom(baseType).Concat(new[] { baseType })
                .Where(t => (t.FullName == name || t.Name == name || t.AssemblyQualifiedName == name) && !t.IsAbstract && !t.ContainsGenericParameters).ToArray();
            if (types.Length != 1) { throw new ArgumentException("类型不存在、不唯一或不可实例化，请使用完整类型名：" + name); }
            return types[0];
        }

        public static string AssetPath(string path, bool write)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Split('/').Any(p => p == ".." || p == "."))
            { throw new ArgumentException("需要规范的 Assets/... 路径：" + path); }
            if (write) { RevAutomationActions.RequireAssetPath(path); }
            else if (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
            { throw new ArgumentException("资产查询限定在 Assets 或 Packages"); }
            if (write)
            {
                for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))); dir != null; dir = dir.Parent)
                {
                    if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("写入路径不能经过目录链接：" + path); }
                    if (dir.FullName == Path.GetFullPath(".")) { break; }
                }
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                { throw new ArgumentException("不能写入目录链接：" + path); }
            }
            return path;
        }

        public static void RequireNewAsset(string path)
        {
            AssetPath(path, true);
            if (File.Exists(path) || Directory.Exists(path)) { throw new ArgumentException("目标已存在：" + path); }
            if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(path).Replace('\\', '/'))) { throw new ArgumentException("父目录不存在：" + path); }
        }

        public static ObjectPage Page(IEnumerable<Object> objects, RevAutomationEditorOptions p)
        {
            p.CheckPage();
            var items = objects.ToArray();
            return new ObjectPage { total = items.Length, offset = p.offset, truncated = items.Length > p.offset + p.limit,
                items = items.Skip(p.offset).Take(p.limit).Select(Describe).ToArray() };
        }

        public static PropertyPage Inspect(Object target, RevAutomationEditorOptions p)
        {
            p.CheckPage();
            var rows = new List<PropertyInfo>();
            using (var serialized = new SerializedObject(target))
            {
                SerializedProperty iterator = serialized.GetIterator();
                while (iterator.NextVisible(true))
                {
                    if (!string.IsNullOrEmpty(p.property) && iterator.propertyPath != p.property && !iterator.propertyPath.StartsWith(p.property + ".", StringComparison.Ordinal)) { continue; }
                    rows.Add(new PropertyInfo { property = iterator.propertyPath, type = iterator.propertyType.ToString(),
                        editable = iterator.editable && iterator.propertyPath != "m_Script", value = PropertyValue(iterator) });
                }
            }
            return new PropertyPage { target = Describe(target), total = rows.Count, offset = p.offset, truncated = rows.Count > p.offset + p.limit,
                properties = rows.Skip(p.offset).Take(p.limit).ToArray() };
        }

        private static string PropertyValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character: return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return p.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference: return p.objectReferenceValue == null ? "null" : Identity(p.objectReferenceValue);
                case SerializedPropertyType.Vector2: return JsonUtility.ToJson(p.vector2Value);
                case SerializedPropertyType.Vector3: return JsonUtility.ToJson(p.vector3Value);
                case SerializedPropertyType.Vector4: return JsonUtility.ToJson(p.vector4Value);
                case SerializedPropertyType.Quaternion: return JsonUtility.ToJson(p.quaternionValue);
                case SerializedPropertyType.Color: return JsonUtility.ToJson(p.colorValue);
                case SerializedPropertyType.Rect: return JsonUtility.ToJson(p.rectValue);
                default: return null;
            }
        }

        public static void Set(Object target, RevAutomationPropertyChange[] changes, bool save = true)
        {
            if (changes == null || changes.Length == 0) { throw new ArgumentException("需要非空 changes：[{property,value}]；value 使用字符串"); }
            using (var serialized = new SerializedObject(target))
            {
                serialized.Update();
                foreach (var change in changes)
                {
                    if (change == null || string.IsNullOrWhiteSpace(change.property) || change.property == "m_Script") { throw new ArgumentException("非法 property"); }
                    var property = serialized.FindProperty(change.property);
                    if (property == null || !property.editable) { throw new ArgumentException("属性不存在或只读：" + change.property); }
                    SetValue(property, change.value);
                }
                Undo.RecordObject(target, "Revolution Automation Inspector");
                serialized.ApplyModifiedProperties();
            }
            EditorUtility.SetDirty(target);
            if (PrefabUtility.IsPartOfPrefabInstance(target)) { PrefabUtility.RecordPrefabInstancePropertyModifications(target); }
            var go = target as GameObject ?? (target as Component)?.gameObject;
            if (go != null && go.scene.IsValid() && !EditorApplication.isPlaying) { EditorSceneManager.MarkSceneDirty(go.scene); }
            if (save && EditorUtility.IsPersistent(target)) { AssetDatabase.SaveAssets(); }
        }

        private static void SetValue(SerializedProperty p, string value)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character:
                    long integer = long.Parse(value, CultureInfo.InvariantCulture);
                    if (p.propertyType == SerializedPropertyType.ArraySize && (integer < 0 || integer > 100000)) { throw new ArgumentOutOfRangeException("value", "数组长度为 0..100000"); }
                    p.longValue = integer; break;
                case SerializedPropertyType.Boolean: p.boolValue = bool.Parse(value); break;
                case SerializedPropertyType.Float:
                    double number = double.Parse(value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(number) || double.IsInfinity(number)) { throw new ArgumentException("数值必须有限"); }
                    p.doubleValue = number; break;
                case SerializedPropertyType.String: p.stringValue = value ?? ""; break;
                case SerializedPropertyType.Enum: p.intValue = int.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.ObjectReference: p.objectReferenceValue = value == "null" || string.IsNullOrEmpty(value) ? null : Resolve(value); break;
                case SerializedPropertyType.Vector2: p.vector2Value = JsonUtility.FromJson<Vector2>(value); break;
                case SerializedPropertyType.Vector3: p.vector3Value = JsonUtility.FromJson<Vector3>(value); break;
                case SerializedPropertyType.Vector4: p.vector4Value = JsonUtility.FromJson<Vector4>(value); break;
                case SerializedPropertyType.Quaternion: p.quaternionValue = JsonUtility.FromJson<Quaternion>(value); break;
                case SerializedPropertyType.Color: p.colorValue = JsonUtility.FromJson<Color>(value); break;
                case SerializedPropertyType.Rect: p.rectValue = JsonUtility.FromJson<Rect>(value); break;
                default: throw new NotSupportedException("此属性使用子字段或 Array.data[n] 修改：" + p.propertyPath + " (" + p.propertyType + ")");
            }
        }
    }
}
