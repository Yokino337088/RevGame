using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    /// <summary>为使用自有界面的宿主读取剧情字段和资源需求。报告只查看当前工程的本地素材。</summary>
    public static class RevGalHostContract
    {
        private sealed class Media
        {
            public string Path;
            public Type Type;
            public readonly List<Dictionary<string, object>> Uses = new List<Dictionary<string, object>>();
        }

        /// <summary>供宿主构建系统检查节点及舞台方案的动态资源。resourceRoot 传项目自己的 Unity 资源目录。</summary>
        public static IEnumerable<(string LogicalPath, Type AssetType, string AssetPath, string Status)> InspectMedia(RevGalStory story, string resourceRoot)
        {
            if (story == null)
            {
                throw new ArgumentNullException(nameof(story));
            }
            var definition = story.Definition;
            var nodes = (definition?.Nodes ?? new List<RevGalNode>()).Where(node => node != null).ToArray();
            var presets = (definition?.StagePresets ?? new List<RevGalStagePreset>()).Where(preset => preset != null).ToArray();
            foreach (var media in CollectMedia(nodes, presets))
            {
                var (assetPath, status) = ResolveMedia(media, resourceRoot?.TrimEnd('/'));
                yield return (media.Path, media.Type, assetPath, status);
            }
        }

        internal static Dictionary<string, object> Describe(RevGalStory story)
        {
            string assetPath = AssetDatabase.GetAssetPath(story);
            var definition = story.Definition;
            var result = RevGalAuthoring.Describe(assetPath, definition);
            // 接入清单沿用工程现存设置，也可检查位于资源根之外的草稿。
            string root = RevGalAssets.ConfiguredResourceRoot();
            result["logicalPath"] = RevGalAssets.LogicalPathInRoot(assetPath, root);
            result["resourceRoot"] = root;
            result["revision"] = definition?.Revision;
            result["variables"] = (definition?.Variables ?? new List<RevGalVariable>())
                .Where(variable => variable != null).Select(variable => new Dictionary<string, object>
                {
                    ["id"] = variable.Id, ["initial"] = variable.Initial
                }).ToArray();

            var nodes = (definition?.Nodes ?? new List<RevGalNode>()).Where(node => node != null).ToArray();
            result["signals"] = nodes.Where(node => node.Kind == RevGalNodeKind.Signal)
                .Select(node => new Dictionary<string, object>
                {
                    ["node"] = node.Id, ["name"] = node.Signal, ["argument"] = node.Argument
                }).ToArray();
            var presets = (definition?.StagePresets ?? new List<RevGalStagePreset>()).Where(preset => preset != null).ToArray();
            result["stagePresets"] = presets;
            result["media"] = CollectMedia(nodes, presets).Select(item => DescribeMedia(item, root)).ToArray();
            return result;
        }

        private static IEnumerable<Media> CollectMedia(RevGalNode[] nodes, RevGalStagePreset[] presets)
        {
            var media = new Dictionary<(string Path, Type Type), Media>();
            foreach (var node in nodes)
            {
                Register(node, "Background", node.Stage?.Background, typeof(Sprite));
                Register(node, "BackgroundMaterial", node.Stage?.BackgroundMaterial, typeof(Material));
                Register(node, "Left", node.Stage?.Left, typeof(Sprite));
                Register(node, "Center", node.Stage?.Center, typeof(Sprite));
                Register(node, "Right", node.Stage?.Right, typeof(Sprite));
                Register(node, "Music", node.Stage?.Music, typeof(AudioClip));
                Register(node, "Voice", node.Voice, typeof(AudioClip));
                Register(node, "Sound", node.Sound, typeof(AudioClip));
            }
            foreach (var preset in presets)
            {
                // 入边显示为方案 ID，便于作者查到小节继承的素材来源。
                var holder = new RevGalNode { Id = "方案/" + preset.Id, Stage = preset.Stage };
                Register(holder, "Background", preset.Stage?.Background, typeof(Sprite));
                Register(holder, "BackgroundMaterial", preset.Stage?.BackgroundMaterial, typeof(Material));
                Register(holder, "Music", preset.Stage?.Music, typeof(AudioClip));
                Register(holder, "Left", preset.Stage?.Left, typeof(Sprite));
                Register(holder, "Center", preset.Stage?.Center, typeof(Sprite));
                Register(holder, "Right", preset.Stage?.Right, typeof(Sprite));
            }
            return media.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ThenBy(entry => entry.Type.Name, StringComparer.Ordinal);

            void Register(RevGalNode node, string slot, string path, Type type)
            {
                if (string.IsNullOrEmpty(path) || path == RevGalStage.Clear)
                {
                    return;
                }
                if (!media.TryGetValue((path, type), out var entry))
                {
                    entry = new Media { Path = path, Type = type };
                    media.Add((path, type), entry);
                }
                entry.Uses.Add(new Dictionary<string, object> { ["node"] = node.Id, ["slot"] = slot });
            }
        }

        private static Dictionary<string, object> DescribeMedia(Media media, string root)
        {
            var (assetPath, status) = ResolveMedia(media, root);
            return new Dictionary<string, object>
            {
                ["path"] = media.Path, ["type"] = media.Type.Name, ["uses"] = media.Uses,
                ["status"] = status, ["assetPath"] = assetPath,
                ["bundle"] = assetPath == null ? null : AssetDatabase.GetImplicitAssetBundleName(assetPath)
            };
        }

        private static (string AssetPath, string Status) ResolveMedia(Media media, string root)
        {
            if (!RevGalValidation.IsLogicalPath(media.Path) || media.Path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return (null, "invalid-path");
            }
            if (string.IsNullOrEmpty(root))
            {
                return (null, "resource-root-unconfigured");
            }
            string logicalFile = root + "/" + media.Path;
            string directory = Path.GetDirectoryName(logicalFile);
            string name = Path.GetFileName(logicalFile);
            var matches = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory).Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileNameWithoutExtension(path) == name).Select(path => path.Replace('\\', '/')).Take(2).ToArray()
                : Array.Empty<string>();
            if (matches.Length != 1)
            {
                return (null, matches.Length == 0 ? "not-found" : "ambiguous");
            }
            return (matches[0], AssetDatabase.LoadAssetAtPath(matches[0], media.Type) != null ? "found" : "type-mismatch");
        }
    }
}
