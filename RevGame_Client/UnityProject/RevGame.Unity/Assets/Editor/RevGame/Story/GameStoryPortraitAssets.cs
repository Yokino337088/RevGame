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
            var atlasNames = ReadAtlasNames();
            foreach (string guid in AssetDatabase.FindAssets("t:StoryBook", new[] { "Assets/GameRes/Config/Story" }))
            {
                StoryBook book = AssetDatabase.LoadAssetAtPath<StoryBook>(AssetDatabase.GUIDToAssetPath(guid));
                if (UpdateBook(book, atlasNames))
                {
                    EditorUtility.SetDirty(book);
                }
            }
            foreach (string guid in AssetDatabase.FindAssets("t:YayaExpressionCatalog", new[] { "Assets/GameRes/Config/Yaya" }))
            {
                var catalog = AssetDatabase.LoadAssetAtPath<YayaExpressionCatalog>(AssetDatabase.GUIDToAssetPath(guid));
                bool changed = false;
                foreach (var entry in catalog.Entries)
                {
                    var reference = ReadyReference(entry.Reference, entry.Sprite, atlasNames);
                    if (!reference.IsSet)
                    {
                        throw new InvalidOperationException("互动目录缺少表情：" + entry.Id);
                    }
                    changed |= !entry.Reference.Equals(reference) || entry.Sprite != null;
                    entry.Reference = reference;
                    entry.Sprite = null;
                }
                if (catalog.AtlasNames.Length > 0)
                {
                    catalog.AtlasNames = Array.Empty<string>();
                    changed = true;
                }
                if (changed)
                {
                    EditorUtility.SetDirty(catalog);
                }
            }
            MigratePvpStandingPortrait();
        }

        /// <summary>删除由旧配方装配的默认引用。新 PvP 面板加载时由台词目录交付同一个切片。</summary>
        private static void MigratePvpStandingPortrait()
        {
            const string path = "Assets/GameRes/UI/Panel/Pvp/PvpPanel.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var sprite = prefab?.transform.Find("yayaPortrait")?.GetComponent<UnityEngine.UI.Image>()?.sprite;
            if (sprite == null || sprite.name != "YayaExpression_26")
            {
                return;
            }
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                root.transform.Find("yayaPortrait").GetComponent<UnityEngine.UI.Image>().sprite = null;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>导入先在临时对象上推导图集，校验完整后才写入现有资产。</summary>
        internal static bool UpdateBook(StoryBook book)
        {
            return UpdateBook(book, ReadAtlasNames());
        }

        private static Dictionary<string, string> ReadAtlasNames()
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
            return atlasNames;
        }

        private static bool UpdateBook(StoryBook book, Dictionary<string, string> atlasNames)
        {
            book.Validate();
            var guide = ReadyReference(book.GuideReference, book.Guide, atlasNames);
            var timeline = new GamePortraitReference[book.ExpressionIds.Length];
            for (int i = 0; i < book.ExpressionIds.Length; i++)
            {
                var mapped = book.ExpressionReference(book.ExpressionIds[i], out _);
                timeline[i] = ReadyReference(mapped.Reference, mapped.Legacy, atlasNames);
            }
            bool changed = !guide.Equals(book.GuideReference) || book.Guide != null || (book.GuideFrames?.Length ?? 0) > 0
                || !timeline.SequenceEqual(book.GuideFrameReferences ?? Array.Empty<GamePortraitReference>());
            book.GuideReference = guide;
            book.GuideFrameReferences = timeline;
            book.Guide = null;
            book.GuideFrames = Array.Empty<Sprite>();
            foreach (StoryBook.Episode episode in book.Episodes)
            {
                var episodePortrait = ReadyReference(episode.PortraitReference, episode.Portrait, atlasNames);
                changed |= !episodePortrait.Equals(episode.PortraitReference) || episode.Portrait != null;
                episode.PortraitReference = episodePortrait;
                episode.Portrait = null;
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (StoryBook.Line line in episode.Lines)
                {
                    var linePortrait = ReadyReference(line.PortraitReference, line.Portrait, atlasNames);
                    changed |= !linePortrait.Equals(line.PortraitReference) || line.Portrait != null;
                    line.PortraitReference = linePortrait;
                    line.Portrait = null;
                    names.Add(book.ResolvePortraitReference(episode, line, out _).Reference.Atlas);
                }
                string[] generated = names.ToArray();
                if (episode.PortraitAtlasNames == null || !episode.PortraitAtlasNames.SequenceEqual(generated))
                {
                    episode.PortraitAtlasNames = generated;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>保留选中的具体切片。新选图片的包归属在资源配置时派生。</summary>
        internal static GamePortraitReference ReferenceFor(Sprite sprite)
        {
            if (sprite == null)
            {
                return default;
            }
            string path = AssetDatabase.GetAssetPath(sprite);
            if (!path.StartsWith("Assets/GameRes/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("请选用 GameRes 中的正式立绘：" + path);
            }
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long id))
            {
                throw new InvalidOperationException("立绘切片无法定位：" + path);
            }
            return new GamePortraitReference { AssetGuid = guid, LocalId = id, SpriteName = sprite.name };
        }

        private static GamePortraitReference ReadyReference(GamePortraitReference reference, Sprite legacy, Dictionary<string, string> atlasNames)
        {
            if (!reference.IsSet)
            {
                reference = ReferenceFor(legacy);
            }
            if (!reference.IsSet)
            {
                return reference;
            }
            string path = AssetDatabase.GUIDToAssetPath(reference.AssetGuid);
            var sprite = StoryBook.EditorSprite(reference);
            string bundle = AssetDatabase.GetImplicitAssetBundleName(path);
            if (sprite == null || !atlasNames.TryGetValue(bundle, out string atlas))
            {
                throw new InvalidOperationException("立绘切片或活动图集缺失：" + path + " / " + reference.SpriteName);
            }
            reference.SpriteName = sprite.name;
            reference.Atlas = atlas;
            return reference;
        }
    }
}
