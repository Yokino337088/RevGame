using System;
using System.Collections.Generic;
using System.Linq;
using Revolution.Gal;
using Revolution.Gal.Editor;
using UnityEditor;

namespace RevGame.Editor
{
    /// <summary>在项目重新打标时恢复 Gal 的资源归属。剧情各自按需加载，默认面板和主题共用一包。</summary>
    internal static class GameGalResourceBuild
    {
        internal static void Configure(Dictionary<string, string> desired, string root)
        {
            var packages = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, story) in FindStories(root))
            {
                var errors = RevGalValidation.Validate(story.Definition).Where(issue => !issue.Warning).ToArray();
                if (errors.Length != 0)
                {
                    throw new InvalidOperationException("Gal 剧情校验失败：" + path + "，" + errors[0].Message);
                }
                string bundle = "gal_story_" + story.Definition.Id.ToLowerInvariant().Replace('.', '_').Replace('-', '_');
                if (packages.TryGetValue(bundle, out string other))
                {
                    throw new InvalidOperationException("Gal 剧情 ID 归一后重名：" + path + "，" + other + "，包=" + bundle);
                }
                packages.Add(bundle, path);
                desired[path] = bundle;
            }
            AssignFolder(RevGal.PanelRoot, "gal_ui");
            AssignFolder("Material/Gal", "gal_material");
            AssignFolder("Audio/Voice/Gal", "gal_voice");

            void AssignFolder(string relativePath, string bundle)
            {
                string path = root + "/" + relativePath;
                if (AssetDatabase.IsValidFolder(path))
                {
                    desired[path] = bundle;
                }
            }
        }

        /// <summary>Gal 的媒体以逻辑路径存储。纳入同一份引用快照，复用项目的图集和依赖门禁。</summary>
        internal static void AddMediaDependencies(GameResourceUsage.Snapshot snapshot, string root)
        {
            foreach (var (path, story) in FindStories(root))
            {
                foreach (var media in RevGalHostContract.InspectMedia(story, root))
                {
                    if (media.Status != "found")
                    {
                        throw new InvalidOperationException("Gal 媒体不可用：" + path + " → " + media.LogicalPath
                            + " (" + media.AssetType.Name + ", " + media.Status + ")");
                    }
                    foreach (string dependency in AssetDatabase.GetDependencies(media.AssetPath, true))
                    {
                        snapshot.Add(dependency, path);
                    }
                }
            }
        }

        private static IEnumerable<(string Path, RevGalStory Story)> FindStories(string root)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:RevGalStory", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var story = AssetDatabase.LoadAssetAtPath<RevGalStory>(path);
                if (story != null)
                {
                    yield return (path, story);
                }
            }
        }
    }
}
