// 只装配本项目的表现资产：死亡材质、胜利弹幕与剧情SO，不改框架或用户正在预览的场景。
using System;
using System.Collections.Generic;
using System.Linq;
using RevGame.Data;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>幂等制作入口：首次写默认美术参数，重复执行保留Inspector调色和用户剧情。</summary>
    public static class GameBossVictoryBuild
    {
        public static readonly string[] Names = { "Chapter01_Stardust", "Chapter02_Sunlight", "Chapter03_Water", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Cross" };
        private static readonly Color[] Primary = { new Color(.28f,.56f,1), new Color(1,.69f,.17f), new Color(.08f,.58f,.96f), new Color(.3f,.2f,.08f), new Color(.45f,.81f,1), new Color(1,.83f,.37f) };
        private static readonly Color[] Secondary = { new Color(.88f,.7f,1), new Color(1,.98f,.72f), new Color(.62f,1,1), new Color(.5f,.62f,.16f), Color.white, Color.white };
        private const string MaterialRoot = "Assets/GameRes/Material/BossDeath";
        private const string PanelRoot = "Assets/GameRes/UI/Panel/Victory";

        /// <summary>六章各用独立Shader和两份共享材质；表现时长只影响演出，允许SO调整，不进战斗数值表。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("Boss演出制作需要编辑态"); }
            Folder(MaterialRoot); Folder(PanelRoot);
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                string name = Names[chapter - 1];
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/BossDeath/" + name + ".shader");
                if (shader == null) { throw new InvalidOperationException("Boss死亡Shader缺失：" + name); }
                BossChapterArtConfig config = AssetDatabase.LoadAssetAtPath<BossChapterArtConfig>("Assets/GameRes/Config/Boss/" + BossChapterArtConfig.ResourceName(chapter) + ".asset");
                if (config == null) { throw new InvalidOperationException("Boss章节美术缺失"); }
                config.DeathMaterial = Material(name, shader, false, chapter);
                config.DeathAuraMaterial = Material(name, shader, true, chapter);
                if (config.DeathSeconds < 2 || config.DeathSeconds > 6) { config.DeathSeconds = 3.2f; }
                EditorUtility.SetDirty(config); AssetDatabase.SaveAssetIfDirty(config);
            }
            CreatePanel();
            CreateIntroPanel();
            EnsurePrepareGroup();
            var book = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
            if (book == null) { throw new InvalidOperationException("先制作现有剧情资产"); }
            var episodes = new List<StoryBook.Episode>(book.Episodes);
            foreach (StoryBook.Episode episode in CreateAfterStories())
            {
                if (!episodes.Any(item => item.Id == episode.Id)) { episodes.Add(episode); }
            }
            book.Episodes = episodes.ToArray(); book.Validate();
            EditorUtility.SetDirty(book); AssetDatabase.SaveAssetIfDirty(book);
            return "六个死亡Shader/十二共享材质、胜利积分预制体和六章收尾SO已装配；保留已有参数/剧情/场景";
        }

        private static Material Material(string name, Shader shader, bool aura, int chapter)
        {
            string path = MaterialRoot + "/" + name + (aura ? "_Aura" : "_Body") + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) { return material; }
            material = new Material(shader);
            material.SetFloat("_Overlay", aura ? 1 : 0);
            material.SetFloat("_Intensity", aura ? 1.65f : 1.4f);
            material.SetColor("_Primary", Primary[chapter - 1]);
            material.SetColor("_Secondary", Secondary[chapter - 1]);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        /// <summary>用现有结算羊皮纸与Noto字体；整体居中，动画由面板类负责，制作代码不进入安装包。</summary>
        private static void CreatePanel()
        {
            string path = PanelRoot + "/BossVictoryPanel.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) { return; }
            var root = new GameObject("BossVictoryPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(BossVictoryPanel));
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(1920,1080);
                var banner = GameAssetBuilder.Rect("banner", root.transform, new Vector2(960,300), Vector2.zero);
                var image = banner.AddComponent<Image>();
                string guid = AssetDatabase.FindAssets("UI_Settlement_imgBK t:Sprite", new[] { "Assets/GameRes/Art" }).FirstOrDefault();
                image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GUIDToAssetPath(guid));
                if (image.sprite == null) { throw new InvalidOperationException("胜利面板底图缺失"); }
                image.type = Image.Type.Sliced; image.color = new Color(1,.98f,.87f,.96f); image.raycastTarget = false;
                Text title = Label("headline", banner.transform, "BOSS 已击败！", new Vector2(880,88), new Vector2(0,58), 52);
                Text score = Label("chapterScore", banner.transform, "本章总积分", new Vector2(880,66), new Vector2(0,-38), 38);
                title.color = new Color(.2f,.3f,.22f); score.color = new Color(.43f,.28f,.1f);
                title.raycastTarget = false; score.raycastTarget = false;
                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null) { throw new InvalidOperationException("胜利弹幕保存失败"); }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static Text Label(string name, Transform parent, string value, Vector2 size, Vector2 position, int fontSize)
        {
            Text text = GameAssetBuilder.Rect(name, parent, size, position).AddComponent<Text>();
            text.font = GameAssetBuilder.Font(); text.text = value; text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            return text;
        }

        /// <summary>只补整体透明度组件，保留用户原有六张手牌和全部布局；出生期间隐藏整组而非销毁重开。</summary>
        private static void EnsurePrepareGroup()
        {
            const string path = "Assets/GameRes/UI/Panel/PreparePanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root.GetComponent<CanvasGroup>() != null) { return; }
                root.AddComponent<CanvasGroup>(); PrefabUtility.SaveAsPrefabAsset(root,path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>登场横幅复用现有羊皮纸，单独金色闪光层，不新增大贴图；闪烁由序列传入进度。</summary>
        private static void CreateIntroPanel()
        {
            string path = PanelRoot + "/BossIntroPanel.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) { return; }
            var root = new GameObject("BossIntroPanel",typeof(RectTransform),typeof(CanvasGroup),typeof(BossIntroPanel));
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(1920,1080);
                GameObject banner = GameAssetBuilder.Rect("banner",root.transform,new Vector2(1040,300),new Vector2(0,140));
                Image image = banner.AddComponent<Image>();
                string guid = AssetDatabase.FindAssets("UI_Settlement_imgBK t:Sprite",new[]{"Assets/GameRes/Art"}).FirstOrDefault();
                image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GUIDToAssetPath(guid));
                if (image.sprite == null) { throw new InvalidOperationException("登场底图缺失"); }
                image.type = Image.Type.Sliced; image.raycastTarget=false;
                Image flare = GameAssetBuilder.Rect("flare",banner.transform,new Vector2(990,242),Vector2.zero).AddComponent<Image>();
                flare.sprite = image.sprite; flare.type=Image.Type.Sliced; flare.color=new Color(1,.82f,.3f,.2f); flare.raycastTarget=false;
                Label("headline",banner.transform,"BOSS 已出现！",new Vector2(920,85),new Vector2(0,50),54);
                Label("subtitle",banner.transform,"强敌降临 · 布阵迎战",new Vector2(920,65),new Vector2(0,-44),34);
                if (PrefabUtility.SaveAsPrefabAsset(root,path)==null) { throw new InvalidOperationException("登场面板保存失败"); }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static StoryBook.Episode Episode(int chapter, string title, params string[] texts)
        {
            var lines = new StoryBook.Line[texts.Length];
            for (int index = 0; index < texts.Length; index++)
            {
                int split = texts[index].IndexOf('|');
                lines[index] = new StoryBook.Line { Speaker = texts[index].Substring(0, split), Text = texts[index].Substring(split + 1) };
            }
            return new StoryBook.Episode { Id = "boss_after_" + chapter, Title = title, Lines = lines };
        }

        /// <summary>仅首次创建SO的文案种子；运行时以SO为准，后续编辑不会被覆盖。</summary>
        public static StoryBook.Episode[] CreateAfterStories()
        {
            return new[]
            {
                Episode(1,"第一章终幕 · 星光相连",
                    "旁白|巡路剑士的铠甲化成一串星尘，落进棋坪的缝隙。被堵住的光，终于又流向了远方。",
                    "巡路剑士|我以为站得越远，就越不会连累同伴。原来孤单的棋子，才最容易失去呼吸。",
                    "团团|第一张棋谱回来啦！它写着：连结不是挤在一起，是愿意替朋友留一条路。",
                    "冒险家|那我们沿着这条星光路去村子。下一局，也一起下。"),
                Episode(2,"第二章终幕 · 阳光下的一手",
                    "旁白|弓手收起紧绷的弦。金色阳光穿过村门，屋顶的魔法灯一盏接一盏亮了起来。",
                    "村口弓手|我一直守着最稳的阵形，却忘了村门本来就是为了迎接客人。",
                    "冒险家|借势不只是借一颗棋子的力量。愿意相信彼此，整片棋盘都会成为我们的后盾。",
                    "团团|第二页棋谱在发光！村长说，镜湖的水流会带我们找到第三页。"),
                Episode(3,"第三章终幕 · 放开倒影",
                    "旁白|水法师的倒影被湖流轻轻卷走。湖面静下来，露出一条通往沼泽的旧栈道。",
                    "水法师|我争每一片水面，只怕失去一格就会失去全部。可攥得越紧，倒影就越破碎。",
                    "冒险家|舍掉一处，才能照顾更大的局面。棋盘上的取舍，也是在为同伴留下选择。",
                    "团团|第三页回来啦。前面泥泞的路，就让我们互相拉着走吧！"),
                Episode(4,"第四章终幕 · 泥中生路",
                    "旁白|刺客的影子沉入泥潭，缠绕道路的沼气散开。几株新芽从刚空出的棋格里探出了头。",
                    "沼泽刺客|我只会封住别人的退路。到最后，连我自己也找不到出口。",
                    "团团|第四页写的是“气眼”！留一口气，不是软弱，是让每个同伴还有机会转身。",
                    "冒险家|你也可以重新选一条路。我们去雪原，找回剩下的棋谱。"),
                Episode(5,"第五章终幕 · 冰封之后",
                    "旁白|盾卫被蓝白冰晶包裹，冰壳随即裂成细雪。它仍握着盾，却不再挡住通往神殿的路。",
                    "雪原盾卫|我想用最厚的盾保护大家，却把大家都关在了风雪里。",
                    "冒险家|真正的眼位给人呼吸，也给人自由。守护不必填满每一格，空白也能成为温暖。",
                    "团团|第五页到手！把这份信任带给守谱者吧。它等那最后一手，已经太久了。"),
                Episode(6,"第六章终幕 · 留给明天的一手",
                    "旁白|十字圣光划开神殿的穹顶。六页棋谱合为一体，拼出的竟是一张留有空白的棋盘。",
                    "守谱者|你们正面破开我的防线，也让我看见了最后一口气。原来世界需要的不是永远不变的胜局。",
                    "冒险家|是愿意继续下棋的朋友。会输，会改变，也会一起开始下一局。",
                    "团团|回家的门开啦！冒险家，你还会回来吗？我还欠你一场堂堂正正的胜利呢！",
                    "冒险家|会的。替我留个位置吧。下一手，我们明天再下。")
            };
        }

        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) { return; }
            int split = path.LastIndexOf('/');
            Folder(path.Substring(0,split)); AssetDatabase.CreateFolder(path.Substring(0,split),path.Substring(split+1));
        }
    }
}
