// ============================================================
// GameFeelBuilder.cs —— 由 Unity MCP 执行的表现资源装配。
// 只保存本项目的预制体和场景；不在运行时找磁盘美术，也不修改 Revolution 快照。
// ============================================================
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameFeelBuilder
    {
        public static string Apply()
        {
            if (EditorApplication.isPlaying)
            {
                throw new System.InvalidOperationException("表现预制体需要在非 Play 状态保存");
            }
            BuildNotice();
            GameArtMigration.ApplyAll();
            AssetDatabase.SaveAssets();
            return "DOTween 增益提示预制体、手机布局、1.15 单位拼格和点击区域已保存。\n" + GameArtMigration.Audit();
        }

        private static void BuildNotice()
        {
            GameObject root = GameAssetBuilder.Rect("BattleEventNotice", null, new Vector2(920, 148), Vector2.zero);
            try
            {
                CanvasGroup group = root.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
                Image background = root.AddComponent<Image>();
                background.sprite = GameAssetBuilder.Sprite("界面/新版/UI_Tip_imgBK.png");
                background.raycastTarget = false;
                // 金色侧条、粗体大字和黑描边增加冲击感，仍只引用用户指定的新版图片。
                Image icon = GameAssetBuilder.Picture("icon", root.transform, GameAssetBuilder.Sprite("界面/新版/UI_Setting_Checkmark.png"), new Vector2(72, 72), new Vector2(-340, 0));
                Text title = GameAssetBuilder.Label("title", root.transform, "战斗支援", new Vector2(620, 65), new Vector2(0, 30), 48);
                title.fontStyle = FontStyle.Bold;
                title.alignment = TextAnchor.MiddleCenter;
                title.color = new Color(1f, 0.88f, 0.2f);
                Outline outline = title.gameObject.AddComponent<Outline>();
                outline.effectColor = Color.black;
                outline.effectDistance = new Vector2(2, -2);
                Text detail = GameAssetBuilder.Label("detail", root.transform, "增益效果已生效", new Vector2(640, 55), new Vector2(0, -32), 27);
                detail.fontStyle = FontStyle.Bold;
                detail.alignment = TextAnchor.MiddleCenter;
                detail.color = Color.white;
                BattleEventNoticeView view = root.AddComponent<BattleEventNoticeView>();
                GameAssetBuilder.SetReference(view, "group", group);
                GameAssetBuilder.SetReference(view, "titleText", title);
                GameAssetBuilder.SetReference(view, "detailText", detail);
                GameAssetBuilder.SetReference(view, "icon", icon);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/Effects/BattleEventNotice.prefab");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
