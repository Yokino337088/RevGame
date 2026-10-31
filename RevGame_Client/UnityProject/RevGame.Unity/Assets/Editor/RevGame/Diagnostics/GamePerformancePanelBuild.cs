// 使用项目现有Prefab制作工具，真实UGUI/字体/AB加载。只生成新面板，不重写用户已有布局。
using System;
using RevGame.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GamePerformancePanelBuild
    {
        public static string AdjustLayout()
        {
            const string path = "Assets/GameRes/UI/Panel/Diagnostics/PerformanceMonitorPanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var card = (RectTransform)root.transform.Find("card");
                card.sizeDelta = new Vector2(620, 810);
                var details = (RectTransform)card.Find("details");
                details.sizeDelta = new Vector2(590, 670);
                details.anchoredPosition = new Vector2(-305, -470);
                card.Find("btnFold/caption").GetComponent<Text>().text = "折叠";
                card.Find("btnClose/caption").GetComponent<Text>().text = "关闭";
                card.Find("summary").GetComponent<Text>().text = "性能监测 · F8 开关";
                details.GetComponent<Text>().text = "等待性能采样";
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            return "性能面板高度810，详情670，完整保留底部环境和负载信息";
        }

        public static string Configure()
        {
            const string folder = "Assets/GameRes/UI/Panel/Diagnostics";
            const string path = folder + "/PerformanceMonitorPanel.prefab";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/UI/Panel", "Diagnostics");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                var root = new GameObject("PerformanceMonitorPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(PerformanceMonitorPanel));
                try
                {
                    GameAssetBuilder.Stretch((RectTransform)root.transform);
                    var card = GameAssetBuilder.Rect("card", root.transform, new Vector2(620, 810), Vector2.zero);
                    var rect = (RectTransform)card.transform;
                    rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
                    rect.anchoredPosition = new Vector2(-18, -18);
                    var image = card.AddComponent<Image>();
                    image.color = new Color(.07f, .13f, .13f, .94f);
                    image.raycastTarget = true;
                    Label("summary", card.transform, "性能监测 · F8 开关", 26, new Vector2(410, 95), new Vector2(-395, -58));
                    Label("details", card.transform, "等待性能采样", 20, new Vector2(590, 670), new Vector2(-305, -470));
                    Button("btnFold", card.transform, "折叠", -140);
                    Button("btnClose", card.transform, "关闭", -55);
                    if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                    {
                        throw new InvalidOperationException("性能面板Prefab保存失败");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
            return GameResourceBuild.Configure();
        }

        private static Text Label(string name, Transform parent, string text, int size, Vector2 dimensions, Vector2 position)
        {
            Text label = GameAssetBuilder.Label(name, parent, text, dimensions, position, size);
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = Vector2.one;
            label.alignment = TextAnchor.UpperLeft;
            label.color = new Color(.89f, .95f, .87f);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            return label;
        }

        private static void Button(string name, Transform parent, string caption, float x)
        {
            var obj = GameAssetBuilder.Rect(name, parent, new Vector2(80, 42), new Vector2(x, -28));
            var rect = (RectTransform)obj.transform;
            rect.anchorMin = rect.anchorMax = Vector2.one;
            var image = obj.AddComponent<Image>();
            image.color = new Color(.2f, .4f, .33f);
            var button = obj.AddComponent<Button>();
            button.targetGraphic = image;
            var label = GameAssetBuilder.Label("caption", obj.transform, caption, new Vector2(80, 42), Vector2.zero, 20);
            label.color = Color.white;
        }
    }
}
