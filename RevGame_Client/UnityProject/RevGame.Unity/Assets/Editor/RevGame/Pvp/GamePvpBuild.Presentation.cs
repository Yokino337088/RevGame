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
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Status/CartoonEyes.png");
            if (sprite == null)
            {
                throw new InvalidOperationException("缺少闯关模式双眼图标");
            }
            data.FindProperty("eyeSprite").objectReferenceValue = sprite;
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

            // 右栏宽度是画布22%，对话框居中占其中一半，向上留出长台词换行空间。
            RectTransform bubble = Rect("yayaBubble", root, .82f, .61f, .93f, .84f);
            Plate(bubble, "UI_Tip_imgBK", Color.white);
            bubble.gameObject.AddComponent<CanvasGroup>();
            Label("name", bubble, "鸦鸦", .08f, .73f, .92f, .96f, 22).color = new Color(1, .80f, .39f);
            var line = Label("text", bubble, "", .075f, .07f, .925f, .75f, 23);
            line.verticalOverflow = VerticalWrapMode.Truncate;
            line.resizeTextForBestFit = true;
            line.resizeTextMinSize = 15;
            line.resizeTextMaxSize = 23;
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
        }
    }
}
