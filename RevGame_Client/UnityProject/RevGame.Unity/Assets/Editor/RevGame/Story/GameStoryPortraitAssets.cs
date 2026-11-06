// 立绘可被策划换成任意在用Sprite，因此图集列表从真实配置和唯一分包标记生成，不能写死某个角色图集。
using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.Data;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>图集配置完成后的剧情资源契约生成器；缺少图集归属直接阻止构建，不允许真机白图。</summary>
    public static class GameStoryPortraitAssets
    {
        /// <summary>逐篇解析实际显示图片并查唯一活动图集；只更新派生列表，不覆盖用户立绘、编号或对白。</summary>
        public static void Configure()
        {
            var atlasNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:SpriteAtlas", new[] { "Assets/GameRes/Atlases" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string bundle = AssetDatabase.GetImplicitAssetBundleName(path);
                if (!string.IsNullOrEmpty(bundle))
                {
                    atlasNames.Add(bundle, System.IO.Path.GetFileNameWithoutExtension(path));
                }
            }
            foreach (string guid in AssetDatabase.FindAssets("t:StoryBook", new[] { "Assets/GameRes/Config/Story" }))
            {
                StoryBook book = AssetDatabase.LoadAssetAtPath<StoryBook>(AssetDatabase.GUIDToAssetPath(guid));
                book.Validate();
                bool changed = false;
                foreach (StoryBook.Episode episode in book.Episodes)
                {
                    var names = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (StoryBook.Line line in episode.Lines)
                    {
                        Sprite portrait = book.ResolvePortrait(episode, line, out _);
                        string path = AssetDatabase.GetAssetPath(portrait);
                        string bundle = AssetDatabase.GetImplicitAssetBundleName(path);
                        if (!atlasNames.TryGetValue(bundle, out string name))
                        {
                            throw new InvalidOperationException("立绘没有唯一活动Sprite图集，请放入GameRes/Art规范目录：" + episode.Id + " → " + path);
                        }
                        names.Add(name);
                    }
                    string[] generated = names.ToArray();
                    if (episode.PortraitAtlasNames == null || !episode.PortraitAtlasNames.SequenceEqual(generated))
                    {
                        episode.PortraitAtlasNames = generated;
                        changed = true;
                    }
                }
                if (changed)
                {
                    EditorUtility.SetDirty(book);
                }
            }
        }
    }
}
