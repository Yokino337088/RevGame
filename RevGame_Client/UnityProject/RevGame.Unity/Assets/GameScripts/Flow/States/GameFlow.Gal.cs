using System;
using RevGame.Core;
using RevGame.UI;
using Revolution;
using Revolution.Gal;
using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System.Collections.Generic;
#endif

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>独立剧情场景只留相机与监听器；剧情面板在场景交割后由 Gal 域装配。</summary>
        public const string GalSceneName = "GalScene";
        internal const string CottageStoryPath = "Gal/Stories/yaya_days_together";
        /// <summary>请求贯穿切场景与首屏交付。取消后先还清资源，再返回大厅并解开过场锁。</summary>
        private RevCancellationTokenSource _galOpening;
        /// <summary>面板持有会话。流程只记归属，玩家自行关闭后剧情快照可立即回收。</summary>
        private WeakReference<RevGalSession> _galSession;

        /// <summary>从开始加载起占用同一套玩法门禁。玩家关闭面板后会话释放，门禁自动恢复。</summary>
        public bool IsGalPlaying => _galOpening != null || GetOwnedGalPanel() != null;

        private bool CanOpenGal => !_disposed && (Screen == GameScreen.Home || Screen == GameScreen.LevelSelect)
            && !IsTutorialActive && !IsGameplayBlocked && !RevUI.IsOpen<RevGalPanel>();

        /// <summary>从大厅或选关页进入独立场景；首屏媒体就绪且遮挡关闭后才返回 true。</summary>
        public async RevTask<bool> OpenGalAsync(string storyPath, RevGalOptions options = null, string slot = null)
        {
            if (!CanOpenGal)
            {
                LastError = "请在大厅或选关空闲时打开 Gal 剧情";
                return false;
            }
            var request = new RevCancellationTokenSource();
            _galOpening = request;
            IsTransitioning = true;
            LastError = string.Empty;
            try
            {
                _loading = await RevUI.OpenAsync<LoadingPanel>();
                ThrowIfDisposed();
                RevScope.Detach(_loading);
                _loading.ResetCover();
                Report(0.02f, "正在翻开故事…");
                await RevTask.Yield();
                ThrowIfDisposed();
                request.Token.ThrowIfCancelled();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                CheckDevelopmentFailure("before-scene");
#endif
                // 先卸载大厅及它的场景音频，再打开剧情面板，避免两边音乐同时播放。
                await RevScene.LoadAsync(GalSceneName, p => Report(0.1f + 0.55f * p, "正在前往故事场景…"));
                ThrowIfDisposed();
                if (RevScene.CurrentName != GalSceneName || RevScene.State != RevSceneLoadState.Done)
                {
                    throw new InvalidOperationException("未能进入剧情场景");
                }
                _activePresentation = null;
                Change(GameScreen.Gal);
                request.Token.ThrowIfCancelled();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                CheckDevelopmentFailure("after-scene");
#endif
                Report(0.72f, "正在准备剧情画面…");
                using var planScope = RevResManager.OpenScope();
                RevGalDefinition portraitPlan = null;
                if (string.Equals(storyPath, CottageStoryPath, StringComparison.Ordinal))
                {
                    // 预读与框架会话共用同一份 SO 请求。计划只看相邻对白，存档和实际推进仍由 Gal 会话决定。
                    // 本次读剧情只为预生成显示计划，临时资源由 using 作用域立即归还。
                    var storyHandle = RevResManager.LoadAsync("Gal/Stories", "yaya_days_together", typeof(RevGalStory), null, RevResGroup.UI, lifetime: RevResLifetime.Manual);
                    planScope.Track(storyHandle);
                    while (storyHandle.IsLoading)
                    {
                        await RevTask.Yield();
                        ThrowIfDisposed();
                        request.Token.ThrowIfCancelled();
                    }
                    portraitPlan = storyHandle.Get<RevGalStory>()?.Definition;
                }
                var panel = await RevGal.OpenAsync(storyPath, options, slot, request.Token);
                _galSession = new WeakReference<RevGalSession>(panel.Data);
                var playback = GalStoryPlayback.Attach(panel, () =>
                {
                    if (GetOwnedGalPanel() == panel)
                    {
                        ReturnHome();
                    }
                }, portraitPlan);
                float deadline = Time.realtimeSinceStartup + 20f;
                while (!playback.StageReady)
                {
                    ThrowIfDisposed();
                    request.Token.ThrowIfCancelled();
                    if (GetOwnedGalPanel() != panel || Time.realtimeSinceStartup >= deadline)
                    {
                        throw new InvalidOperationException("剧情首屏画面没有完成加载");
                    }
                    await RevTask.Yield();
                }
                Report(1f, "故事已就绪");
                await _loading.FadeOutAsync();
                ThrowIfDisposed();
                request.Token.ThrowIfCancelled();
                RevUI.Close<LoadingPanel>();
                _loading = null;
                playback.BeginVisiblePlayback();
                LastError = string.Empty;
                return true;
            }
            catch (Exception error) when ((error is RevOperationCanceledException || error is OperationCanceledException)
                && (_disposed || request.Token.IsCancelled))
            {
                CloseOwnedGalPanel();
                if (!_disposed)
                {
                    if (RevScene.CurrentName == GalSceneName)
                    {
                        await SwitchSceneAsync(GameScreen.Home);
                    }
                    else
                    {
                        RevUI.Close<LoadingPanel>();
                        _loading = null;
                    }
                }
                return false;
            }
            catch (Exception error)
            {
                CloseOwnedGalPanel();
                if (!_disposed)
                {
                    LastError = "剧情打开失败：" + error.Message;
                    RevLog.Exception(error, "剧情打开失败", "RevGame");
                    if (_loading != null)
                    {
                        _loading.SetData(new LoadingPanel.ProgressData(LoadProgress, LoadStep, LastError));
                    }
                }
                return false;
            }
            finally
            {
                if (ReferenceEquals(_galOpening, request))
                {
                    _galOpening = null;
                }
                IsTransitioning = false;
            }
        }

        /// <summary>取消未交付的请求；交付后沿用返回大厅流程，卸载 GalScene 并重新装配大厅。</summary>
        public bool CloseGal()
        {
            if (_disposed)
            {
                return false;
            }
            if (_galOpening != null)
            {
                _galOpening.Cancel();
                return true;
            }
            return Screen == GameScreen.Gal && !IsTransitioning && ReturnHome();
        }

        /// <summary>只关闭本流程拥有的会话。另一个宿主换开的面板及其资源不归这次请求。</summary>
        private void CloseOwnedGalPanel()
        {
            var panel = GetOwnedGalPanel();
            _galSession = null;
            if (panel != null)
            {
                RevUI.Close(panel);
            }
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
        /// <summary>每次预览独占内存档案，支持本次快存快读，关闭后和玩家档案互不影响。</summary>
        internal static RevGalOptions CreateGalPreviewOptions(bool animate)
        {
            var options = new RevGalOptions { UseDefaultStorage = false, Storage = new GalPreviewStorage() };
            if (!animate)
            {
                options.CharactersPerSecond = 0f;
            }
            return options;
        }

        /// <summary>与玩家共用独立场景流程，返回值表示接单，首屏需另查状态。</summary>
        internal bool TryOpenGalForTest(string path, bool animate)
        {
            if (!CanOpenGal || !RevGalValidation.IsLogicalPath(path))
            {
                return false;
            }
            OpenGalAsync(path, CreateGalPreviewOptions(animate)).Forget();
            return true;
        }

        /// <summary>推进当前对白，支持核对节点 ID，防止迟到的测试请求推进下一句。</summary>
        internal bool TryAdvanceGalForTest(string expectedNode)
        {
            var playback = GetOwnedGalPanel()?.GetComponentInChildren<GalStoryPlayback>(true);
            return !IsTransitioning && playback != null && playback.AdvanceForTest(expectedNode);
        }

        /// <summary>自动化走正式流程入口，关闭默认存储以免污染玩家剧情档案。</summary>
        private void RegisterGalAutomation()
        {
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("game.gal.open", "从 RevGame 流程请求打开 Gal 剧情",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"animate\":{\"type\":\"boolean\"}},\"required\":[\"path\"],\"additionalProperties\":false}",
                json =>
                {
                    var args = JsonUtility.FromJson<GalOpenArgs>(json);
                    bool accepted = TryOpenGalForTest(args.path, args.animate);
                    var state = GalAutomationState();
                    state["accepted"] = accepted;
                    return state;
                }, owner: this);
            RevRuntimeAutomation.Register("game.gal.state", "只读 Gal 场景、节点、实际字幕和已落屏的舞台素材", empty,
                _ => GalAutomationState(), readOnly: true, owner: this);
            RevRuntimeAutomation.Register("game.gal.close", "关闭 RevGame 管理的 Gal 剧情", empty,
                _ => new Dictionary<string, object> { ["closed"] = CloseGal() }, owner: this);
        }

        internal Dictionary<string, object> GalAutomationState()
        {
            var panel = GetOwnedGalPanel();
            var playback = panel == null ? null : panel.GetComponentInChildren<GalStoryPlayback>(true);
            var frame = panel?.Data?.Frame;
            return new Dictionary<string, object>
            {
                ["screen"] = Screen.ToString(),
                ["scene"] = RevScene.CurrentName,
                ["transitioning"] = IsTransitioning,
                ["pending"] = _galOpening != null,
                ["playing"] = IsGalPlaying,
                ["blocked"] = IsGameplayBlocked,
                ["galDomain"] = RevScope.IsActive(GameDomain.Gal),
                ["lobbyDomain"] = RevScope.IsActive(GameDomain.Lobby),
                ["stageReady"] = playback != null && playback.StageReady,
                ["frameReady"] = !IsTransitioning && playback != null && playback.FrameReadyForTest,
                ["subtitleSoundStarted"] = playback != null && playback.SubtitleSoundStarted,
                ["nodeId"] = frame?.Id,
                ["nodeKind"] = frame?.Kind.ToString(),
                ["nodeToken"] = frame?.Token,
                ["visibleDialogue"] = panel?.VisibleDialogue,
                ["visibleSpeaker"] = playback?.VisibleSpeakerForTest,
                ["textComplete"] = panel != null && panel.TextComplete,
                ["backgroundSprite"] = playback?.BackgroundSpriteForTest,
                ["leftSprite"] = playback?.LeftSpriteForTest,
                ["centerSprite"] = playback?.CenterSpriteForTest,
                ["rightSprite"] = playback?.RightSpriteForTest,
                ["settingsOpen"] = panel != null && panel.SettingsOpen,
                ["historyOpen"] = panel != null && panel.HistoryOpen,
                ["error"] = LastError
            };
        }

        [Serializable]
        private sealed class GalOpenArgs
        {
            public string path;
            public bool animate;
        }

        private sealed class GalPreviewStorage : IRevGalStorage
        {
            private readonly Dictionary<(string Story, string Slot), RevGalSave> _slots = new Dictionary<(string, string), RevGalSave>();
            private readonly Dictionary<string, RevGalProgress> _progress = new Dictionary<string, RevGalProgress>();

            public RevGalSave ReadSlot(string story, string slot)
            {
                return _slots.TryGetValue((story, slot), out var save) ? save : null;
            }

            public void WriteSlot(string story, string slot, RevGalSave save)
            {
                _slots[(story, slot)] = save;
            }

            public RevGalProgress ReadProgress(string story)
            {
                return _progress.TryGetValue(story, out var progress) ? progress : null;
            }

            public void WriteProgress(string story, RevGalProgress progress)
            {
                _progress[story] = progress;
            }
        }
#endif
    }
}
