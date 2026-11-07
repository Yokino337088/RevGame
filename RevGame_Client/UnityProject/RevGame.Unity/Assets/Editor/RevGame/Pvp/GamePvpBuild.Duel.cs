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
    public static partial class GamePvpBuild
    {
        private const string DuelScenePath = "Assets/Scenes/PvpDuelScene.unity";
        private const string DuelArt = "Assets/GameRes/Art/Pvp/Duel/";

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
                var backdrop = Rect("TempleBackdrop", root.transform, 0, 0, 1, 1).gameObject.AddComponent<Image>();
                backdrop.sprite = LoadDuelSprite("Temple");
                backdrop.raycastTarget = false;
                backdrop.preserveAspect = false;
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

        private static Sprite LoadDuelSprite(string asset)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(DuelArt + asset + ".png") ??
                throw new InvalidOperationException("决斗素材缺失：" + asset);
        }

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
            foreach (string pose in new[] { "Idle", "Run", "Jump", "Attack1", "Attack2", "Attack3", "Down" })
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
            return actor.gameObject.AddComponent<DuelActorAnimator>();
        }

        private static void AddDirection(Button button, PvpDuelView view, int direction)
        {
            var trigger = button.gameObject.AddComponent<EventTrigger>();
            foreach (var kind in new[] { EventTriggerType.PointerDown, EventTriggerType.PointerUp, EventTriggerType.PointerExit })
            {
                var entry = new EventTrigger.Entry { eventID = kind };
                var callback = new EventTrigger.TriggerEvent();
                UnityAction<int> move = view.MovePointer;
                UnityEventTools.AddIntPersistentListener(callback, move, kind == EventTriggerType.PointerDown ? direction : 0);
                entry.callback = callback;
                trigger.triggers.Add(entry);
            }
        }
    }
}
