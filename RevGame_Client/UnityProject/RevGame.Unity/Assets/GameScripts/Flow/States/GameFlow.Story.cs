using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Services;
using RevGame.UI;
using Revolution;

namespace RevGame.Flow
{
    /// <summary>唯一流程控制者的剧情部分。播放立即锁住底层操作，收尾才恢复；不是第二台场景状态机。</summary>
    public sealed partial class GameFlow
    {
        /// <summary>全局流程自己的资源引用；只在重试或Dispose释放，不随章节重复加载。</summary>
        private RevResHandle _storyHandle;
        private StoryBook _storyBook;
        /// <summary>当前播放篇章及从0开始的行号；未播放时篇章为空。</summary>
        private StoryBook.Episode _episode;
        private int _storyLine;
        /// <summary>每次播放/取消递增。异步加载回调带旧版本时不得再显示旧故事。</summary>
        private int _storyVersion;
        /// <summary>Busy从等待加载起就阻塞底层；Visible只在UI实际打开后允许翻页。</summary>
        private bool _storyBusy;
        private bool _storyVisible;
        /// <summary>只在玩家读到末句并点击后执行一次，取消播放绝不能把它当正常完成。</summary>
        private Action _storyCompleted;
        /// <summary>本次战役已展示的章节；重进战役重置，普通回合不会重复弹故事。</summary>
        private int _presentedChapter;
        /// <summary>首次第一章的实操引导开关；始终在正式Battle域和GameScene内。</summary>
        public bool IsTutorialActive
        {
            get; private set;
        }
        /// <summary>开发重播不写首次教学完成标记；返回大厅或正常收尾清除。</summary>
        private bool _tutorialReplay;
        public bool IsStoryPlaying
        {
            get
            {
                return _storyBusy;
            }
        }
        public string StoryId
        {
            get
            {
                return _episode == null ? string.Empty : _episode.Id;
            }
        }
        public int StoryLine
        {
            get
            {
                return _storyLine;
            }
        }
        public StoryBook StoryContent
        {
            get
            {
                return _storyBook;
            }
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>开发态重播开场：复用正式剧情锁输入/翻页流程，不写首次进度，也不串联教学。true只表示接受播放请求。</summary>
        public bool ReplayOpeningStory()
        {
            if (_disposed || IsGameplayBlocked || IsTutorialActive || _storyBook == null || Screen == GameScreen.Exit)
            {
                LastError = "请等待资源、过场、剧情或教学结束后再播放初始剧情";
                return false;
            }
            LastError = string.Empty;
            // 不传完成回调：读完回到当前界面，不能借重播把玩家存档标成已完成。
            PlayStory("opening", null);
            return true;
        }
        /// <summary>GM共用正式教学入口。true仅表示接受；重播替换当前战役，但不重置或完成玩家教学存档。</summary>
        public bool ReplayTutorial()
        {
            if (_disposed || IsGameplayBlocked || IsTutorialActive || Screen == GameScreen.Exit)
            {
                LastError = "请等待当前过场/剧情/教学结束，或先返回大厅再重播";
                return false;
            }
            LastError = string.Empty;
            _tutorialReplay = true;
            _presentedChapter = 0;

            if (Screen == GameScreen.Home)
            {
                BeginTutorialIntroduction();
            }
            else
            {
                // 先通过唯一场景交接器退出旧战斗域，不能在Battle状态直接再开一份教学。
                IsTransitioning = true;
                ReplayTutorialFromHomeAsync();
            }
            return true;
        }
        private async void ReplayTutorialFromHomeAsync()
        {
            await SwitchSceneAsync(GameScreen.Home);

            if (_disposed || !string.IsNullOrEmpty(LastError))
            {
                _tutorialReplay = false;
                return;
            }
            BeginTutorialIntroduction();
        }
        /// <summary>只允许隔离测试存档预览文案；普通玩家不能借此改章节或标记完成。</summary>
        public bool PreviewStoryForTest(string id)
        {
            if (_disposed || IsTransitioning || _storyBusy || !RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                return false;
            }
            PlayStory(id, null);
            return true;
        }
#endif

        /// <summary>配置走Revolution资源句柄，全局流程持有且成对释放；不随战斗域卸载。</summary>
        private async RevTask LoadStoryAsync()
        {
            if (_storyHandle != null)
            {
                RevResManager.Release("Config/Story", "StoryBook");
                _storyHandle = null;
            }
            _storyHandle = RevResManager.LoadAsync("Config/Story", "StoryBook", typeof(StoryBook), null, RevResGroup.Scene);
            while (_storyHandle.IsLoading)
            {
                await RevTask.Yield();
                ThrowIfDisposed();
            }
            _storyBook = _storyHandle.Content as StoryBook;

            if (_storyBook == null)
            {
                throw new InvalidOperationException("剧情资产加载失败：" + _storyHandle.ErrorReason);
            }
            _storyBook.Validate();
            await RevServices.GetRequired<View.GameSpriteAtlasService>().PreloadAsync(new[] { "StoryGuide", "UIMenus" });
            RevSound.Preload("Place");
        }
        /// <summary>启动顺序：开场→教学引子→GameScene真实第一章。未完成教学的老存档也会补教学。</summary>
        private void BeginIntroduction()
        {
            var progress = RevServices.GetRequired<StoryProgress>();

            if (!progress.OpeningComplete)
            {
                PlayStory("opening", () => { progress.CompleteOpening(); BeginTutorialIntroduction(); });
            }
            else

                if (!progress.TutorialComplete)
                {
                    BeginTutorialIntroduction();
                }
        }

        private void BeginTutorialIntroduction()
        {
            PlayStory("tutorial", () =>
            {
                IsTutorialActive = true;
                _chapter = 1;
                IsTransitioning = true;
                SwitchFromButtonAsync(GameScreen.Battle);
            });
        }
        /// <summary>准备事件同步锁输入和计时，异步等背景/过场交付后展示；同章普通回合不重播。</summary>
        private void OnStoryPrepare(int round)
        {
            var battle = RevServices.GetRequired<IBattleService>();

            if (IsTutorialActive || round != 1 || battle.Chapter == _presentedChapter)
            {
                return;
            }
            _presentedChapter = battle.Chapter;
            PlayStory("chapter" + battle.Chapter, null);
        }
        private async void PlayStory(string id, Action completed)
        {
            int version = ++_storyVersion;
            _storyBusy = true;
            _storyVisible = false;
            _storyLine = 0;
            _storyCompleted = completed;
            try
            {
                _episode = _storyBook.Find(id);
                while (!_disposed && version == _storyVersion && (IsTransitioning || _loading != null ||
                    (Screen == GameScreen.Battle && _activePresentation != null && !_activePresentation.IsChapterBackgroundReady)))
                {
                    await RevTask.Yield();
                }
                if (_disposed || version != _storyVersion)
                {
                    return;
                }
                var panel = await RevUI.OpenAsync<StoryPanel, StoryPanel.Frame>(StoryFrame());

                if (_disposed || version != _storyVersion)
                {
                    // 只关闭本次迟到的面板；若缓存实例已被新剧情接管，不能误关它。
                    if (panel != null && panel.Data != null && panel.Data.Version == version)
                    {
                        RevUI.Close(panel);
                    }
                    return;
                }
                if (panel == null)
                {
                    throw new InvalidOperationException("剧情面板加载失败");
                }
                _storyVisible = true;
            }
            catch (Exception error)
            {
                if (_disposed || version != _storyVersion)
                {
                    return;
                }
                CancelStory();
                LastError = error.Message;
                RevLog.Exception(error, "剧情未完成，保留首次进度以便下次重试", "RevGame");
                ShowStoryFailureAsync(error.Message);
            }
        }
        /// <summary>资源异常必须给玩家返回入口；不能只打印日志后假装首次教程已完成。</summary>
        private async void ShowStoryFailureAsync(string reason)
        {
            try
            {
                var panel = await RevUI.OpenAsync<LoadingPanel>();

                if (_disposed)
                {
                    return;
                }
                _loading = panel;
                RevScope.Detach(panel);
                panel.ResetCover();
                LastError = "剧情暂时无法展开：" + reason;
                panel.SetData(new LoadingPanel.ProgressData(0f, "剧情加载失败", LastError));
            }
            catch (Exception error) { RevLog.Exception(error, "剧情错误提示打开失败", "RevGame"); }
        }
        private StoryPanel.Frame StoryFrame()
        {
            return new StoryPanel.Frame { Book = _storyBook, Episode = _episode, Index = _storyLine, Version = _storyVersion };
        }
        /// <summary>UI和自动化共用入口。一次点击直接下一句，末句之后关闭并执行一次完成回调。</summary>
        public bool AdvanceStory()
        {
            if (!_storyVisible || _episode == null || _disposed)
            {
                return false;
            }
            if (++_storyLine < _episode.Lines.Length)
            {
                RevUI.Get<StoryPanel>().SetData(StoryFrame());
                return true;
            }
            Action completed = _storyCompleted;
            CancelStory();
            completed?.Invoke();
            return true;
        }
        private void CancelStory()
        {
            ++_storyVersion;
            _storyVisible = false;
            _storyBusy = false;
            _storyCompleted = null;
            _episode = null;
            RevUI.Close<StoryPanel>();
        }
        /// <summary>只有教程Logic实操完成才可写进度；不让普通按钮/GM直接跳过教学并伪造完成。</summary>
        public bool CompleteTutorial()
        {
            var lesson = RevLogic.Get<TutorialLogic>();

            if (IsGameplayBlocked || !IsTutorialActive || Screen != GameScreen.Battle || lesson == null || lesson.Step != TutorialStep.Ready)
            {
                return false;
            }
            if (!RevServices.GetRequired<IBattleService>().StartBattle())
            {
                return false;
            }
            if (!_tutorialReplay)
            {
                RevServices.GetRequired<StoryProgress>().CompleteTutorial();
            }
            _tutorialReplay = false;
            IsTutorialActive = false;
            RevUI.Close<TutorialPanel>();
            _presentedChapter = 1;
            PlayStory("chapter1", null);
            return true;
        }
    }
}
