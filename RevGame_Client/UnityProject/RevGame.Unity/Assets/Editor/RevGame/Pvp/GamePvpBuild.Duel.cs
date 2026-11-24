using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RevGame.Editor
{
    // 决斗场景的编辑器配方。运行期由已保存场景消费绑定。
    public static partial class GamePvpBuild
    {
        // 决斗场景保存路径，与 GameFlow 场景名对应。
        private const string DuelScenePath = "Assets/Scenes/PvpDuelScene.unity";
        // 编辑器查找角色帧和背景的资产目录。
        private const string DuelArt = "Assets/GameRes/Art/Pvp/Duel/";
        // 决斗当前的角色、招式特效和场景背景从此目录装配。
        private const string ArcadeArt = DuelArt + "Arcade/";
        // 鸦鸦按原生动作号复用普攻、技能和大招的三种命中序列。
        private static readonly string[] ImpactArt = { "Light", "Skill", "Ultimate", "Skill", "Light", "Light" };
        // 与 DuelActorAnimator 的 Pose 顺序对应。制作片段和绑定图片都使用同一张表。
        private static readonly string[] DuelPoses = { "Idle", "Run", "Jump", "Attack1", "Attack2", "Attack3", "Down",
            "Attack4", "Attack5", "Attack6", "Hit" };

        // 导入素材并构建双鸦鸦场景和 HUD。保存后登记构建列表，最后恢复原活动场景。
        public static object BuildDuel()
        {
            if (SceneManager.GetSceneByPath(DuelScenePath).isLoaded)
            {
                throw new InvalidOperationException("请关闭已打开的决斗场景后运行生成配方。");
            }
            MigrateDuelAssets();
            ImportDuelSprites();
            var plugin = (PluginImporter)AssetImporter.GetAtPath("Assets/Plugins/RevGameCore/x86_64/RevGameCore.dll");
            if (plugin.GetCompatibleWithAnyPlatform() || plugin.GetEditorData("OS") != "Windows")
            {
                plugin.SetCompatibleWithAnyPlatform(false);
                plugin.SetCompatibleWithEditor(true);
                plugin.SetEditorData("OS", "Windows");
                plugin.SetEditorData("CPU", "x86_64");
                plugin.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
                plugin.SaveAndReimport();
            }
            _font = GameAssetBuilder.Font();
            BuildDuelHud();
            DuelArenaFxGraphic particle = BuildDuelParticle();
            var prior = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var camera = new GameObject("Camera").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.09f, .08f, .14f);
                camera.gameObject.AddComponent<AudioListener>();
                var root = new GameObject("Duel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = .5f;
                var view = root.AddComponent<PvpDuelView>();
                RectTransform fighters = Rect("ArenaFighters", root.transform, 0, 0, 1, 1);
                fighters.pivot = new Vector2(.5f, .425f);
                view.CameraRig = root.AddComponent<DuelCameraRig>();
                view.CameraRig.Fighters = fighters;
                view.CameraRig.Backdrop = BuildDuelBackdrop(fighters);
                view.CameraRig.LeftWard = BuildDuelWard(fighters, -1, particle.material);
                view.CameraRig.RightWard = BuildDuelWard(fighters, 1, particle.material);
                view.First = AddFighter("BlackFighter", fighters, -.25f);
                view.Second = AddFighter("WhiteFighter", fighters, .25f);
                view.FirstAnimator = view.First.gameObject.AddComponent<DuelActorAnimator>();
                view.SecondAnimator = view.Second.gameObject.AddComponent<DuelActorAnimator>();
                BindDuelActorRoster(view);
                view.Effects = root.AddComponent<DuelStrikeEffects>();
                view.Effects.First = AddEffectLayer("BlackEffects", fighters);
                view.Effects.Second = AddEffectLayer("WhiteEffects", fighters);
                view.Effects.FirstImpact = AddEffectLayer("BlackImpact", fighters);
                view.Effects.SecondImpact = AddEffectLayer("WhiteImpact", fighters);
                view.Effects.Speaker = root.AddComponent<AudioSource>();
                view.Effects.Speaker.playOnAwake = false;
                view.Effects.ParticlePrefab = particle;
                BindDuelPresentation(view);
                var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
                if (!EditorSceneManager.SaveScene(scene, DuelScenePath))
                {
                    throw new InvalidOperationException("决斗场景保存失败。");
                }
                if (!scenes.Exists(entry => entry.path == DuelScenePath))
                {
                    scenes.Add(new EditorBuildSettingsScene(DuelScenePath, true));
                    EditorBuildSettings.scenes = scenes.ToArray();
                }
                return new Dictionary<string, object>
                {
                    ["scene"] = DuelScenePath, ["fighters"] = 2, ["legacyClips"] = DuelPoses.Length,
                    ["hudPrefab"] = DuelHudPrefabPath, ["backdrop"] = view.CameraRig.Backdrop.sprite.name
                };
            }
            finally
            {
                if (prior.IsValid())
                {
                    SceneManager.SetActiveScene(prior);
                }
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        // 确保动画素材按单 Sprite、关闭 Mipmap 的设置导入。
        private static void ImportDuelSprites()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ArcadeArt.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer.textureType != TextureImporterType.Sprite || importer.mipmapEnabled ||
                    importer.npotScale != TextureImporterNPOTScale.None)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.mipmapEnabled = false;
                    importer.npotScale = TextureImporterNPOTScale.None;
                    importer.alphaIsTransparency = true;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                }
            }
        }

        // 在构建场景时检查纹理已经导入为 Sprite，运行时从场景引用取用。
        private static Sprite LoadArcadeSprite(string name)
        {
            return LoadDuelSpriteAtPath(ArcadeArt + name + ".png");
        }

        // 资产没导入或类型不正确时在制作阶段拒绝保存空帧。
        private static Sprite LoadDuelSpriteAtPath(string path)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(path) ??
                throw new InvalidOperationException("决斗街机素材未导入：" + path);
        }

        // 文件名顺序就是播放顺序。五个透明帧同尺寸，由原有决斗图集打包。
        private static DuelStrikeEffects.FrameSequence LoadDuelEffectSequence(string role, string stage, string shape)
        {
            string name = role + stage + shape;
            var frames = new Sprite[DuelStrikeEffects.SequenceFrameCount];
            for (int frame = 0; frame < frames.Length; frame++)
            {
                frames[frame] = LoadDuelSpriteAtPath($"{ArcadeArt}StrikeFrames/{name}_{frame:D2}.png");
            }
            return new DuelStrikeEffects.FrameSequence { Frames = frames };
        }

        // 新建和升级共用同一入口，两席共享鸦鸦的出招素材，背景仍由擂台配置装配。
        private static void BindDuelPresentation(PvpDuelView view)
        {
            BindDuelYayaPresentation(view);
            ApplyDuelBackdrop(view.CameraRig.Backdrop);
        }

        // 复用角色层锚点来对齐舞台坐标，特效初始禁用且不拦截触摸。
        private static Image AddEffectLayer(string name, Transform parent)
        {
            Image layer = AddFighter(name, parent, 0);
            layer.preserveAspect = false;
            layer.enabled = false;
            layer.gameObject.AddComponent<DuelCastReveal>();
            return layer;
        }

        // 创建可被显示快照定位的角色或特效图层，center 是初始水平布局比例。
        private static Image AddFighter(string name, Transform parent, float center)
        {
            var rect = Rect(name, parent, .5f, .425f, .5f, .425f);
            rect.sizeDelta = new Vector2(480, 480);
            rect.anchoredPosition = new Vector2(center * 1920, 0);
            var image = rect.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        // 每个鸦鸦姿态只写一份 Legacy Clip，两席引用已有 GUID 对应的同一组片段。
        private static AnimationClip[] BuildDuelClips(Sprite[][] sequences)
        {
            string folder = DuelAnimationFolder;
            var clips = new AnimationClip[DuelPoses.Length];
            for (int index = 0; index < DuelPoses.Length; index++)
            {
                string pose = DuelPoses[index];
                Sprite[] frames = sequences[index];
                string path = $"{folder}/{YayaPrefix}_{pose}.anim";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                {
                    clip = new AnimationClip();
                    AssetDatabase.CreateAsset(clip, path);
                }
                clip.legacy = true;
                clip.name = pose;
                clip.frameRate = DuelClipFrameRate(pose);
                clip.wrapMode = pose == "Idle" || pose == "Run" ? WrapMode.Loop : WrapMode.ClampForever;
                ObjectReferenceKeyframe[] keys = BuildDuelClipKeys(pose, frames, clip.frameRate);
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(Image), "m_Sprite"), keys);
                EditorUtility.SetDirty(clip);
                clips[index] = clip;
            }
            AssetDatabase.SaveAssets();
            return clips;
        }

        // 生成等间隔的鸦鸦曲线。待机和跑步直接回环，其余姿态重复末帧一次用于收招。
        private static ObjectReferenceKeyframe[] BuildDuelClipKeys(string pose, Sprite[] frames, float frameRate)
        {
            bool loop = pose == "Idle" || pose == "Run";
            var keys = new ObjectReferenceKeyframe[frames.Length + (loop ? 0 : 1)];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = new ObjectReferenceKeyframe { time = i / frameRate, value = frames[Math.Min(i, frames.Length - 1)] };
            }
            return keys;
        }

        // 为一个席位写入共用片段和序列。实际播放由该席自己的决斗动画器推进。
        private static void ConfigureDuelActor(Image actor, DuelActorAnimator presenter, Sprite[][] sequences, AnimationClip[] clips)
        {
            var animation = presenter.GetComponent<Animation>();
            animation.playAutomatically = false;
            animation.enabled = false;
            actor.sprite = sequences[0][0];
            AnimationUtility.SetAnimationClips(animation, clips);
            animation.clip = clips[0];
            BindDuelSpriteSequences(presenter, sequences);
        }

        // 写入鸦鸦的姿态与接触帧，清除旧场景的非等长待机表后按片段采样率播放。
        private static void BindDuelSpriteSequences(DuelActorAnimator presenter, Sprite[][] shared)
        {
            using var data = new SerializedObject(presenter);
            data.FindProperty("animateIdle").boolValue = true;
            data.FindProperty("idleFrameEndSeconds").arraySize = 0;
            SerializedProperty sequences = data.FindProperty("spriteSequences");
            sequences.arraySize = DuelPoses.Length;
            for (int i = 0; i < DuelPoses.Length; i++)
            {
                Sprite[] images = shared[i];
                SerializedProperty pose = sequences.GetArrayElementAtIndex(i);
                pose.FindPropertyRelative("contactIndex").intValue = YayaContactKeys[i];
                SerializedProperty frames = pose.FindPropertyRelative("frames");
                frames.arraySize = images.Length;
                for (int frame = 0; frame < images.Length; frame++)
                {
                    frames.GetArrayElementAtIndex(frame).objectReferenceValue = images[frame];
                }
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>同步现有场景的序列帧与动画片段，保留场景对象和引用。</summary>
        public static object BindExistingDuelSpriteSequences()
        {
            Scene current = SceneManager.GetActiveScene();
            Scene duel = SceneManager.GetSceneByPath(DuelScenePath);
            bool wasLoaded = duel.isLoaded;
            if (EditorApplication.isPlayingOrWillChangePlaymode || (wasLoaded && duel.isDirty))
            {
                throw new InvalidOperationException("请退出 Play 并保存决斗场景后再装配序列帧。");
            }
            if (!wasLoaded)
            {
                duel = EditorSceneManager.OpenScene(DuelScenePath, OpenSceneMode.Additive);
            }
            try
            {
                ImportDuelSprites();
                var view = duel.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PvpDuelView>(true)).Single();
                BindDuelActorRoster(view);
                BindDuelPresentation(view);
                if (!EditorSceneManager.SaveScene(duel))
                {
                    throw new InvalidOperationException("决斗角色序列帧保存失败。");
                }
                return new { scene = DuelScenePath, actors = 2, posesPerActor = DuelPoses.Length };
            }
            finally
            {
                SceneManager.SetActiveScene(current);
                if (!wasLoaded)
                {
                    EditorSceneManager.CloseScene(duel, true);
                }
            }
        }

    }
}
