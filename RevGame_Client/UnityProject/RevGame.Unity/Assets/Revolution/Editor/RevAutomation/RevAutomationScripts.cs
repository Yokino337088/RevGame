// ============================================================
// RevAutomationScripts —— 写脚本、挂脚本、读写文本、导入外部文件。
// 【为什么】全链路开发绕不开"写一个 MonoBehaviour → 等编译 → 挂到对象上 → 连引用"。
//          component.add 只能按已编译的类型名找，AI 刚写完的脚本要从路径找到类，这里补上这一段。
// 【怎么用】script.create → editor.wait → script.attach（或 scene.compose 里写 script 路径）→ component.set。
// 【边界】这里只是写工程文件，产物是普通 .cs，走 Unity 正常编译、能被 git 看到；不提供 eval，
//        也不在内存里执行任何代码。写入限定 Assets，覆盖已有文件需要 confirm，
//        场景/Prefab/材质这类序列化资产拒绝文本写入，必须走对应动作，否则 GUID 与引用会被写坏。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Revolution.Editor
{
    public static class RevAutomationScripts
    {
        private const int MaxTextBytes = 1024 * 1024;
        private static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly Regex QualifiedName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$");
        private static readonly string[] SerializedExtensions = { ".meta", ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim", ".overrideController", ".physicMaterial",
            ".physicsMaterial2D", ".playable", ".mixer", ".renderTexture", ".spriteatlas", ".spriteatlasv2", ".lighting", ".preset", ".terrainlayer", ".mask", ".guiskin",
            ".fontsettings", ".shadervariants", ".cubemap", ".signal", ".flare", ".brush", ".giparams", ".mesh" };

        /// <summary>
        /// 扩展名清单永远列不全，所以再看内容：Unity 的 YAML 序列化资产都以 %YAML 开头。
        /// 已有文件是 YAML 就拒绝覆盖（会写坏 GUID 和引用），新写入的内容是 YAML 也拒绝（伪造的引用同样会坏）。
        /// </summary>
        private static void RejectSerialized(string path, string text)
        {
            if (SerializedExtensions.Contains(Path.GetExtension(path ?? ""), StringComparer.OrdinalIgnoreCase)) { throw new ArgumentException("序列化资产不能按文本写入，会破坏 GUID 与引用：" + path); }
            if ((text ?? "").TrimStart('\uFEFF').StartsWith("%YAML", StringComparison.Ordinal)) { throw new ArgumentException("内容是 Unity 序列化 YAML，不能按文本写入：" + path); }
            if (File.Exists(path))
            {
                using (var reader = new StreamReader(path))
                {
                    var head = new char[5];
                    int read = reader.Read(head, 0, 5);
                    if (new string(head, 0, read) == "%YAML") { throw new ArgumentException("目标是 Unity 序列化资产，不能按文本覆盖：" + path); }
                }
            }
        }

        [Serializable] private sealed class ScriptOptions { public string path, className, nameSpace, baseClass = "MonoBehaviour", text, script, id, objectPath, scene, source; public int offset; }
        [Serializable] public sealed class ScriptInfo { public string path, guid; public bool overwritten, willCompile; public string next; }
        [Serializable] public sealed class TextInfo { public string path, text; public long bytes; }

        /// <summary>沿路径逐级建目录：AssetDatabase.CreateFolder 要求父目录已存在。</summary>
        internal static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) { return; }
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) { throw new ArgumentException("目录必须在 Assets 内：" + folder); }
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(folder)))) { throw new IOException("创建目录失败：" + folder); }
        }

        private static bool PrepareWrite(string path, bool confirm)
        {
            RevAutomationEditorModel.AssetPath(path, true);
            if (Directory.Exists(path)) { throw new ArgumentException("目标是目录：" + path); }
            bool exists = File.Exists(path);
            if (exists && !confirm) { throw new InvalidOperationException("目标已存在，覆盖需要 --confirm：" + path); }
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            return exists;
        }

        private static string Template(ScriptOptions p)
        {
            var body = new StringBuilder();
            string indent = string.IsNullOrEmpty(p.nameSpace) ? "" : "    ";
            body.Append(indent).Append("public class ").Append(p.className).Append(" : ").Append(p.baseClass).Append('\n');
            body.Append(indent).Append("{\n").Append(indent).Append("}\n");
            var text = new StringBuilder("using UnityEngine;\n\n");
            if (string.IsNullOrEmpty(p.nameSpace)) { return text.Append(body).ToString(); }
            return text.Append("namespace ").Append(p.nameSpace).Append("\n{\n").Append(body).Append("}\n").ToString();
        }

        [RevAutomationAction("script.create", Keys = new[] { "path", "className", "nameSpace", "baseClass", "text" },
            Description = "在 Assets 下写 C# 脚本并触发编译：给 text 写入原文，否则按 className/nameSpace/baseClass 生成空类；自动建父目录，覆盖需要 confirm。写完用 editor.wait 等编译，再 script.attach")]
        public static object Create(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<ScriptOptions>(request.payloadJson ?? "{}");
            if (string.IsNullOrEmpty(p.path) || !p.path.EndsWith(".cs", StringComparison.Ordinal)) { throw new ArgumentException("path 需要 Assets 下的 .cs 文件"); }
            string fileClass = Path.GetFileNameWithoutExtension(p.path);
            if (string.IsNullOrEmpty(p.text))
            {
                p.className = string.IsNullOrEmpty(p.className) ? fileClass : p.className;
                if (!Identifier.IsMatch(p.className)) { throw new ArgumentException("className 不是合法标识符：" + p.className); }
                // MonoBehaviour 的类名必须与文件名一致，否则 Unity 找不到脚本，挂载时才报错。
                if (p.className != fileClass) { throw new ArgumentException("className 必须与文件名一致：" + fileClass); }
                if (!string.IsNullOrEmpty(p.nameSpace) && !QualifiedName.IsMatch(p.nameSpace)) { throw new ArgumentException("nameSpace 不合法：" + p.nameSpace); }
                if (!QualifiedName.IsMatch(p.baseClass ?? "")) { throw new ArgumentException("baseClass 不合法：" + p.baseClass); }
            }
            bool overwritten = PrepareWrite(p.path, request.confirm);
            File.WriteAllText(p.path, string.IsNullOrEmpty(p.text) ? Template(p) : p.text, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(p.path);
            return new ScriptInfo { path = p.path, guid = AssetDatabase.AssetPathToGUID(p.path), overwritten = overwritten, willCompile = true, next = "editor.wait，然后 script.attach" };
        }

        /// <summary>从 .cs 路径找到可挂载的类。scene.compose 共用，所以错误信息要能让调用方知道下一步做什么。</summary>
        internal static Type ScriptClass(string path)
        {
            RevAutomationEditorModel.AssetPath(path, false);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path) ?? throw new ArgumentException("脚本不存在：" + path);
            Type type = script.GetClass();
            if (type == null) { throw new InvalidOperationException("脚本还没有可用的类：先 editor.wait 等编译，并确认类名与文件名一致且没有编译错误：" + path); }
            if (!typeof(Component).IsAssignableFrom(type) || type.IsAbstract) { throw new ArgumentException("脚本不是可挂载的组件：" + type.FullName); }
            return type;
        }

        [RevAutomationAction("script.attach", Keys = new[] { "id", "objectPath", "scene", "script" },
            Description = "把已编译的脚本（Assets 下 .cs 路径）挂到场景对象并支持 Undo，返回组件 id，随后用 component.set 连字段")]
        public static object Attach(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            var options = JsonUtility.FromJson<ScriptOptions>(request.payloadJson ?? "{}");
            Type type = ScriptClass(options.script);
            GameObject go = RevAutomationEditorModel.GameObject(p);
            Component component = Undo.AddComponent(go, type);
            if (component == null) { throw new InvalidOperationException("Unity 拒绝添加组件：脚本在 Editor 程序集里不能挂载，或受 DisallowMultipleComponent 限制：" + type.FullName); }
            EditorSceneManager.MarkSceneDirty(go.scene);
            return RevAutomationEditorModel.Describe(component);
        }

        [RevAutomationAction("editor.recompile", Keys = new string[0], Description = "请求重新编译全部脚本；随后用 editor.wait 等待，用 editor.compilation 读错误")]
        public static object Recompile(RevAutomationRequest request)
        {
            CompilationPipeline.RequestScriptCompilation();
            return new RevAutomationActions.RevAutomationText("已请求编译；用 editor.wait 等待完成");
        }

        [RevAutomationAction("asset.read-text", Environment = "either", Keys = new[] { "path" }, Description = "读取 Assets 或 Packages 下的 UTF-8 文本文件，最大 1 MiB，二进制文件拒绝")]
        public static object ReadText(RevAutomationRequest request)
        {
            string path = RevAutomationEditorOptions.Read(request).path;
            RevAutomationEditorModel.AssetPath(path, false);
            if (!File.Exists(path)) { throw new FileNotFoundException("文件不存在", path); }
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxTextBytes) { throw new InvalidOperationException("文件超过 1 MiB：" + path); }
            if (Array.IndexOf(bytes, (byte)0) >= 0) { throw new InvalidOperationException("文件不是文本：" + path); }
            return new TextInfo { path = path, bytes = bytes.Length, text = new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF') };
        }

        [RevAutomationAction("asset.write-text", Keys = new[] { "path", "text" },
            Description = "写 Assets 下的 UTF-8 文本文件并导入，自动建父目录，覆盖需要 confirm；.meta/.unity/.prefab/.asset/.mat 等序列化资产拒绝，请用对应动作")]
        public static object WriteText(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<ScriptOptions>(request.payloadJson ?? "{}");
            string extension = Path.GetExtension(p.path ?? "");
            if (p.text == null) { throw new ArgumentException("需要 text"); }
            RevAutomationEditorModel.AssetPath(p.path, true);
            RejectSerialized(p.path, p.text);
            if (Encoding.UTF8.GetByteCount(p.text) > MaxTextBytes) { throw new ArgumentException("text 超过 1 MiB"); }
            bool overwritten = PrepareWrite(p.path, request.confirm);
            File.WriteAllText(p.path, p.text, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(p.path);
            return new ScriptInfo { path = p.path, guid = AssetDatabase.AssetPathToGUID(p.path), overwritten = overwritten, willCompile = extension == ".cs" };
        }

        [RevAutomationAction("asset.import-file", Keys = new[] { "source", "path" },
            Description = "把工程外的文件（贴图、模型、音频等）复制进 Assets 并导入；source 是本机绝对路径，覆盖需要 confirm，单文件上限 2 GiB")]
        public static object ImportFile(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<ScriptOptions>(request.payloadJson ?? "{}");
            if (string.IsNullOrEmpty(p.source) || !Path.IsPathRooted(p.source)) { throw new ArgumentException("source 需要本机绝对路径"); }
            var source = new FileInfo(p.source);
            if (!source.Exists) { throw new FileNotFoundException("源文件不存在", p.source); }
            if (source.Length > int.MaxValue) { throw new ArgumentException("源文件超过 2 GiB"); }
            if (string.Equals(Path.GetExtension(p.path), ".meta", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("不能导入 .meta"); }
            RevAutomationEditorModel.AssetPath(p.path, true);
            // 覆盖已有的序列化资产同样会换掉它的内容但保留旧 GUID 的引用关系，拒绝。新文件不嗅探：模型、贴图本来就是二进制。
            if (File.Exists(p.path)) { RejectSerialized(p.path, null); }
            bool overwritten = PrepareWrite(p.path, request.confirm);
            File.Copy(source.FullName, p.path, overwritten);
            AssetDatabase.ImportAsset(p.path, ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadMainAssetAtPath(p.path);
            if (asset == null) { throw new InvalidOperationException("文件已复制但 Unity 没能导入，请读 editor.console：" + p.path); }
            return RevAutomationEditorModel.Describe(asset);
        }
    }
}
