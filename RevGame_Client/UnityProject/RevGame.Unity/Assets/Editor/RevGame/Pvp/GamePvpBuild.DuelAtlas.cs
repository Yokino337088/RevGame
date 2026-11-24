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
            // 动画片段的完整时长，单位为秒。非等长待机在打包前核对最后一个采样间隔。
            public float durationSeconds;
            public string firstSprite;
            public string lastSprite;
            public Vector2 frameSize;
            public Vector2 normalizedPivot;
        }

        /// <summary>核对两席共用鸦鸦序列后，保存并实打图集，确认每张切片都可绑定。</summary>
        public static DuelFighterAtlasResult BuildDuelFighterAtlas()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("请在编辑模式构建决斗角色图集。");
            }

            Sprite[][] yaya = DuelPoses.Select(LoadYayaDuelSequence).ToArray();
            var groups = new List<DuelAnimationSequenceResult>(DuelPoses.Length);
            ValidateDuelRoleClips(yaya, groups);
            Sprite[] strikes = ValidateDuelSceneAnimationSlices(yaya);

            // 内置图集只收角色和出招图片。小镇是独立长图，HUD 另走按局加载的专用图集。
            Sprite[] sprites = yaya.SelectMany(frames => frames)
                .Concat(strikes)
                .Distinct().ToArray();
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

        // 核对鸦鸦切片的画布、轴心和内置归属，每个姿态记录一份动画结果。
        private static void ValidateDuelRoleClips(Sprite[][] sequences, List<DuelAnimationSequenceResult> groups)
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
                    if (!sprite.name.StartsWith(YayaPrefix + "_", StringComparison.Ordinal) ||
                        sprite.rect.size != size || sprite.pivot != size * .5f || !Mathf.Approximately(sprite.pixelsPerUnit, pixelsPerUnit) ||
                        importer == null || importer.textureType != TextureImporterType.Sprite ||
                        importer.spriteImportMode != SpriteImportMode.Single || importer.mipmapEnabled ||
                        !string.IsNullOrEmpty(AssetDatabase.GetImplicitAssetBundleName(path)))
                    {
                        throw new InvalidOperationException("决斗切片的顺序、画布、轴心或归属异常：" + path);
                    }
                }
                string clipPath = $"{DuelAnimationFolder}/{YayaPrefix}_{pose}.anim";
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                ValidateDuelClipFrames(clip, frames, clipPath, pose);
                groups.Add(new DuelAnimationSequenceResult
                {
                    role = YayaPrefix, pose = pose, clipPath = clipPath, frameCount = frames.Length,
                    durationSeconds = clip.length,
                    firstSprite = frames[0].name, lastSprite = frames[frames.Length - 1].name,
                    frameSize = size, normalizedPivot = new Vector2(.5f, .5f)
                });
            }
        }

        // 校验鸦鸦片段的采样率和关键帧，确保落屏顺序与制作配方一致。
        private static void ValidateDuelClipFrames(AnimationClip clip, Sprite[] frames, string clipPath, string pose)
        {
            var binding = EditorCurveBinding.PPtrCurve("", typeof(Image), "m_Sprite");
            ObjectReferenceKeyframe[] keys = clip == null ? null : AnimationUtility.GetObjectReferenceCurve(clip, binding);
            int frameRate = DuelClipFrameRate(pose);
            ObjectReferenceKeyframe[] expected = BuildDuelClipKeys(pose, frames, frameRate);
            if (keys == null || keys.Length != expected.Length || !clip.legacy || !Mathf.Approximately(clip.frameRate, frameRate))
            {
                throw new InvalidOperationException("决斗动画帧表或采样率异常：" + clipPath);
            }
            bool loop = pose == "Idle" || pose == "Run";
            if (clip.wrapMode != (loop ? WrapMode.Loop : WrapMode.ClampForever) ||
                (loop && !Mathf.Approximately(clip.length, frames.Length / (float)frameRate)))
            {
                throw new InvalidOperationException("鸦鸦片段循环设置或长度异常：" + clipPath);
            }
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i].value != expected[i].value || !Mathf.Approximately(keys[i].time, expected[i].time))
                {
                    throw new InvalidOperationException("决斗动画关键帧顺序异常：" + clipPath + " 第 " + i + " 帧");
                }
            }
        }

        // 只读打开决斗场景，核对两席绑定相同的鸦鸦换帧表、材质和音效，结束后恢复原场景。
        private static Sprite[] ValidateDuelSceneAnimationSlices(Sprite[][] yaya)
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
                ValidateDuelActorAnimationSlices(view.FirstAnimator, yaya);
                ValidateDuelActorAnimationSlices(view.SecondAnimator, yaya);
                DuelStrikeEffects effects = view.Effects;
                Material material = effects.First.material;
                if (!effects.YayaCastSounds.SequenceEqual(YayaCastAudio.Select(LoadYayaDuelAudio)) ||
                    !effects.YayaImpactSounds.SequenceEqual(YayaImpactAudio.Select(LoadYayaDuelAudio)) ||
                    AssetDatabase.GetAssetPath(material) != YayaDuelMaterial || !material.shader.isSupported ||
                    effects.FirstImpact.material != material || effects.Second.material != material ||
                    effects.SecondImpact.material != material ||
                    view.CameraRig.Backdrop.sprite != LoadArcadeSprite("SunlitTownArena") ||
                    !effects.ParticlePrefab.material.shader.isSupported ||
                    view.GetComponentsInChildren<Button>(true).Length != 0)
                {
                    throw new InvalidOperationException("决斗场景中的特效、背景或 HUD 分层绑定异常。");
                }
                return ValidateStrikeTable(effects.YayaCastSequences, YayaPrefix, "Cast", YayaCastArt)
                    .Concat(ValidateStrikeTable(effects.YayaImpactSequences, YayaPrefix, "Hit", ImpactArt)).ToArray();
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

        // 用场景实绑的序列收集图集成员；数量、顺序或引用不对时在打包前中止。
        private static Sprite[] ValidateStrikeTable(DuelStrikeEffects.FrameSequence[] table, string role, string stage, string[] shapes)
        {
            if (table == null || table.Length != shapes.Length)
            {
                throw new InvalidOperationException("决斗特效动作表缺失：" + role + stage);
            }
            for (int action = 0; action < table.Length; action++)
            {
                Sprite[] frames = table[action].Frames;
                if (frames == null || !frames.SequenceEqual(LoadDuelEffectSequence(role, stage, shapes[action]).Frames))
                {
                    throw new InvalidOperationException("决斗特效帧序异常：" + role + stage + shapes[action]);
                }
            }
            return table.SelectMany(sequence => sequence.Frames).ToArray();
        }

        // 逐席核对鸦鸦的 Legacy 片段、序列和接触帧，避免黑白场景引用在重建后分叉。
        private static void ValidateDuelActorAnimationSlices(DuelActorAnimator actor, Sprite[][] sequences)
        {
            using var data = new SerializedObject(actor);
            SerializedProperty poses = data.FindProperty("spriteSequences");
            if (poses == null || poses.arraySize != sequences.Length || !data.FindProperty("animateIdle").boolValue)
            {
                throw new InvalidOperationException("决斗场景角色缺少切片换帧表：" + actor.name);
            }
            if (data.FindProperty("idleFrameEndSeconds").arraySize != 0)
            {
                throw new InvalidOperationException("鸦鸦待机应沿用片段的固定采样率：" + actor.name);
            }
            var clips = AnimationUtility.GetAnimationClips(actor.gameObject).Select(AssetDatabase.GetAssetPath).ToHashSet();
            for (int i = 0; i < sequences.Length; i++)
            {
                if (!clips.Contains($"{DuelAnimationFolder}/{YayaPrefix}_{DuelPoses[i]}.anim"))
                {
                    throw new InvalidOperationException("决斗场景角色片段绑定异常：" + actor.name + "/" + DuelPoses[i]);
                }
                SerializedProperty pose = poses.GetArrayElementAtIndex(i);
                if (pose.FindPropertyRelative("contactIndex").intValue != YayaContactKeys[i])
                {
                    throw new InvalidOperationException("决斗接触关键帧异常：" + actor.name + "/" + DuelPoses[i]);
                }
                SerializedProperty frames = pose.FindPropertyRelative("frames");
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
