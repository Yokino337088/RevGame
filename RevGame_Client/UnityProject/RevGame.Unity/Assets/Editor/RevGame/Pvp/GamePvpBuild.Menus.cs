using System;
using RevGame.Pvp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        private static void BuildMenus()
        {
            System.IO.Directory.CreateDirectory("Assets/GameRes/UI/Panel/Pvp/Menu");
            SetBorder("UI_Pvp_imgBK", new Vector4(150, 100, 100, 100));
            SetBorder("UI_achieveItem_imgBK", new Vector4(24, 12, 24, 12));
            Menu("PvpEntryPanel", typeof(PvpEntryPanel), root =>
            {
                Label("heading", root, "玩家对战", .25f, .79f, .75f, .9f, 46, true);
                Label("subtitle", root, "选择一种方式，与棋友相遇", .2f, .70f, .8f, .78f, 25, true);
                string[] names = { "server", "lan", "computer", "local" };
                string[] labels = { "服务器匹配", "局域网联机", "本地人机对局", "本地双人对局" };
                for (int i = 0; i < names.Length; i++)
                {
                    float y = .61f - i * .13f;
                    Button(names[i], root, labels[i], .34f, y, .66f, y + .085f, 31);
                }
            });
            Menu("PvpServerPanel", typeof(PvpServerPanel), root =>
            {
                Label("heading", root, "服务器匹配", .2f, .8f, .8f, .91f, 44, true);
                Label("nameLabel", root, "棋友昵称", .22f, .66f, .36f, .73f, 27, true);
                Input("nickname", root, "棋友", .38f, .66f, .76f, .73f, 16);
                Label("endpointLabel", root, "对战地址", .22f, .55f, .36f, .62f, 27, true);
                Input("endpoint", root, PvpServerPanel.DefaultEndpoint, .38f, .55f, .76f, .62f, 100);
                Button("mode", root, "新手场 · 二次确认落子", .29f, .44f, .71f, .51f, 25);
                Label("state", root, "准备连接", .2f, .33f, .8f, .42f, 29, true);
                Label("duration", root, "准备好后开始匹配", .25f, .24f, .75f, .32f, 30, true);
                Button("connect", root, "连接 / 重连", .27f, .12f, .48f, .21f, 28);
                Button("match", root, "开始匹配", .52f, .12f, .73f, .21f, 28);
            });
            Menu("PvpLanPanel", typeof(PvpLanPanel), root =>
            {
                Label("heading", root, "局域网联机", .2f, .8f, .8f, .91f, 44, true);
                Input("nickname", root, "棋友", .2f, .70f, .54f, .77f, 16);
                Button("search", root, "搜索同网设备", .57f, .70f, .8f, .77f, 27);
                Label("state", root, "连接同一Wi-Fi后开始搜索", .18f, .58f, .82f, .68f, 27, true);
                RectTransform scroll = Rect("scroll", root, .18f, .14f, .82f, .56f);
                var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
                scrollRect.horizontal = false;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
                RectTransform viewport = Rect("viewport", scroll, 0, 0, 1, 1);
                viewport.gameObject.AddComponent<RectMask2D>();
                RectTransform content = Rect("content", viewport, 0, 1, 1, 1);
                content.pivot = new Vector2(.5f, 1);
                content.sizeDelta = new Vector2(0, 116);
                scrollRect.viewport = viewport;
                scrollRect.content = content;
                RectTransform item = Rect("item", content, 0, 1, 1, 1);
                item.pivot = new Vector2(.5f, 1);
                item.sizeDelta = new Vector2(0, 106);
                Image image = Plate(item, "UI_achieveItem_imgBK", Color.white, true);
                var button = item.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                Label("name", item, "棋友 · 点击邀请", .04f, .52f, .96f, .96f, 27, true);
                Label("detail", item, "设备名称 / 机型", .04f, .04f, .96f, .53f, 21, true);
                item.gameObject.SetActive(false);
            });
            var notice = new GameObject("PvpNoticePanel", typeof(RectTransform), typeof(PvpNoticePanel));
            try
            {
                StretchRoot(notice);
                RectTransform banner = Rect("banner", notice.transform, .18f, 1, .82f, 1);
                banner.pivot = new Vector2(.5f, 1);
                banner.sizeDelta = new Vector2(0, 100);
                banner.anchoredPosition = new Vector2(0, 150);
                Plate(banner, "UI_achieveItem_imgBK", Color.white);
                Label("text", banner, "", .04f, .08f, .96f, .92f, 28, true);
                PrefabUtility.SaveAsPrefabAsset(notice, "Assets/GameRes/UI/Panel/Pvp/Menu/PvpNoticePanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(notice);
            }
        }
        private static void SetBorder(string name, Vector4 border)
        {
            var importer = AssetImporter.GetAtPath("Assets/GameRes/Art/UI/" + name + ".png") as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("缺少UI素材：" + name);
            }
            if (importer.spriteBorder != border)
            {
                importer.spriteBorder = border;
                importer.SaveAndReimport();
            }
        }
        private static void Menu(string name, Type panelType, Action<Transform> compose)
        {
            var root = new GameObject(name, typeof(RectTransform), panelType);
            try
            {
                StretchRoot(root);
                // 同一Sprite放大的中央不透明区域兜底，透明花边不会透出大厅或无尽背景。
                Plate(Rect("opaqueBacking", root.transform, -.4f, -.4f, 1.4f, 1.4f), "UI_Pvp_imgBK", Color.white, true);
                Plate(Rect("background", root.transform, .02f, .02f, .98f, .98f), "UI_Pvp_imgBK", Color.white, true);
                compose(root.transform);
                Button("back", root.transform, "返回", .06f, .07f, .20f, .14f, 27);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Pvp/Menu/" + name + ".prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void StretchRoot(GameObject root)
        {
            RectTransform rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
        }
    }
}
