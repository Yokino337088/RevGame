using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        // 角色随内置场景进入 Player。图集不在 GameRes 的 AB 图集根目录下。
        private const string DuelAnimationFolder = "Assets/Scenes/PvpDuelAnimations";
        private const string DuelFighterAtlasPath = DuelAnimationFolder + "/DuelFighters.spriteatlasv2";
        private const int DuelFighterAtlasPageSize = 2048;

        [Serializable]
        public sealed class DuelFighterAtlasResult
        {
            public string atlasPath;
            public string buildTarget;
            public bool includeInBuild;
            public int packedSprites;
            public int texturePages;
            public DuelAnimationSequenceResult[] sequences;
        }

        [Serializable]
        public sealed class DuelAnimationSequenceResult
        {
            public string role;
            public string pose;
            public string clipPath;
            public int frameCount;
            public string firstSprite;
            public string lastSprite;
            public Vector2 frameSize;
            public Vector2 normalizedPivot;
        }

        /// <summary>确认原来的切片和动画引用，生成内置决斗角色图集并实际打包读回。</summary>
        public static DuelFighterAtlasResult BuildDuelFighterAtlas()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("请在编辑模式构建决斗角色图集。");
            }

            Sprite[][] swordsman = DuelPoses.Select(LoadDuelSequence).ToArray();
            Sprite[][] assassin = DuelPoses.Select(pose => LoadDuelSequence("Assassin" + pose)).ToArray();
            var groups = new List<DuelAnimationSequenceResult>(DuelPoses.Length * 2);
            ValidateDuelRoleClips("Swordsman", "", swordsman, groups);
            ValidateDuelRoleClips("Assassin", "Assassin", assassin, groups);
            ValidateDuelSceneAnimationSlices(swordsman, assassin);

            // 白名单只来自黑白两个角色的七种动作。擂台和招式 Shader 的独立采样纹理不会入图集。
            Sprite[] sprites = swordsman.Concat(assassin).SelectMany(frames => frames).ToArray();
            string[] paths = sprites.Select(AssetDatabase.GetAssetPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (paths.Distinct(StringComparer.Ordinal).Count() != sprites.Length)
            {
                throw new InvalidOperationException("同一张决斗切片被重复加入角色图集。");
            }
            SpriteAtlas atlas = ConfigureDuelFighterAtlas(paths);
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, target);
            atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(DuelFighterAtlasPath);
            int pages = ValidatePackedDuelFighters(atlas, sprites);
            AssetDatabase.SaveAssets();
            return new DuelFighterAtlasResult
            {
                atlasPath = DuelFighterAtlasPath,
                buildTarget = target.ToString(),
                includeInBuild = ((SpriteAtlasImporter)AssetImporter.GetAtPath(DuelFighterAtlasPath)).includeInBuild,
                packedSprites = sprites.Length,
                texturePages = pages,
                sequences = groups.ToArray()
            };
        }

        // 原始图片是一图一帧，保留原 Sprite 和 GUID。核对连续序号、居中轴心和同角色尺寸。
        private static void ValidateDuelRoleClips(string role, string prefix, Sprite[][] sequences, List<DuelAnimationSequenceResult> groups)
        {
            Vector2 size = sequences[0][0].rect.size;
            float pixelsPerUnit = sequences[0][0].pixelsPerUnit;
            for (int i = 0; i < DuelPoses.Length; i++)
            {
                string pose = DuelPoses[i];
                Sprite[] frames = sequences[i];
                for (int frame = 0; frame < frames.Length; frame++)
                {
                    Sprite sprite = frames[frame];
                    string path = AssetDatabase.GetAssetPath(sprite);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (sprite.name != $"{prefix}{pose}_{frame:D2}" ||
                        sprite.rect.size != size || sprite.pivot != size * .5f || !Mathf.Approximately(sprite.pixelsPerUnit, pixelsPerUnit) ||
                        importer == null || importer.textureType != TextureImporterType.Sprite ||
                        importer.spriteImportMode != SpriteImportMode.Single || importer.mipmapEnabled ||
                        !string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(path)))
                    {
                        throw new InvalidOperationException("决斗切片的顺序、画布、轴心或归属异常：" + path);
                    }
                }
                string clipPath = $"{DuelAnimationFolder}/{role}_{pose}.anim";
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                ValidateDuelClipFrames(clip, frames, clipPath);
                groups.Add(new DuelAnimationSequenceResult
                {
                    role = role, pose = pose, clipPath = clipPath, frameCount = frames.Length,
                    firstSprite = frames[0].name, lastSprite = frames[frames.Length - 1].name,
                    frameSize = size, normalizedPivot = new Vector2(.5f, .5f)
                });
            }
        }

        // Legacy 动画和显式换帧表都必须使用同一批切片。最后一个关键帧重复末图，保留末图时长。
        private static void ValidateDuelClipFrames(AnimationClip clip, Sprite[] frames, string clipPath)
        {
            var binding = EditorCurveBinding.PPtrCurve("", typeof(Image), "m_Sprite");
            ObjectReferenceKeyframe[] keys = clip == null ? null : AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (keys == null || keys.Length != frames.Length + 1 || !clip.legacy || !Mathf.Approximately(clip.frameRate, 15))
            {
                throw new InvalidOperationException("决斗动画帧表或采样率异常：" + clipPath);
            }
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i].value != frames[Math.Min(i, frames.Length - 1)] ||
                    !Mathf.Approximately(keys[i].time, i / clip.frameRate))
                {
                    throw new InvalidOperationException("决斗动画关键帧顺序异常：" + clipPath + " 第 " + i + " 帧");
                }
            }
        }

        // 只读打开目标场景，核对真正上屏的换帧表，不重建场景或覆盖原资产。
        private static void ValidateDuelSceneAnimationSlices(Sprite[][] swordsman, Sprite[][] assassin)
        {
            Scene prior = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(DuelScenePath);
            bool wasLoaded = scene.isLoaded;
            if (wasLoaded && scene.isDirty)
            {
                throw new InvalidOperationException("决斗场景有未保存改动，请先保存再校验角色切片。");
            }
            if (!wasLoaded)
            {
                scene = EditorSceneManager.OpenScene(DuelScenePath, OpenSceneMode.Additive);
            }
            try
            {
                PvpDuelView view = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PvpDuelView>(true)).Single();
                ValidateDuelActorAnimationSlices(view.FirstAnimator, swordsman);
                ValidateDuelActorAnimationSlices(view.SecondAnimator, assassin);
            }
            finally
            {
                if (prior.IsValid())
                {
                    SceneManager.SetActiveScene(prior);
                }
                if (!wasLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void ValidateDuelActorAnimationSlices(DuelActorAnimator actor, Sprite[][] sequences)
        {
            using var data = new SerializedObject(actor);
            SerializedProperty poses = data.FindProperty("spriteSequences");
            if (poses == null || poses.arraySize != sequences.Length)
            {
                throw new InvalidOperationException("决斗场景角色缺少切片换帧表：" + actor.name);
            }
            for (int i = 0; i < sequences.Length; i++)
            {
                SerializedProperty frames = poses.GetArrayElementAtIndex(i).FindPropertyRelative("frames");
                if (frames == null || frames.arraySize != sequences[i].Length)
                {
                    throw new InvalidOperationException("决斗场景角色切片数量异常：" + actor.name + "/" + DuelPoses[i]);
                }
                for (int frame = 0; frame < frames.arraySize; frame++)
                {
                    if (frames.GetArrayElementAtIndex(frame).objectReferenceValue != sequences[i][frame])
                    {
                        throw new InvalidOperationException("决斗场景角色切片顺序异常：" + actor.name + "/" + DuelPoses[i]);
                    }
                }
            }
        }

        // 只在内容变化时重写 V2 源资产。关闭旋转和紧密裁切，保证 UGUI 两层交叉淡入淡出使用同一画布。
        private static SpriteAtlas ConfigureDuelFighterAtlas(string[] paths)
        {
            SpriteAtlas existing = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(DuelFighterAtlasPath);
            string[] members = existing == null ? Array.Empty<string>() :
                existing.GetPackables().Select(AssetDatabase.GetAssetPath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (!members.SequenceEqual(paths))
            {
                var atlasAsset = new SpriteAtlasAsset();
                try
                {
                    atlasAsset.Add(paths.Select(path => AssetDatabase.LoadAssetAtPath<Texture2D>(path)).Cast<UnityEngine.Object>().ToArray());
                    SpriteAtlasAsset.Save(atlasAsset, DuelFighterAtlasPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(atlasAsset);
                }
                AssetDatabase.ImportAsset(DuelFighterAtlasPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            var importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(DuelFighterAtlasPath);
            if (!string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(DuelFighterAtlasPath)))
            {
                throw new InvalidOperationException("内置决斗图集不能标记为 AssetBundle。");
            }
            string before = EditorJsonUtility.ToJson(importer);
            importer.includeInBuild = true;
            importer.packingSettings = new SpriteAtlasPackingSettings { enableRotation = false, enableTightPacking = false, padding = 4 };
            importer.textureSettings = new SpriteAtlasTextureSettings
            {
                readable = false, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Bilinear
            };
            importer.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "DefaultTexturePlatform", maxTextureSize = DuelFighterAtlasPageSize,
                textureCompression = TextureImporterCompression.Compressed
            });
            importer.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "Standalone", overridden = true, maxTextureSize = DuelFighterAtlasPageSize, format = TextureImporterFormat.DXT5
            });
            importer.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "Android", overridden = true, maxTextureSize = DuelFighterAtlasPageSize, format = TextureImporterFormat.ETC2_RGBA8
            });
            if (before != EditorJsonUtility.ToJson(importer))
            {
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<SpriteAtlas>(DuelFighterAtlasPath);
        }

        // 读取实际图集精灵和页纹理。GetSprites 返回临时副本，验收完成后立即清理。
        private static int ValidatePackedDuelFighters(SpriteAtlas atlas, Sprite[] expected)
        {
            if (atlas == null || atlas.spriteCount != expected.Length)
            {
                throw new InvalidOperationException("决斗图集实际打包的切片数量不匹配。");
            }
            var packed = new Sprite[atlas.spriteCount];
            try
            {
                int count = atlas.GetSprites(packed);
                const string clone = "(Clone)";
                var names = new HashSet<string>(expected.Select(sprite => sprite.name), StringComparer.Ordinal);
                var pages = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    Sprite sprite = packed[i];
                    string name = sprite.name;
                    if (name.EndsWith(clone, StringComparison.Ordinal))
                    {
                        name = name.Substring(0, name.Length - clone.Length);
                    }
                    if (!names.Remove(name) || sprite.texture == null)
                    {
                        throw new InvalidOperationException("决斗图集出现未预期的切片或缺失纹理：" + name);
                    }
                    pages.Add(sprite.texture.GetInstanceID());
                }
                if (names.Count != 0 || pages.Count == 0)
                {
                    throw new InvalidOperationException("决斗图集未覆盖全部角色切片。");
                }
                return pages.Count;
            }
            finally
            {
                foreach (Sprite sprite in packed)
                {
                    if (sprite != null)
                    {
                        UnityEngine.Object.DestroyImmediate(sprite);
                    }
                }
            }
        }
    }
}
