#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Automation;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>项目书回归使用真实大厅按钮、书角和剪贴板。隔离 Play 只记录外部跳转目标。</summary>
    public static class ProjectOpenSourceAutomation
    {
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        private static readonly object Owner = new object();
        private static readonly GameBookDragInput DragInput = new GameBookDragInput();
        private static bool _previewLinks;
        private static string _previousClipboard;
        private static string _lastUrl;

        [Serializable]
        private sealed class PreviewArgs
        {
            public bool enabled;
        }

        public static void Register()
        {
            RevRuntimeAutomation.Register("project-book.state", "读取项目书当前页、转场、按钮和链接回执",
                Empty, _ => State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("project-book.drag", "隔离 Play 通过书角发送真实拖曳并按需松手",
                GameBookDragInput.Schema, json =>
                {
                    RequireIsolated();
                    DragInput.Send(RevUI.Get<ProjectOpenSourcePanel>(), json);
                    return State();
                }, owner: Owner);
            RevRuntimeAutomation.Register("project-book.preview-links", "隔离 Play 捕获跳转地址，并在结束时归还原文本剪贴板",
                "{\"type\":\"object\",\"properties\":{\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"enabled\"],\"additionalProperties\":false}",
                json =>
                {
                    RequireIsolated();
                    bool enabled = JsonUtility.FromJson<PreviewArgs>(json).enabled;
                    if (enabled && !_previewLinks)
                    {
                        _previousClipboard = GUIUtility.systemCopyBuffer;
                        _previewLinks = true;
                    }
                    else if (!enabled)
                    {
                        ResetPreview();
                    }
                    return State();
                }, owner: Owner);
        }

        public static void Unregister()
        {
            ResetPreview();
            RevRuntimeAutomation.UnregisterAllOf(Owner);
        }

        internal static bool RecordExternalLaunch(string url)
        {
            if (!_previewLinks)
            {
                return false;
            }
            _lastUrl = url;
            return true;
        }

        private static void ResetPreview()
        {
            DragInput.Reset();
            if (_previewLinks && IsTestCopy(GUIUtility.systemCopyBuffer))
            {
                GUIUtility.systemCopyBuffer = _previousClipboard;
            }
            _previewLinks = false;
            _previousClipboard = null;
            _lastUrl = null;
        }

        private static bool IsTestCopy(string clipboard)
        {
            return clipboard == ProjectOpenSourcePanel.RepositoryUrl || clipboard == ProjectOpenSourcePanel.GroupNumber;
        }

        private static void RequireIsolated()
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                throw new InvalidOperationException("项目书输入验收需要隔离 Play");
            }
        }

        private static Dictionary<string, object> State()
        {
            var book = UnityEngine.Object.FindObjectOfType<ProjectOpenSourcePanel>();
            var home = RevUI.Get<HomePanel>();
            var entry = home == null ? null : home.transform.Find("btnProjectOpenSource");
            string clipboard = _previewLinks ? GUIUtility.systemCopyBuffer : null;
            bool bundleMode = true;
#if UNITY_EDITOR
            bundleMode = RevResBootstrap.UseABInEditor;
#endif
            return new Dictionary<string, object>
            {
                ["book"] = book == null ? null : book.Inspect(),
                ["abMode"] = bundleMode,
                ["previewLinks"] = _previewLinks,
                ["externalUrl"] = _lastUrl,
                ["copiedRepository"] = clipboard == ProjectOpenSourcePanel.RepositoryUrl,
                ["copiedGroup"] = clipboard == ProjectOpenSourcePanel.GroupNumber,
                ["entrySpriteShared"] = entry != null && entry.GetComponent<Image>().sprite == home.transform.Find("btnEndless").GetComponent<Image>().sprite,
                ["entryBelowQuit"] = entry is RectTransform entryRect
                    && entryRect.anchorMax.y < ((RectTransform)home.transform.Find("btnQuit")).anchorMin.y,
                ["entryFontLarger"] = entry != null && entry.GetComponentInChildren<Text>().fontSize > home.transform.Find("btnEndless").GetComponentInChildren<Text>().fontSize
            };
        }
    }

    public sealed partial class ProjectOpenSourcePanel
    {
        internal Dictionary<string, object> Inspect()
        {
            return new Dictionary<string, object>
            {
                ["page"] = _page + 1,
                ["phase"] = _phase.ToString(),
                ["curl"] = _amount,
                ["coverVisible"] = _cover.gameObject.activeSelf,
                ["coverProgress"] = _coverMaterial.GetFloat(Progress),
                ["previousVisible"] = _previous.gameObject.activeSelf,
                ["nextVisible"] = _next.gameObject.activeSelf,
                ["authorText"] = _authorBody.text,
                ["notice"] = _notice.text,
                ["skinsPacked"] = ((Image)_next.targetGraphic).sprite.packed && ((Image)_previous.targetGraphic).sprite.packed,
                ["shader"] = _paperMaterial.shader.name
            };
        }
    }
}
#endif
