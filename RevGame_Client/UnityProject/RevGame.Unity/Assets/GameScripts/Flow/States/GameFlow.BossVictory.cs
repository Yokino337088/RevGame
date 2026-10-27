// RevActionSequence唯一编排：慢动作镜头→章节死亡→棋盘波浪→积分弹幕→章节收尾剧情。
// 蓝图一次Build，运行时状态属于一个GameFlow；finally恢复镜头/时间/视图，取消和失败也执行。
using System;
using DG.Tweening;
using RevGame.Core;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        private static readonly RevSequenceDefinition BossVictorySequence = RevSequence.Create("Boss击败章节演出", RevSequenceConcurrency.RejectPerSource)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().VictoryFocusAsync(), "慢动作主将特写", cancelOnFailure: true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().VictoryDeathAsync(), "本章专属死亡", cancelOnFailure: true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().VictoryWaveAsync(), "棋盘胜利波浪", cancelOnFailure: true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().VictoryBannerAsync(), "击败与章节积分", cancelOnFailure: true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().VictoryStoryAsync(), "章节收尾剧情", cancelOnFailure: true)
            .Finally(f => f.Do("恢复时间镜头与视图", ctx => ctx.SourceAs<GameFlow>().CleanupVictoryVisuals()))
            .OnCompleted(run => run.Context.SourceAs<GameFlow>().FinishVictory(true))
            .OnCancelled(run => run.Context.SourceAs<GameFlow>().FinishVictory(false))
            .Build();

        private RevSequenceRunner _victoryRunner;
        private RevSequenceHandle _victoryHandle;
        private BossDefeatedInfo _victoryInfo;
        private BoardView _victoryBoard;
        private GamePresentation _victoryPresentation;
        private Tween _victoryTween;
        /// <summary>本次演出独有的声音句柄；取消只停止自己，不能停止其它业务音效。</summary>
        private RevSoundHandle _victorySound;
        private float _victoryOldTimeScale;
        private int _victoryVersion;
        /// <summary>只有本演出启动的剧情才由取消收尾关闭，避免误关其它开发预览。</summary>
        private int _victoryStoryVersion;
        /// <summary>开发GM仅预览表现；不派结算完成、不写剧情进度、不加积分，结束还原活棋。</summary>
        private bool _victoryPreview;
        public bool IsBossVictoryPlaying { get; private set; }
        public string BossVictoryStage { get; private set; } = "idle";
        public bool FinalBossStoryShown { get; private set; }

        /// <summary>胜利事实先于结算UI到达；立即锁流程，防玩家/AI在演出前点击继续。</summary>
        private void OnBossDefeated(BossDefeatedInfo info)
        {
            if (_disposed || IsBossVictoryPlaying || Screen != GameScreen.Battle) { return; }
            _victoryPreview = false;
            StartVictorySequence(info);
        }
        private void StartVictorySequence(BossDefeatedInfo info)
        {
            _victoryInfo = info;
            _victoryBoard = UnityEngine.Object.FindObjectOfType<BoardView>();
            _victoryPresentation = _activePresentation;
            IsBossVictoryPlaying = true;
            _victoryStoryVersion = 0;
            LastError = string.Empty;
            _victoryOldTimeScale = Time.timeScale;
            ++_victoryVersion;
            _victoryRunner ??= new RevSequenceRunner();
            RevMono.AddUpdate(TickVictorySequence, owner: this);
            _victoryHandle = _victoryRunner.Play(BossVictorySequence, this);
        }
        private void TickVictorySequence() { _victoryRunner?.Tick(Time.unscaledDeltaTime); }
        private Vector3 VictoryCenter
        {
            get { return _victoryInfo.Position.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset) + new Vector3(_victoryInfo.BodySize - 1, _victoryInfo.BodySize - 1, 0) * Consts.CellSize * .5f; }
        }
        private async RevTask VictoryFocusAsync()
        {
            BossVictoryStage = "focus";
            if (_victoryBoard == null || _victoryPresentation == null) { throw new InvalidOperationException("Boss演出场景接线缺失"); }
            if (_victoryPreview)
            {
                _victoryBoard.BorrowVictoryBossForPreview(_victoryInfo);
                SetPrepareHidden(true);
            }
            else { _victoryBoard.PrepareVictoryBoss(_victoryInfo); }
            // 轻慢动作只比当前速度慢30%；镜头Tween使用非缩放时间，避免拉近/拉回被倍率再次拖慢。
            Time.timeScale = _victoryOldTimeScale * .7f;
            await VictoryTweenAsync(.4f, value => _victoryPresentation.SetBossFocus(VictoryCenter, _victoryInfo.BodySize, Mathf.SmoothStep(0, 1, value)));
        }
        private async RevTask VictoryDeathAsync()
        {
            BossVictoryStage = "death";
            _victoryBoard.BeginVictoryDeath(_victoryInfo);
            _victorySound = RevSound.Play("BossExit", volume: .5f);
            await VictoryTweenAsync(_victoryBoard.VictoryDeathSeconds, value => _victoryBoard.SetVictoryDeathProgress(value));
            _victoryBoard.ReleaseVictoryBoss();
            Time.timeScale = _victoryOldTimeScale;
        }
        private async RevTask VictoryWaveAsync()
        {
            BossVictoryStage = "wave";
            await VictoryTweenAsync(1.7f, value =>
            {
                _victoryPresentation.SetBossFocus(VictoryCenter, _victoryInfo.BodySize, 1f - Mathf.SmoothStep(0,1,Mathf.Clamp01(value * 4)));
                _victoryBoard.SetVictoryBoardWave(value, VictoryCenter);
            });
            _victoryBoard.ClearVictoryBoardWave();
            // 胜利弹幕也只在镜头回到棋盘全景之后显示。
            _victoryPresentation.ClearBossFocus();
        }
        private async RevTask VictoryBannerAsync()
        {
            BossVictoryStage = "banner-loading";
            int version = _victoryVersion;
            await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(new[] { "UIMenus", "UIBattle" });
            CheckVictoryVersion(version);
            BossDefeatedInfo snapshot = _victoryInfo;
            BossVictoryPanel panel = await RevUI.OpenAsync<BossVictoryPanel, BossDefeatedInfo>(snapshot);
            if (version != _victoryVersion || _disposed)
            {
                // 资源加载可能晚于取消；仅关闭携带本次事实快照的实例，不能关闭下一次演出。
                if (panel != null && ReferenceEquals(panel.Data, snapshot)) { RevUI.Close(panel); }
                throw new OperationCanceledException("Boss弹幕请求已过期");
            }
            CheckVictoryVersion(version);
            if (panel == null) { throw new InvalidOperationException("Boss积分弹幕加载失败"); }
            BossVictoryStage = "banner";
            await VictoryTweenAsync(2.2f, _ => { });
            RevUI.Close<BossVictoryPanel>();
        }
        private async RevTask VictoryStoryAsync()
        {
            if (_victoryPreview) { return; }
            BossVictoryStage = "story";
            int version = _victoryVersion;
            PlayStory("boss_after_" + _victoryInfo.Chapter, null);
            _victoryStoryVersion = _storyVersion;
            while (IsStoryPlaying) { CheckVictoryVersion(version); await RevTask.Yield(); }
            CheckVictoryVersion(version);
            if (!string.IsNullOrEmpty(LastError)) { throw new InvalidOperationException(LastError); }
            if (_victoryInfo.Chapter == Consts.ChapterCount) { FinalBossStoryShown = true; }
        }

        /// <summary>每次只拥有一个表现Tween，非缩放时间避免慢动作把序列变成无限等待；版本检查阻止取消后提交。</summary>
        private async RevTask VictoryTweenAsync(float seconds, Action<float> update)
        {
            int version = _victoryVersion;
            float progress = 0;
            Tween tween = DOTween.To(() => progress, value => { progress = value; update(value); }, 1f, seconds).SetUpdate(true).SetEase(Ease.Linear);
            _victoryTween = tween;
            while (tween.IsActive() && tween.IsPlaying()) { CheckVictoryVersion(version); await RevTask.Yield(); }
            CheckVictoryVersion(version);
            if (progress < .999f) { throw new InvalidOperationException("Boss演出动画被提前停止"); }
            if (ReferenceEquals(_victoryTween, tween)) { _victoryTween = null; }
        }
        private void CheckVictoryVersion(int version)
        { if (_disposed || !IsBossVictoryPlaying || version != _victoryVersion) { throw new OperationCanceledException("Boss演出已取消"); } }

        /// <summary>finally即时清理，只停止本演出创建的动画，不使用KillAll/全局序列清空。</summary>
        private void CleanupVictoryVisuals()
        {
            _victoryTween?.Kill(false); _victoryTween = null;
            RevSound.Stop(_victorySound);
            _victorySound = default;
            RevSound.Unload("BossExit");
            Time.timeScale = _victoryOldTimeScale;
            if (_victoryPresentation != null) { _victoryPresentation.ClearBossFocus(); }
            if (_victoryBoard != null) { _victoryBoard.ReleaseVictoryBoss(); _victoryBoard.ClearVictoryBoardWave(); }
            RevUI.Close<BossVictoryPanel>();
            if (_victoryPreview) { SetPrepareHidden(false); }
        }
        private void FinishVictory(bool success)
        {
            if (!IsBossVictoryPlaying) { return; }
            IsBossVictoryPlaying = false;
            BossVictoryStage = success ? "completed" : "cancelled";
            RevMono.RemoveUpdate(TickVictorySequence);
            if (!success && !_disposed)
            {
                RevLog.Warn("Boss演出未完整播放，已恢复视觉并开放真实结算", "RevGame");
                if (_victoryStoryVersion != 0 && _storyVersion == _victoryStoryVersion) { CancelStory(); }
            }
            BossDefeatedInfo info = _victoryInfo;
            bool preview = _victoryPreview;
            _victoryPreview = false;
            _victoryBoard = null; _victoryPresentation = null; _victoryInfo = null;
            if (!_disposed && !preview && info != null && Screen == GameScreen.Battle)
            { RevEvent.DispatchEvent<BossDefeatedInfo>(Events.BossVictoryPresentationFinished, info); }
        }
        private void CancelBossVictory()
        {
            if (!IsBossVictoryPlaying) { return; }
            ++_victoryVersion;
            _victoryHandle.Stop();
            _victoryRunner?.Tick(0);
        }
    }
}
