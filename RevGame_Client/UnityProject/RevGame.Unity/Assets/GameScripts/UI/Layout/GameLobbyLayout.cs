using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using RevGame.View;
#endif

namespace RevGame.UI
{
    /// <summary>大厅的标题、菜单、提示和场景立绘使用同一个安全设计框。</summary>
    internal static class GameLobbyLayout
    {
        /// <summary>预留底部入口和呼吸光的位置，让立绘完整落在左栏。</summary>
        private static readonly Rect PortraitZone = Rect.MinMaxRect(.02f, .14f, .40f, .975f);

        /// <summary>角色和主菜单之间的提示起点，使用设计框的归一化坐标。</summary>
        internal const float HintLeft = .418f;

        /// <summary>将左栏换算成相机视口坐标，让场景立绘与界面控件共享边界。</summary>
        internal static Rect PortraitViewport(Vector2 pixels, Rect safeArea)
        {
            Rect frame = GameBattleLayout.FitBoardViewport(pixels, safeArea);
            return new Rect(frame.position + Vector2.Scale(frame.size, PortraitZone.position),
                Vector2.Scale(frame.size, PortraitZone.size));
        }

        /// <summary>绑定时安排大厅标题和左栏入口，兼容尚未重打资源包的预制体。</summary>
        internal static void ConfigureContent(Transform panel)
        {
            if (panel.Find("title") is RectTransform titleRect && titleRect.TryGetComponent(out Text title))
            {
                titleRect.anchorMin = new Vector2(.58f, .718f);
                titleRect.anchorMax = new Vector2(.97f, .886f);
                titleRect.offsetMin = titleRect.offsetMax = Vector2.zero;

                // 固定单行标题。动态字体按画布缩放取整，旧截断设置会在行高超过盒子时整行丢弃。
                title.resizeTextForBestFit = false;
                title.horizontalOverflow = HorizontalWrapMode.Overflow;
                title.verticalOverflow = VerticalWrapMode.Overflow;
            }

            PlaceBottomEntry(panel.Find("btnMemories") as RectTransform, 32f, new Vector2(292f, 72f));
            PlaceBottomEntry(panel.Find("btnCottageStory") as RectTransform, 344f, new Vector2(260f, 72f));
            PlaceHint(panel.Find("MemorySpeech") as RectTransform, .62f);
            PlaceHint(panel.Find("CottageStoryTip") as RectTransform, .19f);
            if (panel.Find("lanInvite") is RectTransform invite)
            {
                // 邀请的两个按钮和标题按原比例缩放到中栏，保留原有点击区域及监听。
                invite.anchorMin = invite.anchorMax = new Vector2(.524f, .49f);
                invite.anchoredPosition = Vector2.zero;
                invite.localScale = new Vector3(.78f, .78f, 1f);
            }
        }

        /// <summary>沿底边排列入口，为网页复用出来的按钮保留同一行的后续位置。</summary>
        private static void PlaceBottomEntry(RectTransform entry, float x, Vector2 size)
        {
            if (entry == null)
            {
                return;
            }
            entry.anchorMin = entry.anchorMax = entry.pivot = Vector2.zero;
            entry.anchoredPosition = new Vector2(x, 22f);
            entry.sizeDelta = size;
        }

        /// <summary>将提示限制在中栏，让显示和淡出都避开立绘与右栏操作。</summary>
        private static void PlaceHint(RectTransform hint, float y)
        {
            if (hint == null)
            {
                return;
            }
            hint.anchorMin = hint.anchorMax = new Vector2(HintLeft, y);
            hint.pivot = new Vector2(0f, .5f);
            hint.anchoredPosition = Vector2.zero;
            hint.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 414f);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>读排版器生成的标题字形和实际落屏边界，检验可见性与立绘遮挡。</summary>
        internal static object Inspect(Transform panel, LobbySceneView lobby)
        {
            var corners = new Vector3[4];
            var pixels = new Vector2(UnityEngine.Device.Screen.width, UnityEngine.Device.Screen.height);
            Rect normalized = GameBattleLayout.SafeViewport();
            var safe = new Rect(Vector2.Scale(normalized.position, pixels), Vector2.Scale(normalized.size, pixels));
            Rect contentFrame = GameMobileLayout.ScreenBounds((RectTransform)panel, corners);
            Rect portrait = default;
            bool hasPortrait = lobby != null && lobby.TryGetYayaScreenBounds(out portrait);
            var clipped = new List<string>();
            var outsideFrame = new List<string>();
            var overlapping = new List<string>();
            int checkedControls = 0;
            foreach (Transform child in panel)
            {
                if (!(child is RectTransform control) || !control.gameObject.activeInHierarchy)
                {
                    continue;
                }
                checkedControls++;
                Rect bounds = GameMobileLayout.ScreenBounds(control, corners);
                if (!GameMobileLayout.Contains(safe, bounds))
                {
                    clipped.Add(child.name);
                }
                if (!GameMobileLayout.Contains(contentFrame, bounds))
                {
                    outsideFrame.Add(child.name);
                }
                if (hasPortrait && bounds.Overlaps(portrait))
                {
                    overlapping.Add(child.name);
                }
            }
            var title = panel.Find("title")?.GetComponent<Text>();
            int glyphs = title == null ? 0 : title.cachedTextGenerator.characterCountVisible;
            return new Dictionary<string, object>
            {
                ["width"] = pixels.x,
                ["height"] = pixels.y,
                ["title"] = title == null ? string.Empty : title.text,
                ["titleGlyphs"] = glyphs,
                ["titleLines"] = title == null ? 0 : title.cachedTextGenerator.lineCount,
                ["titleDrawn"] = title != null && title.isActiveAndEnabled && !title.canvasRenderer.cull &&
                    glyphs == title.text.Length && title.cachedTextGenerator.vertexCount >= title.text.Length * 4,
                ["portraitInSafeArea"] = hasPortrait && GameMobileLayout.Contains(safe, portrait),
                ["controlsChecked"] = checkedControls,
                ["controlsOutsideSafeArea"] = clipped,
                ["controlsOutsideContentFrame"] = outsideFrame,
                ["controlsOverlappingPortrait"] = overlapping
            };
        }
#endif
    }
}
