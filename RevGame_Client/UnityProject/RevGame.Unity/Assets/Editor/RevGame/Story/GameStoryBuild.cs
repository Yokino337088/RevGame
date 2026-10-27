using System;
using System.Linq;
using RevGame.Data;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>编辑器幂等装配故事资产、对话框和真实棋盘引导浮层。运行时不拼UI、不读磁盘、不创建临时配置。</summary>
    public static class GameStoryBuild
    {
        private const string Root = "Assets/GameRes/UI/Panel/Story";
        private const string BookPath = "Assets/GameRes/Config/Story/StoryBook.asset";
        private static readonly Color Ink = new Color(0.12f, 0.24f, 0.22f);
        private static readonly Color Paper = new Color(0.97f, 0.94f, 0.85f);
        private static Font _font;

        /// <summary>用随机临时键检查真正的PlayerPrefs持久化；finally删除临时键，完全不碰玩家生产进度。</summary>
        public static string CheckProgress()
        {
            string key = "RevGame.Tests.Story." + Guid.NewGuid().ToString("N");
            try
            {
                var first = new RevGame.Services.StoryProgress(key);
                if (first.OpeningComplete || first.TutorialComplete) { throw new InvalidOperationException("新存档应未完成"); }
                first.CompleteOpening();
                var second = new RevGame.Services.StoryProgress(key);
                if (!second.OpeningComplete || second.TutorialComplete) { throw new InvalidOperationException("重建后应仅开场完成"); }
                second.CompleteTutorial();
                var third = new RevGame.Services.StoryProgress(key);
                if (!third.OpeningComplete || !third.TutorialComplete) { throw new InvalidOperationException("两项完成应持久化"); }
                third.SetTestSession(true);
                if (third.OpeningComplete || third.TutorialComplete) { throw new InvalidOperationException("内存隔离应从头开始"); }
                third.SetTestSession(false);
                if (!third.OpeningComplete || !third.TutorialComplete) { throw new InvalidOperationException("隔离测试不可改写存档"); }
                return "5项持久化与隔离断言通过";
            }
            finally { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); }
        }

        public static string Configure()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("请退出Play再装配剧情"); }
            Directory("Assets/GameRes/Art/Story"); Directory(Root); Directory("Assets/GameRes/Config/Story");
            const string guidePath = "Assets/GameRes/Art/Story/SlimeGuide.png";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(guidePath) == null)
            {
                string error = AssetDatabase.MoveAsset("Assets/ArtRes/棋子/序列帧/棋子_史莱姆/棋子_史莱姆_萌新.png", guidePath);
                if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(guidePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            Sprite guide = AssetDatabase.LoadAssetAtPath<Sprite>(guidePath);
            var book = AssetDatabase.LoadAssetAtPath<StoryBook>(BookPath);
            if (book == null)
            {
                book = ScriptableObject.CreateInstance<StoryBook>();
                book.Guide = guide;
                book.Episodes = GameStoryContent.Create();
                book.Episodes = book.Episodes.Concat(GameBossVictoryBuild.CreateAfterStories()).ToArray();
                AssetDatabase.CreateAsset(book, BookPath);
            }
            // 迁移旧静态向导：使用项目已有三帧身体动画，移动保留GUID，归StoryGuide唯一图集。
            book.GuideFrames = new Sprite[3];
            for (int i = 0; i < book.GuideFrames.Length; i++)
            {
                string name = "棋子_史莱姆_s1_000" + (i + 1) + ".png";
                string destination = "Assets/GameRes/Art/Story/GuideIdle_" + (i + 1) + ".png";
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(destination) == null)
                {
                    string previous = "Assets/GameRes/Art/Story/" + name;
                    if (AssetDatabase.LoadAssetAtPath<Texture2D>(previous) == null) { previous = "Assets/ArtRes/棋子/序列帧/棋子_史莱姆/" + name; }
                    string error = AssetDatabase.MoveAsset(previous, destination);
                    if (!string.IsNullOrEmpty(error)) { throw new InvalidOperationException(error); }
                }
                var frameImporter = (TextureImporter)AssetImporter.GetAtPath(destination);
                frameImporter.textureType = TextureImporterType.Sprite;
                frameImporter.spriteImportMode = SpriteImportMode.Single;
                frameImporter.mipmapEnabled = false;
                frameImporter.alphaIsTransparency = true;
                frameImporter.SaveAndReimport();
                book.GuideFrames[i] = AssetDatabase.LoadAssetAtPath<Sprite>(destination);
            }
            book.Guide = book.GuideFrames[0];
            // 只迁移被本次交互改变的教学句子，不覆盖用户可能已经修改的章节剧情。
            if (book.TutorialRevision < 2)
            {
                var lesson = book.Find("lesson").Lines;
                lesson[0].Text = "这里就是真正的战斗棋盘。先点击左侧金色框中的【剑士手牌】，再选择空格落子。";
                lesson[1].Text = "敌人只剩下方这一口气。点击棋盘上高亮的格子，亲手落子封住它！";
                lesson[4].Text = "换位成功！棋子会自动战斗，你负责布阵和换位。等待准备增益抵达，再点原来的【开始战斗】按钮，继续这一局！";
                lesson[2].Text = lesson[2].Text.Replace("金色格", "高亮格");
                lesson[3].Text = lesson[3].Text.Replace("金色空格", "高亮空格");
                book.TutorialRevision = 2;
            }
            // 紧凑提示只迁移已知默认句子；用户在SO自行编辑过的文案原样保留。
            if (book.TutorialRevision < 3)
            {
                var lesson = book.Find("lesson").Lines;
                if (lesson[0].Text == "这里就是真正的战斗棋盘。先点击左侧金色框中的【剑士手牌】，再选择空格落子。") { lesson[0].Text = "点击左侧魔法框中的剑士手牌，准备落子。"; }
                if (lesson[1].Text == "敌人只剩下方这一口气。点击棋盘上高亮的格子，亲手落子封住它！") { lesson[1].Text = "点击敌人下方的魔法框空格，封住最后一口气。"; }
                if (lesson[2].Text == "封锁成功！它还在场上，因为零气限制行动，不会直接扣光生命。现在点击左侧高亮格里的己方棋子，准备换位。") { lesson[2].Text = "零气会限制行动，不会直接消灭敌人。点击左侧框中的己方棋子。"; }
                if (lesson[3].Text == "再点左边的高亮空格。移开同伴后，对手会恢复一口气：换位会改变双方的局面，要看清再动！") { lesson[3].Text = "点击左边的魔法框空格换位，观察敌人恢复一口气。"; }
                if (lesson[4].Text == "换位成功！棋子会自动战斗，你负责布阵和换位。等待准备增益抵达，再点原来的【开始战斗】按钮，继续这一局！") { lesson[4].Text = "棋子会自动战斗。等待准备增益抵达，再点击开始战斗！"; }
                book.TutorialRevision = 3;
            }
            EditorUtility.SetDirty(book);
            book.Validate();
            _font = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameRes/UI/Panel/HomePanel.prefab").GetComponentInChildren<Text>(true).font;
            BuildStory(); BuildTutorial();
            EditorBuildSettings.scenes = EditorBuildSettings.scenes.Where(scene => scene.path != "Assets/Scenes/TutorialScene.unity").ToArray();
            AssetDatabase.DeleteAsset("Assets/Scenes/TutorialScene.unity");
            AssetDatabase.SaveAssets();
            return "剧情SO、下方对话框与GameScene引导浮层已装配；" + GameResourceBuild.Configure();
        }

        private static void Directory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) { return; }
            int slash = path.LastIndexOf('/'); Directory(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }

        private static RectTransform Node(string name, Transform parent, Vector2 min, Vector2 max, Vector2 offsetMin = default, Vector2 offsetMax = default)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)obj.transform; rect.SetParent(parent, false);
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax;
            return rect;
        }
        private static Image Box(string name, Transform parent, Vector2 min, Vector2 max, Color color)
        {
            var image = Node(name, parent, min, max).gameObject.AddComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
        }
        private static Text Label(string name, Transform parent, Vector2 min, Vector2 max, string text, int size, Color color)
        {
            var label = Node(name, parent, min, max).gameObject.AddComponent<Text>();
            label.font = _font; label.fontSize = size; label.color = color; label.text = text;
            label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }
        private static Button Button(string name, Transform parent, Vector2 min, Vector2 max, string text)
        {
            var image = Box(name, parent, min, max, new Color(0.19f, 0.39f, 0.29f)); image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            if (!string.IsNullOrEmpty(text)) { Label("Label", image.transform, Vector2.zero, Vector2.one, text, 30, Paper).alignment = TextAnchor.MiddleCenter; }
            return button;
        }
        private static GameObject RootObject(string name)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup));
            var rect = (RectTransform)root.transform; rect.sizeDelta = new Vector2(1920, 1080);
            return root;
        }
        private static void Save(GameObject root)
        {
            try { PrefabUtility.SaveAsPrefabAsset(root, Root + "/" + root.name + ".prefab"); }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>透明点击层承接“任意处下一句”；只有下方羊皮纸对话框可见，标题和全部文字都在框内。</summary>
        private static void BuildStory()
        {
            var root = RootObject("StoryPanel"); root.AddComponent<StoryPanel>();
            var view = Node("storyView", root.transform, Vector2.zero, Vector2.one); view.gameObject.AddComponent<StoryView>();
            var next = Button("btnNext", view, Vector2.zero, Vector2.one, "");
            next.image.color = Color.clear; next.transition = Selectable.Transition.None;
            var card = Box("DialogueCard", next.transform, new Vector2(.13f,.07f), new Vector2(.87f,.35f), Color.white);
            card.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_Settlement_imgBK.png");
            card.type = Image.Type.Sliced;
            var guide = Box("guide", card.transform, new Vector2(.025f,.18f), new Vector2(.17f,.81f), Color.white); guide.preserveAspect = true;
            Label("title", card.transform, new Vector2(.20f,.77f), new Vector2(.96f,.94f), "", 26, Ink);
            Label("speaker", card.transform, new Vector2(.20f,.59f), new Vector2(.94f,.77f), "", 25, new Color(.23f,.38f,.23f));
            Label("dialogue", card.transform, new Vector2(.20f,.18f), new Vector2(.94f,.59f), "", 30, Ink);
            Label("counter", card.transform, new Vector2(.20f,.035f), new Vector2(.94f,.16f), "", 20, Ink).alignment = TextAnchor.MiddleRight;
            Save(root);
        }

        /// <summary>只制作提示和透传高亮，没有格子、棋子、选牌替身或“完成”按钮。</summary>
        /// <summary>编辑器创建可调共享材质；运行时只切换引用，不逐帧克隆材质。</summary>
        private static Material HighlightMaterial(string name, float width, float opacity, float pulse, float fill)
        {
            Directory("Assets/GameRes/Material/UI");
            string path = "Assets/GameRes/Material/UI/" + name + ".mat";
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/UI/SlimeTutorialHighlight.shader");
            if (shader == null) { throw new InvalidOperationException("缺少教学魔法高亮Shader"); }
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader;
            material.SetColor("_Color", new Color(0.12f, 0.82f, 0.57f, opacity));
            material.SetColor("_Accent", new Color(0.45f, 0.35f, 0.88f, 1f));
            material.SetFloat("_Width", width); material.SetFloat("_PulseDepth", pulse);
            material.SetFloat("_PulseRate", 0.85f); material.SetFloat("_Fill", fill);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void BuildTutorial()
        {
            var root = RootObject("TutorialPanel"); root.AddComponent<TutorialPanel>();
            var hint = Box("Hint", root.transform, new Vector2(.76f,.30f), new Vector2(.98f,.75f), Paper);
            hint.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_Settlement_imgBK.png"); hint.type = Image.Type.Sliced;
            Label("progress", hint.transform, new Vector2(.07f,.67f), new Vector2(.54f,.94f), "", 24, Ink);
            var instruction = Label("instruction", hint.transform, new Vector2(.07f,.10f), new Vector2(.93f,.67f), "", 28, Ink);
            instruction.resizeTextForBestFit = true; instruction.resizeTextMinSize = 22; instruction.resizeTextMaxSize = 28;
            Label("liberty", hint.transform, new Vector2(.55f,.67f), new Vector2(.93f,.94f), "", 22, Ink);
            // 三份共享材质使用同一个Shader：提示框轻呼吸，UI目标更醒目，棋格额外闪烁。
            var material = HighlightMaterial("SlimeTutorialHighlight", 4.5f, 0.92f, 0.32f, 0.035f);
            var boardMaterial = HighlightMaterial("SlimeTutorialCell", 5f, 0.95f, 0.66f, 0.13f);
            var hintMaterial = HighlightMaterial("SlimeTutorialHint", 3.5f, 0.78f, 0.18f, 0f);
            var hintGlow = Box("hintGlow", hint.transform, Vector2.zero, Vector2.one, Color.white);
            hintGlow.material = hintMaterial;
            var focus = Box("focus", root.transform, Vector2.zero, Vector2.zero, Color.white);
            focus.material = material;
            var serializedPanel = new SerializedObject(root.GetComponent<TutorialPanel>());
            serializedPanel.FindProperty("boardFocusMaterial").objectReferenceValue = boardMaterial;
            serializedPanel.ApplyModifiedPropertiesWithoutUndo();
            Save(root);
        }
    }
}
