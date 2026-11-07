using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.View;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
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
        // 与 DuelActorAnimator 的 Pose 顺序对应。制作片段和绑定图片都使用同一张表。
        private static readonly string[] DuelPoses = { "Idle", "Run", "Jump", "Attack1", "Attack2", "Attack3", "Down" };

        // 导入素材、构建场景和 HUD、保存并登记构建列表；最后恢复用户原活动场景。
        public static object BuildDuel()
        {
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
                var root = new GameObject("Duel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = .5f;
                var view = root.AddComponent<PvpDuelView>();
                var backdrop = Rect(DuelBackdropName, root.transform, 0, 0, 1, 1).gameObject.AddComponent<Image>();
                ApplyBoxingRing(backdrop);
                view.First = AddFighter("BlackFighter", root.transform, -.25f);
                view.Second = AddFighter("WhiteFighter", root.transform, .25f);
                view.FirstAnimator = AddActorAnimation(view.First, "");
                view.SecondAnimator = AddActorAnimation(view.Second, "Assassin");
                view.Effects = root.AddComponent<DuelStrikeEffects>();
                view.Effects.First = AddFighter("BlackEffects", root.transform, -.25f);
                view.Effects.Second = AddFighter("WhiteEffects", root.transform, .25f);
                view.Effects.StrikeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/Pvp/PvpDuelStrike.shader") ??
                    throw new InvalidOperationException("决斗招式 Shader 未导入。");
                view.Effects.Jab = LoadDuelSequence("FxJab");
                view.Effects.Skill = LoadDuelSequence("FxSkill");
                view.Effects.Ultimate = LoadDuelSequence("FxUltimate");
                view.FirstHealth = AddMeter("BlackHealth", root.transform, .075f, .876f, .455f, .912f, new Color(.86f,.25f,.25f), false);
                view.SecondHealth = AddMeter("WhiteHealth", root.transform, .545f, .876f, .925f, .912f, new Color(.23f,.65f,.9f), true);
                view.FirstEnergy = AddMeter("BlackEnergy", root.transform, .075f, .837f, .455f, .852f, new Color(1,.74f,.21f), false);
                view.SecondEnergy = AddMeter("WhiteEnergy", root.transform, .545f, .837f, .925f, .852f, new Color(1,.74f,.21f), true);
                view.FirstHealthText = Label("BlackHp", root.transform, "", .08f, .87f, .20f, .916f, 26);
                view.SecondHealthText = Label("WhiteHp", root.transform, "", .80f, .87f, .92f, .916f, 26);
                view.Names = Label("FighterNames", root.transform, "", .08f, .92f, .92f, .98f, 30);
                Plate(Rect("TimerPlate", root.transform, .455f, .854f, .545f, .953f), "UI_achieveItem_imgBK", Color.white);
                view.Clock = Label("Clock", root.transform, "", .45f, .862f, .55f, .941f, 55, true);
                view.Banner = Label("Banner", root.transform, "等待双方进入决斗场", .15f, .64f, .85f, .82f, 54);
                var shadow = view.Banner.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(.08f, .04f, .12f, .92f);
                shadow.effectDistance = new Vector2(2,-3);
                // 草图的三角布局：右下普攻，左下技能，正上大招。左手上方是跳跃。
                view.Jab = Button("Jab", root.transform, "普攻 · J", .865f, .045f, .972f, .112f);
                view.Skill = Button("Skill", root.transform, "技能 · K", .746f, .045f, .853f, .112f);
                view.Ultimate = Button("Ultimate", root.transform, "大招 · L", .865f, .128f, .972f, .195f);
                view.Jump = Button("Jump", root.transform, "↑ · 跳跃", .102f, .128f, .209f, .195f);
                view.Exit = Button("Back", root.transform, "返回大厅", .02f, .735f, .13f, .792f);
                view.Reconnect = Button("Reconnect", root.transform, "重连", .14f, .735f, .25f, .792f);
                view.Reconnect.gameObject.SetActive(false);
                AddDirection(Button("Left", root.transform, "← · A", .06f, .07f, .17f, .135f), view, -1);
                AddDirection(Button("Right", root.transform, "→ · D", .18f, .07f, .29f, .135f), view, 1);
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
                return new Dictionary<string, object> { ["scene"] = DuelScenePath, ["fighters"] = 2, ["legacyClips"] = 14 };
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
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { DuelArt.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer.textureType != TextureImporterType.Sprite || importer.mipmapEnabled)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.mipmapEnabled = false;
                    importer.SaveAndReimport();
                }
            }
        }

        // 按约定名称加载单个场景 Sprite，缺失时让构建明确失败。
        private static Sprite LoadDuelSprite(string asset)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(DuelArt + asset + ".png") ??
                throw new InvalidOperationException("决斗素材缺失：" + asset);
        }

        // 按文件名前缀和编号排序加载动作序列，保证每次生成顺序相同。
        private static Sprite[] LoadDuelSequence(string action)
        {
            string folder = DuelArt.TrimEnd('/');
            var frames = AssetDatabase.FindAssets("t:Sprite", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => System.IO.Path.GetFileNameWithoutExtension(path).StartsWith(action + "_", StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<Sprite>).ToArray();
            if (frames.Length == 0)
            {
                throw new InvalidOperationException("缺少角色动作序列：" + action);
            }
            return frames;
        }

        // 创建可被显示快照定位的角色或特效图层，center 是初始水平布局比例。
        private static Image AddFighter(string name, Transform parent, float center)
        {
            var rect = Rect(name, parent, .5f, .30f, .5f, .30f);
            rect.sizeDelta = new Vector2(345, 345);
            rect.anchoredPosition = new Vector2(center * 1920, 0);
            var image = rect.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        // 生成条框和独立填充层，mirror 决定血量从哪一侧开始变化。
        private static Image AddMeter(string name, Transform parent, float x0, float y0, float x1, float y1, Color color, bool mirror)
        {
            var shell = Rect(name, parent, x0, y0, x1, y1);
            Plate(shell, "UI_achieveItem_imgBK", new Color(.22f,.17f,.25f));
            var fill = Plate(Rect("Fill", shell, .008f, .10f, .992f, .90f), "UI_achieveItem_imgBK", color);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = mirror ? 1 : 0;
            return fill;
        }

        // 为一个角色生成七个 Legacy Clip 并绑定到播放组件；已有资产原地更新以保留 GUID。
        private static DuelActorAnimator AddActorAnimation(Image actor, string prefix)
        {
            // 两个角色共享状态枚举，各自持有 Clip。生成时就写好 Image.sprite 曲线，运行期只播放。
            string folder = "Assets/Scenes/PvpDuelAnimations";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/Scenes", "PvpDuelAnimations");
            }
            var animation = actor.gameObject.AddComponent<Animation>();
            animation.playAutomatically = false;
            var clips = new List<AnimationClip>();
            foreach (string pose in DuelPoses)
            {
                Sprite[] frames = LoadDuelSequence(prefix + pose);
                string path = $"{folder}/{(prefix.Length == 0 ? "Swordsman" : prefix)}_{pose}.anim";
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
                clips.Add(clip);
                if (pose == "Idle")
                {
                    animation.clip = clip;
                    actor.sprite = frames[0];
                }
            }
            AnimationUtility.SetAnimationClips(animation, clips.ToArray());
            AssetDatabase.SaveAssets();
            var presenter = actor.gameObject.AddComponent<DuelActorAnimator>();
            BindDuelSpriteSequences(presenter, prefix);
            return presenter;
        }

        // 把图片按姿态写进场景，运行时无需查找资产或解析编辑器动画曲线。
        private static void BindDuelSpriteSequences(DuelActorAnimator presenter, string prefix)
        {
            using var data = new SerializedObject(presenter);
            SerializedProperty sequences = data.FindProperty("spriteSequences");
            sequences.arraySize = DuelPoses.Length;
            for (int i = 0; i < DuelPoses.Length; i++)
            {
                Sprite[] images = LoadDuelSequence(prefix + DuelPoses[i]);
                SerializedProperty frames = sequences.GetArrayElementAtIndex(i).FindPropertyRelative("frames");
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
                BindDuelSpriteSequences(view.FirstAnimator, "");
                BindDuelSpriteSequences(view.SecondAnimator, "Assassin");
                AddDirection(view.transform.Find("Left").GetComponent<Button>(), view, -1);
                AddDirection(view.transform.Find("Right").GetComponent<Button>(), view, 1);
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

        // 把指针按下、松开与移出转发为方向意图，避免按住后移出产生粘键。
        private static void AddDirection(Button button, PvpDuelView view, int direction)
        {
            var trigger = button.GetComponent<EventTrigger>() ?? button.gameObject.AddComponent<EventTrigger>();
            trigger.triggers.RemoveAll(entry => entry.eventID == EventTriggerType.PointerDown ||
                entry.eventID == EventTriggerType.PointerUp || entry.eventID == EventTriggerType.PointerExit);
            foreach (var kind in new[] { EventTriggerType.PointerDown, EventTriggerType.PointerUp, EventTriggerType.PointerExit })
            {
                var entry = new EventTrigger.Entry { eventID = kind };
                var callback = new EventTrigger.TriggerEvent();
                UnityAction<int> move = kind == EventTriggerType.PointerDown ? view.MovePointer : view.ReleasePointer;
                UnityEventTools.AddIntPersistentListener(callback, move, direction);
                entry.callback = callback;
                trigger.triggers.Add(entry);
            }
        }
    }
}
