#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Revolution.Gal
{
    /// <summary>开发态播放接线。测试档与玩家档分开，所有界面输入走实际 RevUI 按钮。</summary>
    internal static class RevGalAutomation
    {
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        private static readonly object Owner = new object();
        private static MemoryStorage _storage;
        private static int _request;
        private static bool _pending;
        private static string _error;

        [Serializable]
        private sealed class OpenArgs
        {
            public string path;
            public string slot;
            public bool instant;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            _storage = new MemoryStorage();
            _request = 0;
            _pending = false;
            _error = null;
            RevRuntimeAutomation.Register("gal.state", "读取默认 Gal Panel、剧情帧、已读、选项及变量",
                Empty, _ => Describe(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("gal.open", "从 Revolution 逻辑路径打开剧情，开发验收使用内存存档",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"slot\":{\"type\":\"string\"},\"instant\":{\"type\":\"boolean\"}},\"required\":[\"path\"],\"additionalProperties\":false}",
                json =>
                {
                    var args = JsonUtility.FromJson<OpenArgs>(json);
                    _pending = true;
                    _error = null;
                    Open(args, ++_request).Forget();
                    return new Dictionary<string, object> { ["accepted"] = true };
                }, owner: Owner);
            RevRuntimeAutomation.Register("gal.close", "关闭默认 Gal Panel 并取消待打开请求", Empty, _ =>
            {
                ++_request;
                _pending = false;
                _error = null;
                return new Dictionary<string, object> { ["closed"] = RevGal.Close() };
            }, owner: Owner);
        }

        private static async RevTask Open(OpenArgs args, int ticket)
        {
            try
            {
                await RevGal.OpenAsync(args.path, new RevGalOptions
                {
                    Storage = _storage, CharactersPerSecond = args.instant ? 0f : 42f,
                    HandleSignal = (_, signal) => RevTask.Completed
                }, args.slot);
            }
            catch (Exception error)
            {
                if (ticket == _request)
                {
                    _error = error.Message;
                }
            }
            finally
            {
                if (ticket == _request)
                {
                    _pending = false;
                }
            }
        }

        private static Dictionary<string, object> Describe()
        {
            var panel = RevUI.Get<RevGalPanel>();
            var story = panel?.Data;
            var frame = story?.Frame;
            return new Dictionary<string, object>
            {
                ["open"] = panel != null && story != null && !story.IsDisposed,
                ["pending"] = _pending, ["error"] = _error,
                ["storyId"] = story?.StoryId, ["node"] = frame?.Id, ["kind"] = frame?.Kind.ToString(),
                ["text"] = panel?.VisibleDialogue, ["revealed"] = panel?.TextComplete ?? false,
                ["backgroundVisible"] = panel?.BackgroundVisible ?? false,
                ["historyOpen"] = panel?.HistoryOpen ?? false,
                ["settingsOpen"] = panel?.SettingsOpen ?? false,
                ["mode"] = panel?.PlaybackMode,
                ["theme"] = panel?.ThemeName,
                ["choices"] = frame?.Choices.Select(choice => new Dictionary<string, object>
                { ["id"] = choice.Id, ["text"] = choice.Text, ["enabled"] = choice.Enabled }).ToArray(),
                ["variables"] = story?.Variables().ToDictionary(x => x.Id, x => (object)x.Value),
                ["progress"] = story?.Progress(), ["history"] = story?.History()
            };
        }

        private sealed class MemoryStorage : IRevGalStorage
        {
            private readonly Dictionary<string, RevGalSave> _slots = new Dictionary<string, RevGalSave>();
            private readonly Dictionary<string, RevGalProgress> _progress = new Dictionary<string, RevGalProgress>();

            public RevGalSave ReadSlot(string story, string slot) => _slots.TryGetValue(story + ":" + slot, out var save) ? save : null;
            public void WriteSlot(string story, string slot, RevGalSave save) => _slots[story + ":" + slot] = save;
            public RevGalProgress ReadProgress(string story) => _progress.TryGetValue(story, out var progress) ? progress : null;
            public void WriteProgress(string story, RevGalProgress progress) => _progress[story] = progress;
        }
    }
}
#endif
