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
                AddCage(root.transform);
                view.First = AddFighter("BlackFighter", root.transform, -.25f);
                view.Second = AddFighter("WhiteFighter", root.transform, .25f);
                view.Idle = LoadDuelSequence("Idle");
                view.Run = LoadDuelSequence("Run");
                view.AttackOne = LoadDuelSequence("Attack1");
                view.AttackTwo = LoadDuelSequence("Attack2");
                view.AttackThree = LoadDuelSequence("Attack3");
                view.First.sprite = view.Idle[0];
                view.Second.sprite = view.Idle[0];
                view.FirstHealth = AddMeter("BlackHealth", root.transform, .075f, .876f, .455f, .912f, new Color(.86f,.25f,.25f), false);
                view.SecondHealth = AddMeter("WhiteHealth", root.transform, .545f, .876f, .925f, .912f, new Color(.23f,.65f,.9f), true);
                view.FirstEnergy = AddMeter("BlackEnergy", root.transform, .075f, .837f, .455f, .852f, new Color(1,.74f,.21f), false);
                view.SecondEnergy = AddMeter("WhiteEnergy", root.transform, .545f, .837f, .925f, .852f, new Color(1,.74f,.21f), true);
                view.FirstHealthText = Label("BlackHp", root.transform, "", .08f, .87f, .20f, .916f, 26);
                view.SecondHealthText = Label("WhiteHp", root.transform, "", .80f, .87f, .92f, .916f, 26);
                view.Names = Label("FighterNames", root.transform, "", .08f, .92f, .92f, .98f, 30);
                Plate(Rect("TimerPlate", root.transform, .455f, .854f, .545f, .953f), "UI_achieveItem_imgBK", Color.white);
                view.Clock = Label("Clock", root.transform, "", .45f, .862f, .55f, .941f, 55, true);
                view.Banner = Label("Banner", root.transform, "等待双方进入八角笼", .15f, .64f, .85f, .82f, 54);
                var shadow = view.Banner.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(.08f, .04f, .12f, .92f);
                shadow.effectDistance = new Vector2(2,-3);
                view.Jab = Button("Jab", root.transform, "普攻 · J", .57f, .07f, .69f, .135f);
                view.Skill = Button("Skill", root.transform, "技能 · K", .70f, .07f, .82f, .135f);
                view.Ultimate = Button("Ultimate", root.transform, "大招 · L", .83f, .07f, .95f, .135f);
                view.Exit = Button("Back", root.transform, "返回大厅", .035f, .18f, .165f, .235f);
                view.Reconnect = Button("Reconnect", root.transform, "重连", .17f, .18f, .29f, .235f);
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
                return new Dictionary<string, object> { ["scene"] = DuelScenePath, ["fighters"] = 2,
                    ["animationFrames"] = view.Idle.Length + view.Run.Length + view.AttackOne.Length + view.AttackTwo.Length + view.AttackThree.Length };
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

        private static void AddCage(Transform parent)
        {
            // 这是八角笼的只读舞台标记，边线不参与原生范围判定。
            Vector2[] points = { new Vector2(-680,-45), new Vector2(-485,-175), new Vector2(485,-175), new Vector2(680,-45),
                new Vector2(680,90), new Vector2(485,202), new Vector2(-485,202), new Vector2(-680,90) };
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 a = points[i], b = points[(i + 1) % points.Length], d = b - a;
                var line = Rect("CageEdge" + i, parent, .5f, .36f, .5f, .36f);
                line.anchoredPosition = (a + b) * .5f;
                line.sizeDelta = new Vector2(d.magnitude + 5, 8);
                line.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                Plate(line, "UI_achieveItem_imgBK", new Color(.53f,.29f,.10f,.86f));
            }
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
