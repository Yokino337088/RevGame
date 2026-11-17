using System;
using System.IO;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    internal static class RevGalAssets
    {
        public static RevGalStory Read(string path)
        {
            return AssetDatabase.LoadAssetAtPath<RevGalStory>(AssetPath(path, ".asset"))
                ?? throw new ArgumentException("找不到 RevGalStory 资产：" + path);
        }

        public static RevGalStory Write(string path, RevGalDefinition definition, bool overwrite)
        {
            path = AssetPath(path, ".asset");
            var errors = RevGalValidation.Validate(definition).Where(issue => !issue.Warning).ToArray();
            if (errors.Length > 0)
            {
                throw new ArgumentException(string.Join("；", errors.Select(issue => (issue.Node ?? "剧情") + "：" + issue.Message)));
            }

            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null || File.Exists(path))
            {
                if (!overwrite || !(existing is RevGalStory story))
                {
                    throw new InvalidOperationException("目标存在，请明确允许覆盖现有剧情 SO：" + path);
                }
                if (EditorUtility.IsDirty(story))
                {
                    throw new InvalidOperationException("目标剧情有未保存编辑，请先保存后再导入");
                }
                Undo.RecordObject(story, "导入 Gal 剧情");
                story.SetDefinition(definition);
                EditorUtility.SetDirty(story);
                AssetDatabase.SaveAssetIfDirty(story);
                return story;
            }

            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var created = ScriptableObject.CreateInstance<RevGalStory>();
            created.SetDefinition(definition);
            AssetDatabase.CreateAsset(created, path);
            Undo.RegisterCreatedObjectUndo(created, "创建 Gal 剧情");
            AssetDatabase.SaveAssetIfDirty(created);
            return created;
        }

        public static RevGalDefinition Template(string id, string title)
        {
            var definition = new RevGalDefinition { Id = id, Title = title, Start = "opening" };
            definition.Variables.Add(new RevGalVariable { Id = "trust" });
            var opening = new RevGalNode
            {
                Id = "opening", Kind = RevGalNodeKind.Line, Title = "初次见面", Speaker = "旅人",
                Text = "天快黑了。要沿灯光走，还是试试另一条路？", Next = "route", Position = new Vector2(40, 180)
            };
            var route = new RevGalNode
            {
                Id = "route", Kind = RevGalNodeKind.Choice, Title = "两条路", Text = "选一条路。",
                Position = new Vector2(320, 180)
            };
            var lamp = new RevGalEdge { Id = "lamp", Label = "沿灯光走", Target = "lamps" };
            lamp.Effects.Add(new RevGalEffect { Variable = "trust", Operation = RevGalOperation.Add, Value = 1 });
            route.Exits.Add(lamp);
            route.Exits.Add(new RevGalEdge { Id = "shortcut", Label = "试试近路", Target = "shortcut" });
            definition.Nodes.Add(opening);
            definition.Nodes.Add(route);
            definition.Nodes.Add(new RevGalNode
            {
                Id = "lamps", Kind = RevGalNodeKind.Line, Title = "灯下", Speaker = "旅人", Text = "灯一直亮着。我在这里等你。",
                Next = "return", Position = new Vector2(610, 50)
            });
            definition.Nodes.Add(new RevGalNode
            {
                Id = "shortcut", Kind = RevGalNodeKind.Line, Title = "林间", Text = "林间吹来风，远处亮起了星星。",
                Next = "stars", Position = new Vector2(610, 300)
            });
            definition.Nodes.Add(new RevGalNode
            {
                Id = "return", Kind = RevGalNodeKind.End, Title = "归途", Text = "结局 · 灯下归途", Position = new Vector2(895, 50)
            });
            definition.Nodes.Add(new RevGalNode
            {
                Id = "stars", Kind = RevGalNodeKind.End, Title = "夜色", Text = "结局 · 借一夜星光", Position = new Vector2(895, 300)
            });
            return definition;
        }

        public static string ResourceRoot()
        {
            var root = ABBuildConfig.Instance.resRoot?.TrimEnd('/');
            if (string.IsNullOrEmpty(root) || !AssetDatabase.IsValidFolder(root))
            {
                throw new InvalidOperationException("请在 RevAB 打包工具设置资源根目录");
            }
            return AssetPath(root, null);
        }

        public static string LogicalPath(UnityEngine.Object asset)
        {
            string root = ResourceRoot() + "/";
            string path = AssetDatabase.GetAssetPath(asset);
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                throw new ArgumentException("请选择资源根目录中的资产：" + root);
            }
            string relative = path.Substring(root.Length);
            string extension = Path.GetExtension(relative);
            return relative.Substring(0, relative.Length - extension.Length);
        }

        public static string AssetPath(string path, string extension)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)
                || path != path.Trim() || path.IndexOfAny(new[] { ':', '\\', '\n', '\r' }) >= 0
                || path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")
                || (extension != null && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("请选择 Assets 下的工程路径：" + path);
            }
            return path;
        }

        public static void EnsureFolder(string path)
        {
            if (path == "Assets" || AssetDatabase.IsValidFolder(path))
            {
                return;
            }
            AssetPath(path, null);
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))))
            {
                throw new IOException("创建资产目录失败：" + path);
            }
        }
    }
}
