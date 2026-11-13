using System;
using RevGame.Data;
using RevGame.Pvp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        /// <summary>仅就地更新对局两个预制体。使用Unity序列化保留GUID和现有菜单布局。</summary>
        public static object BuildMatchHud()
        {
            _font = GameAssetBuilder.Font();
            const string panelPath = "Assets/GameRes/UI/Panel/Pvp/PvpPanel.prefab";
            const string boardPath = "Assets/GameRes/Pvp/PvpBoardView.prefab";
            GameObject panel = PrefabUtility.LoadPrefabContents(panelPath);
            try
            {
                ConfigureMatchHud(panel.transform);
                PrefabUtility.SaveAsPrefabAsset(panel, panelPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(panel);
            }

            GameObject board = PrefabUtility.LoadPrefabContents(boardPath);
            try
            {
                var data = new SerializedObject(board.GetComponent<PvpBoardView>());
                BindEyeSprite(data);
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(board, boardPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(board);
            }

            AssetDatabase.SaveAssets();
            return new { success = true, text = "左侧弹幕、右侧鸦鸦与计分面板、棋盘双眼已装配" };
        }

        private static void BindEyeSprite(SerializedObject data)
        {
            SerializedProperty frames = data.FindProperty("eyeBlinkFrames");
            frames.arraySize = 4;
            for (int i = 0; i < frames.arraySize; i++)
            {
                string path = "Assets/GameRes/Art/Status/EyeBlink/EyeBlink_" + i.ToString("D2") + ".png";
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                {
                    throw new InvalidOperationException("缺少六章眼位帧 " + path);
                }
                frames.GetArrayElementAtIndex(i).objectReferenceValue = sprite;
            }
        }

        private static void ConfigureMatchHud(Transform root)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<YayaExpressionCatalog>("Assets/GameRes/Config/Yaya/YayaExpressionCatalog.asset");
            if (catalog == null)
            {
                throw new InvalidOperationException("缺少闯关和无尽共用的鸦鸦正式表情目录");
            }
            var panelData = new SerializedObject(root.GetComponent<PvpPanel>());
            panelData.FindProperty("portraitCatalog").objectReferenceValue = catalog;
            panelData.ApplyModifiedPropertiesWithoutUndo();
            foreach (string obsolete in new[] { "status", "preview", "agentState", "boardToast", "yayaPortrait", "yayaBubble" })
            {
                Transform node = root.Find(obsolete);
                if (node != null)
                {
                    UnityEngine.Object.DestroyImmediate(node.gameObject);
                }
            }

            RectTransform toast = Rect("boardToast", root, .022f, .59f, .226f, .72f);
            Plate(toast, "UI_Tip_imgBK", Color.white);
            toast.gameObject.AddComponent<CanvasGroup>();
            var notice = Label("text", toast, "", .08f, .08f, .92f, .92f, 26);
            notice.verticalOverflow = VerticalWrapMode.Truncate;
            toast.gameObject.SetActive(false);

            RectTransform portrait = Rect("yayaPortrait", root, .765f, .12f, .985f, .64f);
            var image = portrait.gameObject.AddComponent<Image>();
            image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Characters/Yaya/Expressions/YayaExpression_26.png");
            if (image.sprite == null)
            {
                throw new InvalidOperationException("缺少鸦鸦正式立绘");
            }
            image.preserveAspect = true;
            image.raycastTarget = false;
            portrait.gameObject.SetActive(false);

            // 右侧气泡向上扩展，保留鸦鸦脸部空间。固定字号让长短句都同样清楚。
            RectTransform bubble = Rect("yayaBubble", root, .74f, .623f, .985f, .755f);
            bubble.pivot = new Vector2(.5f, 0);
            Plate(bubble, "UI_Tip_imgBK", Color.white);
            var bubbleGroup = bubble.gameObject.AddComponent<CanvasGroup>();
            bubbleGroup.blocksRaycasts = false;
            var line = Label("text", bubble, "", 0, 0, 1, 1, 34);
            line.rectTransform.offsetMin = new Vector2(22, 16);
            line.rectTransform.offsetMax = new Vector2(-22, -16);
            line.color = new Color(1, .98f, .92f);
            line.lineSpacing = 1.14f;
            var outline = line.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(.075f, .047f, .035f, .94f);
            outline.effectDistance = new Vector2(1.6f, -1.6f);
            bubble.gameObject.SetActive(false);

            foreach (string name in new[] { "scoring", "result" })
            {
                var side = (RectTransform)root.Find(name);
                side.anchorMin = new Vector2(.765f, side.anchorMin.y);
                side.anchorMax = new Vector2(.985f, side.anchorMax.y);
            }
            var result = (RectTransform)root.Find("result");
            result.anchorMin = new Vector2(.765f, .14f);
            result.anchorMax = new Vector2(.985f, .81f);
            if (!result.TryGetComponent<CanvasGroup>(out _))
            {
                result.gameObject.AddComponent<CanvasGroup>();
            }
            Text totals = result.Find("text").GetComponent<Text>();
            totals.resizeTextForBestFit = true;
            totals.resizeTextMinSize = 17;
            totals.resizeTextMaxSize = 27;
            totals.supportRichText = true;
            totals.verticalOverflow = VerticalWrapMode.Truncate;

            // 反馈层在确认窗之下，视觉动画不会遮盖模态窗口的内容。
            int overlayIndex = root.Find("invite").GetSiblingIndex();
            toast.SetSiblingIndex(overlayIndex);
            portrait.SetSiblingIndex(overlayIndex + 1);
            bubble.SetSiblingIndex(overlayIndex + 2);
            ConfigureDefeat(root);
        }

        private static void ConfigureDefeat(Transform root)
        {
            const string path = "Assets/GameRes/Art/UI/PvpDefeat/PvpDefeatArt.png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("缺少用户提供的败北立绘");
            }
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }
            Transform old = root.Find("defeat");
            if (old != null)
            {
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            }

            // 全屏底板拦截下层按钮，立绘维持原比例，台词落在画面右侧留白。
            RectTransform defeat = Rect("defeat", root, 0, 0, 1, 1);
            // 底板沿用对战菜单的不透明Sprite。向外多铺一圈，屏幕边缘也看不到下层棋盘。
            Plate(Rect("backdrop", defeat, -.12f, -.12f, 1.12f, 1.12f), "UI_Pvp_imgBK", new Color(.12f, .16f, .21f, 1), true);
            var art = Rect("art", defeat, 0, 0, 1, 1).gameObject.AddComponent<Image>();
            art.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            art.preserveAspect = true;
            art.raycastTarget = false;
            Text line = Label("line", defeat, "弱者就应该被本鸦踩在脚下狠狠地羞辱一番", .66f, .48f, .95f, .74f, 45);
            line.resizeTextForBestFit = true;
            line.resizeTextMinSize = 25;
            line.resizeTextMaxSize = 45;
            Button("closeDefeat", defeat, "查看结算", .76f, .12f, .93f, .19f, 27);
            defeat.gameObject.SetActive(false);
        }
    }
}
