using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>装配大厅五个常用入口的浅色插画和文字，保留现有预制体布局与按钮回调。</summary>
    internal static class GameLobbyButtonBuild
    {
        /// <summary>无尽入口首次生成和修改已有预制体时使用的同一文案。</summary>
        internal const string EndlessTitle = "无尽模式";

        /// <summary>大厅入口独立插画的正式 Sprite 目录，由大厅共用图集打包。</summary>
        private const string Art = "Assets/GameRes/Art/UI/LobbyButtons/";

        /// <summary>浅色纸面上的深墨色，沿用对战标题的字色。</summary>
        private static readonly Color32 Ink = new Color32(0x14, 0x29, 0x30, 255);

        /// <summary>按现有按钮名选择插画，把文字放入避开左侧图案的区域。素材或组件缺失时阻止保存。</summary>
        internal static void Apply(Button button)
        {
            string spriteName = button.name switch
            {
                "btnPlay" => "LobbyStartButton",
                "btnEndless" => "LobbyEndlessButton",
                "btnPvP" => "LobbyPvpButton",
                "btnQuit" => "LobbyQuitButton",
                "btnProjectOpenSource" => "LobbyOpenSourceButton",
                _ => throw new InvalidOperationException("不是大厅常用入口：" + button.name)
            };
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(Art + spriteName + ".png");
            var image = button.targetGraphic as Image;
            Text label = button.transform.Find("label")?.GetComponent<Text>();
            if (sprite == null || image == null || label == null)
            {
                throw new InvalidOperationException("大厅入口缺少插画、图层或文案节点：" + button.name);
            }

            // 插画和圆角已经按入口比例绘制，整张采样能保留两端的细节。
            image.sprite = sprite;
            image.type = Image.Type.Simple;
            image.material = null;
            image.preserveAspect = false;
            image.color = Color.white;
            label.color = Ink;
            label.fontSize = 56;
            // 常态使用同一字号。加载失败等长文案仍能在留字区内缩小显示。
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 28;
            label.resizeTextMaxSize = 56;
            label.rectTransform.anchorMin = new Vector2(.21f, .05f);
            label.rectTransform.anchorMax = new Vector2(.87f, .95f);
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
            if (button.name == "btnEndless")
            {
                label.text = EndlessTitle;
            }
        }

        /// <summary>在编辑态重装现有大厅的五个入口，只在全部装配成功后保存并卸载预制体内容。</summary>
        public static string ApplyToHome()
        {
            const string path = "Assets/GameRes/UI/Panel/HomePanel.prefab";
            GameObject home = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (string name in new[] { "btnPlay", "btnEndless", "btnPvP", "btnQuit", "btnProjectOpenSource" })
                {
                    Button button = home.transform.Find(name)?.GetComponent<Button>();
                    if (button == null)
                    {
                        throw new InvalidOperationException("大厅预制体缺少入口：" + name);
                    }
                    Apply(button);
                }
                ConfigureLanInvitation(home.transform);
                if (PrefabUtility.SaveAsPrefabAsset(home, path) == null)
                {
                    throw new InvalidOperationException("大厅浅色入口装配保存失败");
                }
                return "已装配大厅五个入口";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(home);
            }
        }

        /// <summary>在大厅安全框左侧补齐一个默认隐藏的邀请卡，已有节点直接复用。</summary>
        internal static void ConfigureLanInvitation(Transform root)
        {
            if (root.Find("lanInvite") != null)
            {
                return;
            }
            GameObject invitation = GameAssetBuilder.Rect("lanInvite", root, new Vector2(540, 216), new Vector2(312, 156));
            GameAssetBuilder.Anchor((RectTransform)invitation.transform, new Vector2(0, .5f));
            GameCombatUiBuild.ApplyArt(invitation.AddComponent<Image>(), "BattleHud/BattleInfoPlate", true);
            Text title = GameAssetBuilder.Label("title", invitation.transform, "棋友邀请你下棋",
                new Vector2(474, 78), new Vector2(0, 42), 32);
            title.color = Ink;
            title.resizeTextForBestFit = true;
            title.resizeTextMinSize = 21;
            title.resizeTextMaxSize = 32;
            StyleInvitationButton(GameAssetBuilder.MakeButton("accept", invitation.transform, "接受", new Vector2(201, 66), new Vector2(-113, -44)));
            StyleInvitationButton(GameAssetBuilder.MakeButton("decline", invitation.transform, "暂不接受", new Vector2(201, 66), new Vector2(113, -44)));
            invitation.SetActive(false);
        }

        /// <summary>邀请的两个操作复用对局轻量按钮图，避免引入另一个大厅依赖包。</summary>
        private static void StyleInvitationButton(Button button)
        {
            GameCombatUiBuild.ApplyArt(button.GetComponent<Image>(), "PvpMenu/PvpMatchButton", true);
            button.transform.Find("label").GetComponent<Text>().color = Ink;
        }
    }
}
