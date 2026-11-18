#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Automation;
using RevGame.Services;
using RevGame.UI;
using Revolution;

namespace RevGame.Bootstrap
{
    /// <summary>收藏书开发态取证。点击用框架的通用 UI 接口，拖页使用真实 EventSystem 事件。</summary>
    internal static class YayaMemoryAutomation
    {
        private static readonly object Owner = new object();
        private static readonly GameBookDragInput DragInput = new GameBookDragInput();

        public static void Register()
        {
            RevRuntimeAutomation.Register("memories.state", "读取收藏书通关、已阅、入口与实际落屏状态",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
                _ => State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("memories.test-record-clear", "隔离验收中通过正式通关记账入口记录一章",
                "{\"type\":\"object\",\"properties\":{\"chapter\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6}},\"required\":[\"chapter\"],\"additionalProperties\":false}",
                json =>
                {
                    if (!RevServices.GetRequired<StoryProgress>().IsIsolated)
                    {
                        throw new InvalidOperationException("需要隔离 Play，避免写入玩家进度");
                    }
                    var args = UnityEngine.JsonUtility.FromJson<ClearArgs>(json);
                    RevServices.GetRequired<YayaMemoryProgress>().RecordClear(args.chapter);
                    return State();
                }, owner: Owner);
            RevRuntimeAutomation.Register("memories.drag", "在隔离Play中从书页边缘发出并保持真实拖拽事件，release结束",
                GameBookDragInput.Schema,
                json =>
                {
                    var book = GetBookForDrag();
                    DragInput.Send(book, json);
                    return book.Inspect();
                }, owner: Owner);
        }

        public static void Unregister()
        {
            DragInput.Reset();
            RevRuntimeAutomation.UnregisterAllOf(Owner);
        }

        internal static Dictionary<string, object> State()
        {
            var progress = RevServices.GetRequired<YayaMemoryProgress>();
            var home = RevUI.Get<HomePanel>();
            var book = RevUI.Get<YayaMemoryBookPanel>();
            var speech = home == null ? null : home.transform.Find("MemorySpeech");
            var bubble = speech == null ? null : speech.Find("Background").GetComponent<UnityEngine.UI.Image>().sprite;
            var cottageEntry = home == null ? null : home.transform.Find("btnCottageStory");
            var cottageBadge = cottageEntry == null ? null : cottageEntry.Find("Unread");
            var cottageTip = home == null ? null : home.transform.Find("CottageStoryTip");
            bool bundleMode = true;
#if UNITY_EDITOR
            bundleMode = RevResBootstrap.UseABInEditor;
#endif
            var refs = new int[YayaMemoryProgress.ChapterCount];
            for (int i = 0; i < refs.Length; i++)
            {
                refs[i] = RevResManager.GetRefCount("YayaMemories/Chapter" + (i + 1).ToString("D2") + "/", "Illustration");
            }
            return new Dictionary<string, object>
            {
                ["cleared"] = progress.Cleared, ["unlocked"] = progress.Unlocked, ["read"] = progress.Read, ["unread"] = progress.Unread,
                ["abMode"] = bundleMode, ["book"] = book == null ? null : book.Inspect(),
                ["badge"] = home != null && home.transform.Find("btnMemories/Unread").gameObject.activeInHierarchy,
                ["speech"] = speech != null && speech.gameObject.activeInHierarchy,
                ["speechSprite"] = bubble == null ? "" : bubble.name, ["speechPacked"] = bubble != null && bubble.packed,
                ["cottageUnlocked"] = progress.IsCottageStoryUnlocked,
                ["cottageUnread"] = progress.IsCottageStoryUnread,
                ["cottageEntry"] = cottageEntry != null && cottageEntry.gameObject.activeInHierarchy,
                ["cottageBadge"] = cottageBadge != null && cottageBadge.gameObject.activeInHierarchy,
                ["cottageTip"] = cottageTip != null && cottageTip.gameObject.activeInHierarchy,
                ["refs"] = refs
            };
        }

        [Serializable]
        private sealed class ClearArgs
        {
            public int chapter;
        }

        internal static Dictionary<string, object> Drag(int direction, float amount, bool release)
        {
            var book = GetBookForDrag();
            DragInput.Send(book, direction, amount, release);
            return book.Inspect();
        }

        private static YayaMemoryBookPanel GetBookForDrag()
        {
            var book = RevUI.Get<YayaMemoryBookPanel>();
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || book == null)
            {
                throw new InvalidOperationException("需要已打开收藏书的隔离 Play");
            }
            return book;
        }
    }
}
#endif
