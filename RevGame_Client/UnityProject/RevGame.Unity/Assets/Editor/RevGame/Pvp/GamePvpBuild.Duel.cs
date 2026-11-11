using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
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
        // 新街机角色和特效只从此目录装配。旧场景的素材归档仍可由编辑器查阅。
        private const string ArcadeArt = DuelArt + "Arcade/";
        // 两席镜像测试共用绯焰序列帧，按同一份图集和动作配置播放。
        private const string EmberPrefix = "Ember";
        // 两席共享动作取样表。每个数字指向同角色的一张原画，图集只存一份。
        private static readonly int[][] ArcadePoseFrames =
        {
            new[] { 0, 1 }, new[] { 2, 3 }, new[] { 12, 4, 16, 4 }, new[] { 5, 20, 21, 6, 19 },
            new[] { 24, 25, 26, 27, 19 }, new[] { 13, 9, 26, 23, 10, 19 }, new[] { 18, 11 },
            new[] { 28, 29, 30, 31, 19 }, new[] { 13, 14, 22, 7, 19 }, new[] { 12, 8, 15, 23, 9, 19 },
            new[] { 17, 18, 19 }
        };
        // 每个姿态的接触关键帧下标。非攻击姿态填 0，普通移动仍按渲染时间播放。
        private static readonly int[] ArcadeContactKeys = { 0, 0, 0, 2, 2, 3, 0, 3, 3, 3, 0 };
        // 由四类出招形状与额外普攻段构成的起手/命中序列，数组下标是原生动作编号减一。
        private static readonly int[] ArcadeCastArt = { 0, 1, 2, 3, 5, 4 };
        private static readonly int[] ArcadeImpactArt = { 0, 6, 7, 6, 2, 4 };
        // 三段普攻、两种技能和大招各用不同的起手音色。
        private static readonly string[] ArcadeCastSounds = { "Jab", "Rush", "Ultimate", "Sweep", "Hook", "Finisher" };
        // 与 DuelActorAnimator 的 Pose 顺序对应。制作片段和绑定图片都使用同一张表。
        private static readonly string[] DuelPoses = { "Idle", "Run", "Jump", "Attack1", "Attack2", "Attack3", "Down",
            "Attack4", "Attack5", "Attack6", "Hit" };

        // 导入素材、构建场景和 HUD、保存并登记构建列表；最后恢复用户原活动场景。
        public static object BuildDuel()
        {
            if (SceneManager.GetSceneByPath(DuelScenePath).isLoaded)
            {
                throw new InvalidOperationException("请关闭已打开的决斗场景后运行生成配方。");
            }
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
            var prior = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var camera = new GameObject("Camera").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.09f, .08f, .14f);
                camera.gameObject.AddComponent<AudioListener>();
                var root = new GameObject("Duel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = .5f;
                var view = root.AddComponent<PvpDuelView>();
                var backdrop = Rect(DuelBackdropName, root.transform, 0, 0, 1, 1).gameObject.AddComponent<Image>();
                ApplyDuelBackdrop(backdrop);
                RectTransform fighters = Rect("ArenaFighters", root.transform, 0, 0, 1, 1);
                fighters.pivot = new Vector2(.5f, .425f);
                view.CameraRig = root.AddComponent<DuelCameraRig>();
                view.CameraRig.Fighters = fighters;
                view.CameraRig.Backdrop = backdrop;
                view.First = AddFighter("BlackFighter", fighters, -.25f);
                view.Second = AddFighter("WhiteFighter", fighters, .25f);
                Sprite[][] sequences = DuelPoses.Select(LoadDuelSequence).ToArray();
                AnimationClip[] clips = BuildDuelClips(sequences);
                view.FirstAnimator = AddActorAnimation(view.First, clips, sequences);
                view.SecondAnimator = AddActorAnimation(view.Second, clips, sequences);
                view.Effects = root.AddComponent<DuelStrikeEffects>();
                view.Effects.First = AddEffectLayer("BlackEffects", fighters);
                view.Effects.Second = AddEffectLayer("WhiteEffects", fighters);
                view.Effects.FirstImpact = AddEffectLayer("BlackImpact", fighters);
                view.Effects.SecondImpact = AddEffectLayer("WhiteImpact", fighters);
                view.Effects.CastSprites = ArcadeCastArt.Select(LoadArcadeEffect).ToArray();
                view.Effects.ImpactSprites = ArcadeImpactArt.Select(LoadArcadeEffect).ToArray();
                view.Effects.CastSounds = ArcadeCastSounds.Select(LoadArcadeSound).ToArray();
                view.Effects.ImpactSounds = new[] { "LightHit", "SkillHit", "UltimateHit" }.Select(LoadArcadeSound).ToArray();
                view.Effects.Speaker = root.AddComponent<AudioSource>();
                view.Effects.Speaker.playOnAwake = false;
                BuildDuelHud(view);
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
                return new Dictionary<string, object> { ["scene"] = DuelScenePath, ["fighters"] = 2, ["legacyClips"] = clips.Length };
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

        // 以编辑器动作表读取切片。重复关键帧引用同一 Sprite，避免在图集中保存重复原画。
        private static Sprite[] LoadDuelSequence(string pose)
        {
            return ArcadePoseFrames[Array.IndexOf(DuelPoses, pose)]
                .Select(index => LoadArcadeSprite($"{EmberPrefix}_{index:D2}")).ToArray();
        }

        // 在构建场景时检查纹理已经导入为 Sprite，运行时从场景引用取用。
        private static Sprite LoadArcadeSprite(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(ArcadeArt + name + ".png") ??
                throw new InvalidOperationException("决斗街机素材未导入：" + name);
        }

        // 特效起手和命中可复用同一贴图。
        private static Sprite LoadArcadeEffect(int index)
        {
            return LoadArcadeSprite($"Fx_{index:D2}");
        }

        // 查找场景持有的原声音效，缺失时中止生成。
        private static AudioClip LoadArcadeSound(string name)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>(ArcadeArt + "Audio/" + name + ".wav") ??
                throw new InvalidOperationException("决斗音效未导入：" + name);
        }

        // 复用角色层锚点来对齐舞台坐标，特效初始禁用且不拦截触摸。
        private static Image AddEffectLayer(string name, Transform parent)
        {
            Image layer = AddFighter(name, parent, 0);
            layer.preserveAspect = false;
            layer.enabled = false;
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

        // 生成一套共享的 Legacy Clip，原来已存在的片段保留 GUID。
        private static AnimationClip[] BuildDuelClips(Sprite[][] sequences)
        {
            string folder = "Assets/Scenes/PvpDuelAnimations";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/Scenes", "PvpDuelAnimations");
            }
            var clips = new AnimationClip[DuelPoses.Length];
            for (int index = 0; index < DuelPoses.Length; index++)
            {
                string pose = DuelPoses[index];
                Sprite[] frames = sequences[index];
                string path = $"{folder}/Swordsman_{pose}.anim";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null)
                {
                    clip = new AnimationClip();
                    AssetDatabase.CreateAsset(clip, path);
                }
                clip.legacy = true;
                clip.name = pose;
                clip.frameRate = 15;
                clip.wrapMode = pose == "Idle" || pose == "Run" ? WrapMode.Loop : WrapMode.ClampForever;
                var keys = new ObjectReferenceKeyframe[frames.Length + 1];
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = new ObjectReferenceKeyframe { time = i / clip.frameRate, value = frames[Math.Min(i, frames.Length - 1)] };
                }
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(Image), "m_Sprite"), keys);
                EditorUtility.SetDirty(clip);
                clips[index] = clip;
            }
            AssetDatabase.SaveAssets();
            return clips;
        }

        // 每席独立管理播放时间和动作切换，两席引用同一套只读动画资源。
        private static DuelActorAnimator AddActorAnimation(Image actor, AnimationClip[] clips, Sprite[][] sequences)
        {
            var animation = actor.gameObject.AddComponent<Animation>();
            animation.playAutomatically = false;
            // 动画资源用于读帧率，实际换帧由决斗表现组件完成。
            animation.enabled = false;
            animation.clip = clips[0];
            actor.sprite = sequences[0][0];
            AnimationUtility.SetAnimationClips(animation, clips);
            var presenter = actor.gameObject.AddComponent<DuelActorAnimator>();
            BindDuelSpriteSequences(presenter, sequences);
            return presenter;
        }

        // 把图片按姿态写进场景，运行时无需查找资产或解析编辑器动画曲线。
        private static void BindDuelSpriteSequences(DuelActorAnimator presenter, Sprite[][] shared)
        {
            using var data = new SerializedObject(presenter);
            SerializedProperty sequences = data.FindProperty("spriteSequences");
            sequences.arraySize = DuelPoses.Length;
            for (int i = 0; i < DuelPoses.Length; i++)
            {
                Sprite[] images = shared[i];
                SerializedProperty pose = sequences.GetArrayElementAtIndex(i);
                pose.FindPropertyRelative("contactIndex").intValue = ArcadeContactKeys[i];
                SerializedProperty frames = pose.FindPropertyRelative("frames");
                frames.arraySize = images.Length;
                for (int frame = 0; frame < images.Length; frame++)
                {
                    frames.GetArrayElementAtIndex(frame).objectReferenceValue = images[frame];
                }
            }
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>给已制作的场景补上角色帧序列，保留场景原有画面与对象引用。</summary>
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
                var view = duel.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PvpDuelView>(true)).Single();
                Sprite[][] shared = DuelPoses.Select(LoadDuelSequence).ToArray();
                BindDuelSpriteSequences(view.FirstAnimator, shared);
                BindDuelSpriteSequences(view.SecondAnimator, shared);
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
