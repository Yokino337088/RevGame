using System;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    /// <summary>只生成本次积分零件；不重建其它HUD内容或用户场景。资源仍经统一AB/图集门禁配置。</summary>
    public static class GameScoreBuilder
    {
        public static string Configure()
        {
            const string materialPath = "Assets/GameRes/Material/Combat/ScoreSpark.mat";
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameShader/Combat/ScoreSpark.shader");
            if (shader == null) { throw new InvalidOperationException("积分Shader尚未导入"); }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, materialPath); }
            else { material.shader = shader; EditorUtility.SetDirty(material); }
            GameObject spark = GameAssetBuilder.Rect("ScoreSpark", null, new Vector2(60, 60), Vector2.zero);
            try
            {
                Image glow = spark.AddComponent<Image>(); glow.material = material; glow.raycastTarget = false;
                GameAssetBuilder.SetReference(spark.AddComponent<ScoreSparkView>(), "glow", glow);
                PrefabUtility.SaveAsPrefabAsset(spark, "Assets/GameRes/Effects/Combat/ScoreSpark.prefab");
            }
            finally { Object.DestroyImmediate(spark); }
            const string hudPath = "Assets/GameRes/UI/Panel/BattleHudPanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(hudPath);
            try
            {
                Transform old = root.transform.Find("scoreFrame");
                if (old != null) { Object.DestroyImmediate(old.gameObject); }
                GameObject frame = GameAssetBuilder.Rect("scoreFrame", root.transform, new Vector2(330, 310), new Vector2(-750, 60));
                Image background = frame.AddComponent<Image>(); background.color = new Color(.045f, .075f, .12f, .9f); background.raycastTarget = false;
                Image top = GameAssetBuilder.Rect("GoldRule", frame.transform, new Vector2(300, 3), new Vector2(0, 143)).AddComponent<Image>();
                top.color = new Color(1, .72f, .28f); top.raycastTarget = false;
                Label(frame.transform, "RoundCaption", "本 回 合 积 分", new Vector2(0, 116), 24, new Color(1, .85f, .49f));
                ScoreReelView round = Reel(frame.transform, "RoundReel", 9, 30, 46, new Vector2(0, 64), 36, Color.white);
                Label(frame.transform, "MultiplierCaption", "得 分 倍 率", new Vector2(-65, 5), 21, new Color(.58f, .84f, 1));
                ScoreReelView multiplier = Reel(frame.transform, "MultiplierReel", 4, 23, 34, new Vector2(84, 5), 26, new Color(.48f, .96f, 1f));
                // 数字滚筒显示百分位定点数，静态小数点放在后两位之间，不参与任何计分运算。
                Text dot = Label(multiplier.transform, "Decimal", ".", new Vector2(0, -3), 26, new Color(.48f, .96f, 1)); dot.rectTransform.sizeDelta = new Vector2(12, 34);
                Text times = Label(frame.transform, "Times", "×", new Vector2(26, 5), 24, new Color(.48f, .96f, 1)); times.rectTransform.sizeDelta = new Vector2(22, 34);
                dot.verticalOverflow = times.verticalOverflow = VerticalWrapMode.Overflow;
                dot.horizontalOverflow = times.horizontalOverflow = HorizontalWrapMode.Overflow;
                var multiplierSettings = new SerializedObject(multiplier);
                multiplierSettings.FindProperty("fractionalDigits").intValue = 2;
                multiplierSettings.FindProperty("decimalPoint").objectReferenceValue = dot;
                multiplierSettings.ApplyModifiedPropertiesWithoutUndo();
                Label(frame.transform, "LifetimeCaption", "永 久 总 积 分", new Vector2(0, -52), 20, new Color(.65f, .73f, .85f));
                ScoreReelView lifetime = Reel(frame.transform, "LifetimeReel", 10, 25, 34, new Vector2(0, -87), 25, new Color(1, .83f, .45f));
                Text gain = Label(frame.transform, "scoreGain", "", new Vector2(0, -128), 22, new Color(.5f, 1, .82f));
                var hud = root.GetComponent<BattleHudPanel>();
                GameAssetBuilder.SetReference(hud, "roundReel", round); GameAssetBuilder.SetReference(hud, "lifetimeReel", lifetime);
                GameAssetBuilder.SetReference(hud, "multiplierReel", multiplier); GameAssetBuilder.SetReference(hud, "scoreFrame", frame.transform);
                GameAssetBuilder.SetReference(hud, "scoreGain", gain);
                // 独立Canvas仅隔离积分滚动重建；没有GraphicRaycaster，绝不挡住落子触控。
                frame.AddComponent<Canvas>().overrideSorting = false;
                PrefabUtility.SaveAsPrefabAsset(root, hudPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            return "积分HUD、三组滚筒、共享Shader飞星已装配；待统一资源配置及真实AB验收";
        }

        private static Text Label(Transform parent, string name, string content, Vector2 position, int fontSize, Color color)
        {
            Text text = GameAssetBuilder.Label(name, parent, content, new Vector2(300, 36), position, fontSize);
            text.color = color; return text;
        }

        /// <summary>编辑时创建固定数量的列，运行时不创建遮罩或文字；上溢时完整显示长整数，不截掉高位。</summary>
        private static ScoreReelView Reel(Transform parent, string name, int count, float width, float height, Vector2 position, int fontSize, Color color)
        {
            GameObject root = GameAssetBuilder.Rect(name, parent, new Vector2(count * width, height), position);
            // 每整行共享一个裁剪窗口。逐列RectMask2D会把同一字体拆成十多个Draw Call，横向各列本来就不重叠。
            root.AddComponent<RectMask2D>();
            ScoreReelView reel = root.AddComponent<ScoreReelView>();
            var so = new SerializedObject(reel);
            SerializedProperty lower = so.FindProperty("lower"), upper = so.FindProperty("upper");
            lower.arraySize = upper.arraySize = count; so.FindProperty("rowHeight").floatValue = height;
            for (int i = 0; i < count; i++)
            {
                GameObject window = GameAssetBuilder.Rect("Digit" + i, root.transform, new Vector2(width, height), new Vector2((i - (count - 1) * .5f) * width, 0));
                Text a = GameAssetBuilder.Label("Current", window.transform, "0", new Vector2(width, height), Vector2.zero, fontSize);
                Text b = GameAssetBuilder.Label("Next", window.transform, "1", new Vector2(width, height), new Vector2(0, height), fontSize);
                a.color = b.color = color;
                // 当前CJK字体的行高大于字号；让Text生成完整字形，再由窗口裁剪。Truncate会直接生成零个顶点。
                a.verticalOverflow = b.verticalOverflow = VerticalWrapMode.Overflow;
                a.horizontalOverflow = b.horizontalOverflow = HorizontalWrapMode.Overflow;
                lower.GetArrayElementAtIndex(i).objectReferenceValue = a; upper.GetArrayElementAtIndex(i).objectReferenceValue = b;
            }
            Text overflow = GameAssetBuilder.Label("Overflow", root.transform, "", new Vector2(count * width, height), Vector2.zero, fontSize);
            overflow.color = color; overflow.resizeTextForBestFit = true; overflow.resizeTextMinSize = 14; overflow.resizeTextMaxSize = fontSize;
            so.FindProperty("overflow").objectReferenceValue = overflow; overflow.gameObject.SetActive(false);
            so.ApplyModifiedPropertiesWithoutUndo(); return reel;
        }
    }
}
