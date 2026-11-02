using System;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static partial class GameEndlessBuild
    {
        private const string EyeRoot = "Assets/GameRes/Art/Endless/HeartEye/";
        /// <summary>按真实64×64源帧导入，完整矩形+正确Alpha；20帧归唯一无尽专用SpriteAtlas V2。</summary>
        private static void ConfigureHeartFrames()
        {
            for (int i = 0; i < 20; i++)
            {
                string path = EyeRoot + "HeartEye_" + i.ToString("D2") + ".png";
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException("棋心眼帧未导入：" + path);
                }
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 64;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.alphaIsTransparency = true;
                importer.filterMode = FilterMode.Point;
                importer.SaveAndReimport();
            }
        }
        /// <summary>棋心标记与血条绑定到Arena。Sprite保持资产引用，运行时由图集服务提供像素。</summary>
        private static void BuildHeart(Transform root, SerializedObject arena)
        {
            var frames = arena.FindProperty("heartFrames");
            frames.arraySize = 20;
            for (int i = 0; i < 20; i++)
            {
                frames.GetArrayElementAtIndex(i).objectReferenceValue = RequireAsset<Sprite>(EyeRoot + "HeartEye_" + i.ToString("D2") + ".png");
            }
            arena.FindProperty("heartEye").objectReferenceValue = HeartSprite(root, "棋心红色独眼", (Sprite)frames.GetArrayElementAtIndex(0).objectReferenceValue, new Vector3(4, 4, -.8f), Vector3.one);
            Sprite bar = RequireAsset<Sprite>(PlacementSpritePath);
            SpriteRenderer track = HeartSprite(root, "棋心血条底", bar, new Vector3(4, 4.55f, -.7f), new Vector3(.88f, .11f, 1));
            track.color = new Color(.2f, .08f, .05f);
            SpriteRenderer fill = HeartSprite(root, "棋心血条", bar, new Vector3(4, 4.55f, -.8f), new Vector3(.82f, .08f, 1));
            fill.color = new Color(.7f, .1f, .14f);
            arena.FindProperty("heartTrack").objectReferenceValue = track;
            arena.FindProperty("heartBar").objectReferenceValue = fill;
        }
        private static SpriteRenderer HeartSprite(Transform root, string name, Sprite sprite, Vector3 position, Vector3 scale)
        {
            var node = new GameObject(name, typeof(SpriteRenderer));
            node.transform.SetParent(root, false);
            node.transform.localPosition = position;
            node.transform.localScale = scale;
            SpriteRenderer renderer = node.GetComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = 20;
            return renderer;
        }
    }
}
