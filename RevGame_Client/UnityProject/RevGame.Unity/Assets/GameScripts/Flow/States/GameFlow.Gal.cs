using System;
using RevGame.Core;
using Revolution;
using Revolution.Gal;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
using UnityEngine;
#endif

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>一个打开请求占用一次取消源。流程离开时取消，晚到的加载不能占用新的游戏域。</summary>
        private RevCancellationTokenSource _galOpening;
        /// <summary>面板持有会话。流程只记归属，玩家自行关闭后剧情快照可立即回收。</summary>
        private WeakReference<RevGalSession> _galSession;

        /// <summary>从开始加载起占用同一套玩法门禁。玩家关闭面板后会话释放，门禁自动恢复。</summary>
        public bool IsGalPlaying => _galOpening != null || GetOwnedGalPanel() != null;

        /// <summary>从稳定的大厅或选关页打开独立 Gal 剧情。调用方按需传自己的档案和信号处理。</summary>
        public async RevTask<bool> OpenGalAsync(string storyPath, RevGalOptions options = null, string slot = null)
        {
            if (_disposed || (Screen != GameScreen.Home && Screen != GameScreen.LevelSelect) || IsTutorialActive
                || IsGameplayBlocked || RevUI.IsOpen<RevGalPanel>())
            {
                LastError = "请在大厅或选关空闲时打开 Gal 剧情";
                return false;
            }
            var request = new RevCancellationTokenSource();
            _galOpening = request;
            LastError = string.Empty;
            try
            {
                var panel = await RevGal.OpenAsync(storyPath, options, slot, request.Token);
                _galSession = new WeakReference<RevGalSession>(panel.Data);
                LastError = string.Empty;
                return true;
            }
            catch (RevOperationCanceledException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception error)
            {
                LastError = "Gal 剧情打开失败：" + error.Message;
                RevLog.Exception(error, "Gal 剧情打开失败", "RevGame");
                return false;
            }
            finally
            {
                if (ReferenceEquals(_galOpening, request))
                {
                    _galOpening = null;
                }
            }
        }

        /// <summary>仅关闭本流程持有的会话或加载请求。另一个宿主在同一面板打开的新会话归它管理。</summary>
        public bool CloseGal()
        {
            var request = _galOpening;
            var panel = GetOwnedGalPanel();
            var session = panel != null ? panel.Data : null;
            _galOpening = null;
            _galSession = null;
            request?.Cancel();
            bool closed = panel != null && session != null && ReferenceEquals(panel.Data, session) && RevUI.Close(panel);
            return closed || request != null;
        }

        private RevGalPanel GetOwnedGalPanel()
        {
            if (_galSession == null || !_galSession.TryGetTarget(out var session) || session.IsDisposed)
            {
                return null;
            }
            var panel = RevUI.Get<RevGalPanel>();
            return panel != null && ReferenceEquals(panel.Data, session) ? panel : null;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>自动化走正式流程入口，关闭默认存储以免污染玩家剧情档案。</summary>
        private void RegisterGalAutomation()
        {
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.gal.open", "从 RevGame 流程请求打开 Gal 剧情",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}",
                json =>
                {
                    var task = OpenGalAsync(JsonUtility.FromJson<GalOpenArgs>(json).path,
                        new RevGalOptions { UseDefaultStorage = false, CharactersPerSecond = 0f });
                    task.Forget();
                    return GalAutomationState();
                }, owner: this);
            RevRuntimeAutomation.Register("game.gal.state", "读取 RevGame 对 Gal 的流程锁和拥有的会话", empty,
                _ => GalAutomationState(), readOnly: true, owner: this);
            RevRuntimeAutomation.Register("game.gal.close", "关闭 RevGame 管理的 Gal 剧情", empty,
                _ => new Dictionary<string, object> { ["closed"] = CloseGal() }, owner: this);
        }

        private object GalAutomationState()
        {
            return new Dictionary<string, object>
            {
                ["screen"] = Screen.ToString(),
                ["pending"] = _galOpening != null,
                ["playing"] = IsGalPlaying,
                ["blocked"] = IsGameplayBlocked,
                ["error"] = LastError
            };
        }

        [Serializable]
        private sealed class GalOpenArgs
        {
            public string path;
        }
#endif
    }
}
