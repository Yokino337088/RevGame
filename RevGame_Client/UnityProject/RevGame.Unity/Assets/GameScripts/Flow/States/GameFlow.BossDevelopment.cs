// 六章Boss直达属于开发流程入口，不直接改场景或手牌；复用GameFlow的场景交割与BattleService真实配置。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Animation;
using RevGame.Core;
using RevGame.Data;
using RevGame.Services;
using Revolution;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>跨“旧战斗→首页→目标Boss”的整次请求互斥；两个内部过场之间也不能被另一条GM抢入。</summary>
        private bool _bossDevelopmentEntering;
        /// <summary>只在下一次Battle域交割消费；普通开局、失败和退出都清掉，不能污染后续正式战役。</summary>
        private bool _bossDevelopmentStartPending;
        /// <summary>整次直达请求是否仍在运行，供只读GM等待；不表示Boss已经开战。</summary>
        public bool IsBossDevelopmentEntering { get { return _bossDevelopmentEntering; } }

        /// <summary>一次调用接受目标章请求。允许跳过剧情，但只取消展示，不伪造首次剧情/教学完成。</summary>
        public bool EnterBossChapterForDevelopment(int chapter)
        {
            if (_disposed || IsTransitioning || _bossDevelopmentEntering || IsAudioSettingsVisible || _loading != null)
            { LastError = "请等待当前过场完成或关闭设置后再进入Boss关卡"; return false; }
            if (!_startupComplete || !GameConfig.IsLoaded || Screen == GameScreen.Exit)
            { LastError = "请先等待游戏完成启动"; return false; }
            if (chapter < 1 || chapter > Consts.ChapterCount)
            { LastError = "Boss章节只允许1～" + Consts.ChapterCount; return false; }
            var runner = RevServices.Get<GameAutomationRunner>();
            if (runner != null && runner.State == "running")
            { LastError = "自动化流程仍在运行，请先停止它"; return false; }
            if (LevelCatalog.GetRound(chapter, Balance.BossRoundIndex)?.IsBoss != true)
            { LastError = "该章没有有效Boss回合配置"; return false; }
            CancelStory();
            CancelBossVictory();
            CancelBossIntro();
            FinalBossStoryShown = false;
            IsTutorialActive = false;
            _tutorialReplay = false;
            _bossDevelopmentEntering = true;
            _bossDevelopmentStartPending = false;
            LastError = string.Empty;
            IsTransitioning = true;
            EnterBossChapterAsync(chapter);
            return true;
        }

        /// <summary>当前已是Battle时先正常退场，确保同一个FSM状态也会重新OnEnter，不直接重初始化框架。</summary>
        private async void EnterBossChapterAsync(int chapter)
        {
            try
            {
                if (Screen == GameScreen.Battle)
                {
                    await SwitchSceneAsync(GameScreen.Home);
                    if (_disposed || !string.IsNullOrEmpty(LastError)) { return; }
                }
                _chapter = chapter;
                _presentedChapter = chapter;
                _bossDevelopmentStartPending = true;
                IsTransitioning = true;
                await SwitchSceneAsync(GameScreen.Battle);
            }
            catch (System.Exception error)
            {
                if (!_disposed)
                {
                    LastError = "Boss关卡进入失败：" + error.Message;
                    RevLog.Exception(error, "Boss直达流程未完成", "RevGame");
                }
            }
            finally
            {
                _bossDevelopmentStartPending = false;
                _bossDevelopmentEntering = false;
                if (!_disposed) { IsTransitioning = false; }
            }
        }
    }
}
#endif
