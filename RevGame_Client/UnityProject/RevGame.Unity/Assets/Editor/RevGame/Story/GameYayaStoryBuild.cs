// 项目窄制作配方：导入用户原画/遮罩、复用正式待机关键帧，装配大厅和全屏剧情Prefab，保留已有GUID。
using System;
using System.IO;
using System.Linq;
using RevGame.Data;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>鸦鸦大厅与故事资源制作，普通构建不调用此迁移；所有运行期仍走Revolution资源/UI门面。</summary>
    public static class GameYayaStoryBuild
    {
        /// <summary>独立大厅纹理/遮罩/材质目录；Default采样纹理不进Sprite图集。</summary>
        private const string LobbyRoot = "Assets/GameRes/Lobby";
        /// <summary>剧情仅复制选用差分，图集归属与大厅24帧待机分开。</summary>
        private const string Expressions = "Assets/GameRes/Art/Characters/Yaya/Expressions";
        /// <summary>固定美术编号：谄媚、高兴、嫌弃、两种害羞和生气。</summary>
        private static readonly int[] ExpressionIds = { 1, 3, 23, 11, 12, 13 };
        /// <summary>本次制作使用的项目中文子集字库，制作完成后不参与运行期加载。</summary>
        private static Font _font;
        /// <summary>一次制作对应本次需求的完整资源，随后由调用方执行图集/字库/AB门禁。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("鸦鸦资源制作要求编辑态");
            }
            Directory.CreateDirectory(LobbyRoot);
            Directory.CreateDirectory(Expressions);
            AssetDatabase.Refresh();
            string art = Path.GetFullPath("../../美术/AI生成/新版");
            Import(Path.Combine(art, "大厅图片/大厅_魔法小屋_背景.png"), LobbyRoot + "/MagicCottage.png", false);
            Import(Path.Combine(art, "大厅图片/大厅_魔法小屋_Shader遮罩_R天空G水面B发光.png"), LobbyRoot + "/MagicCottageMask.png", false);
            foreach (int id in ExpressionIds)
            {
                Import(Path.Combine(art, "表情差分/鸦鸦表情差分_透明/" + id + ".png"), Expressions + "/YayaExpression_" + id.ToString("D2") + ".png", true);
            }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(LobbyRoot + "/MagicCottageMaterial.mat");
            if (material == null)
            {
                Shader shader = Shader.Find("RevGame/Background/LobbyMagicCottage");
                if (shader == null)
                {
                    throw new InvalidOperationException("大厅小屋Shader未导入");
                }
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, LobbyRoot + "/MagicCottageMaterial.mat");
            }
            material.SetTexture("_ChapterTex", AssetDatabase.LoadAssetAtPath<Texture2D>(LobbyRoot + "/MagicCottage.png"));
            material.SetTexture("_RegionMask", AssetDatabase.LoadAssetAtPath<Texture2D>(LobbyRoot + "/MagicCottageMask.png"));
            EditorUtility.SetDirty(material);
            _font = AssetDatabase.LoadAssetAtPath<Font>("Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf");
            PatchHome();
            GameLobbySceneBuild.Configure();
            StoryBook book = AssetDatabase.LoadAssetAtPath<StoryBook>("Assets/GameRes/Config/Story/StoryBook.asset");
            if (book.YayaRevision < 1)
            {
                // 只在旧版迁移时设置初始立绘。资源配方重跑不得覆盖策划拖入的新图片、映射与逐句覆盖。
                book.ExpressionIds = ExpressionIds;
                book.GuideFrames = ExpressionIds.Select(id => AssetDatabase.LoadAssetAtPath<Sprite>(Expressions + "/YayaExpression_" + id.ToString("D2") + ".png")).ToArray();
                book.Guide = book.GuideFrames[1];
                book.Episodes = GameYayaStoryContent.Create();
                book.YayaRevision = 1;
            }
            EditorUtility.SetDirty(book);
            book.Validate();
            BuildStory();
            AssetDatabase.SaveAssets();
            return "鸦鸦大厅原画/RGB遮罩、24帧待机、6表情和全屏剧情已装配；请生成图集/字库后构建真实AB";
        }
        /// <summary>更新正式图片内容但保留.meta与GUID；Unity控制运行期尺寸，源美术库完整保留。</summary>
        private static void Import(string source, string path, bool sprite)
        {
            File.Copy(source, path, true);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.alphaIsTransparency = sprite;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = sprite ? 1024 : 2048;
            importer.sRGBTexture = !path.Contains("Mask");
            importer.textureCompression = TextureImporterCompression.Compressed;
            if (sprite)
            {
                importer.spritePixelsPerUnit = 100;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
            }
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = sprite ? 1024 : 2048, format = sprite ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1 });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = sprite ? 1024 : 2048, format = sprite ? TextureImporterFormat.ETC2_RGBA8 : TextureImporterFormat.ETC2_RGB4 });
            importer.SaveAndReimport();
        }
        /// <summary>创建规范RectTransform，锚点用0～1比例、偏移归零，保持16:9与其它窗口尺寸的一致布局。</summary>
        private static RectTransform Node(string name, Transform parent, Vector2 min, Vector2 max)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)obj.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }
        /// <summary>使用项目中文字体和浅金文字，标签不拦截点击；字号以1920×1080参考画布为单位。</summary>
        private static Text Label(string name, Transform parent, Vector2 min, Vector2 max, int size)
        {
            var text = Node(name, parent, min, max).gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.color = new Color(1f, .95f, .78f);
            text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;
            return text;
        }
        /// <summary>大厅UI只保留菜单；小屋和角色由StartScene Renderer显示，删除旧背景与临时UI角色节点。</summary>
        private static void PatchHome()
        {
            const string path = "Assets/GameRes/UI/Panel/HomePanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Image old = root.GetComponent<Image>();
                if (old != null)
                {
                    UnityEngine.Object.DestroyImmediate(old);
                }
                Transform oldBackground = root.transform.Find("LobbyBackground");
                if (oldBackground != null)
                {
                    UnityEngine.Object.DestroyImmediate(oldBackground.gameObject);
                }
                // 原大厅有独立screenBackground子节点，必须移除；只删根Image会留下彩色漩涡。
                Transform flowing = root.transform.Find("screenBackground");
                if (flowing != null)
                {
                    UnityEngine.Object.DestroyImmediate(flowing.gameObject);
                }
                Transform previous = root.transform.Find("Yaya");
                if (previous != null)
                {
                    UnityEngine.Object.DestroyImmediate(previous.gameObject);
                }
                string[] buttons = { "btnPlay", "btnEndless", "btnPvP", "btnQuit" };
                for (int i = 0; i < buttons.Length; i++)
                {
                    var rect = root.transform.Find(buttons[i]) as RectTransform;
                    rect.anchorMin = new Vector2(.65f, .55f - i * .115f);
                    rect.anchorMax = new Vector2(.92f, .64f - i * .115f);
                    rect.offsetMin = rect.offsetMax = Vector2.zero;
                }
                var title = root.transform.Find("title") as RectTransform;
                title.anchorMin = new Vector2(.56f, .76f);
                title.anchorMax = new Vector2(.96f, .90f);
                title.offsetMin = title.offsetMax = Vector2.zero;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        /// <summary>立绘独立置顶；对话框在右下不重叠，右上跳过始终可点击，背景和下一句层阻挡底层游戏。</summary>
        private static void BuildStory()
        {
            const string path = "Assets/GameRes/UI/Panel/Story/StoryPanel.prefab";
            var root = new GameObject("StoryPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(StoryPanel));
            ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 1080);
            try
            {
                var backdrop = Node("storyBackdrop", root.transform, Vector2.zero, Vector2.one).gameObject.AddComponent<RawImage>();
                backdrop.gameObject.AddComponent<StoryBackdrop>();
                backdrop.raycastTarget = true;
                backdrop.color = Color.white;
                var view = Node("storyView", root.transform, Vector2.zero, Vector2.one);
                view.gameObject.AddComponent<StoryView>();
                var click = Node("btnNext", view, Vector2.zero, Vector2.one).gameObject.AddComponent<Image>();
                click.color = Color.clear;
                click.raycastTarget = true;
                click.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;
                var card = Node("DialogueCard", view, new Vector2(.46f, .08f), new Vector2(.965f, .40f)).gameObject.AddComponent<Image>();
                card.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_ChooseLevel_imgTip2.png");
                card.type = Image.Type.Sliced;
                card.raycastTarget = false;
                if (card.sprite == null)
                {
                    throw new InvalidOperationException("对话框原图UI_ChooseLevel_imgTip2不存在");
                }
                // 原对话框必须配置九宫格，四角不随右侧宽框拉伸；修改Importer保留原GUID。
                var cardImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(card.sprite));
                if (card.sprite.border == Vector4.zero)
                {
                    cardImporter.spriteBorder = new Vector4(32, 32, 32, 32);
                    cardImporter.SaveAndReimport();
                    card.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_ChooseLevel_imgTip2.png");
                }
                Label("title", card.transform, new Vector2(.08f, .77f), new Vector2(.93f, .94f), 26);
                Label("speaker", card.transform, new Vector2(.08f, .62f), new Vector2(.93f, .78f), 30);
                Label("dialogue", card.transform, new Vector2(.08f, .18f), new Vector2(.93f, .61f), 32);
                Label("counter", card.transform, new Vector2(.08f, .03f), new Vector2(.93f, .17f), 20);
                var skip = Node("btnSkip", view, new Vector2(.85f, .90f), new Vector2(.98f, .975f)).gameObject.AddComponent<Image>();
                skip.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_Tavern_btnClose.png");
                skip.type = Image.Type.Sliced;
                skip.gameObject.AddComponent<Button>().targetGraphic = skip;
                Text skipText = Label("label", skip.transform, Vector2.zero, Vector2.one, 28);
                skipText.text = "跳过剧情";
                skipText.alignment = TextAnchor.MiddleCenter;
                skipText.color = new Color(.18f, .24f, .20f);
                var guide = Node("guide", view, new Vector2(.015f, .02f), new Vector2(.45f, .97f)).gameObject.AddComponent<Image>();
                guide.preserveAspect = true;
                guide.raycastTarget = false;
                guide.transform.SetAsLastSibling();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
