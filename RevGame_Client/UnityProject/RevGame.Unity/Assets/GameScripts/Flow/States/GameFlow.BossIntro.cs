// Boss入场仍由唯一GameFlow编排：准备UI暂隐→就绪→特写→逆向显现→登场弹幕/棋盘舞动→还原。
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
        /// <summary>不可变蓝图仅构建一次。入场是表现序列，不创建第二套战斗阶段；finally取消也归位。</summary>
        private static readonly RevSequenceDefinition BossIntroSequence = RevSequence.Create("Boss回合入场",RevSequenceConcurrency.RejectPerSource)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().IntroReadyAsync(),"等待真实棋盘与场景",cancelOnFailure:true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().IntroFocusAsync(),"主将特写",cancelOnFailure:true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().IntroRevealAsync(),"逆向死亡显现",cancelOnFailure:true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().IntroReturnCameraAsync(),"镜头完整拉回",cancelOnFailure:true)
            .WaitTask(ctx => ctx.SourceAs<GameFlow>().IntroAnnounceAsync(),"闪烁登场与棋盘舞动",cancelOnFailure:true)
            .Finally(f => f.Do("恢复准备UI与镜头",ctx => ctx.SourceAs<GameFlow>().CleanupBossIntro()))
            .OnCompleted(run => run.Context.SourceAs<GameFlow>().FinishBossIntro(true))
            .OnCancelled(run => run.Context.SourceAs<GameFlow>().FinishBossIntro(false)).Build();
        private RevSequenceRunner _introRunner;
        private RevSequenceHandle _introHandle;
        private BoardView _introBoard;
        private GamePresentation _introPresentation;
        private BossDefeatedInfo _introInfo;
        private Tween _introTween;
        /// <summary>本次演出独有的声音句柄；取消只停止自己，不能停止其它业务音效。</summary>
        private RevSoundHandle _introSound;
        /// <summary>版本归本流程；晚到的UI/资源不得显示上一场入场。时间不改Time.timeScale，准备Logic由门禁暂停。</summary>
        private int _introVersion;
        public bool IsBossIntroPlaying { get; private set; }
        public string BossIntroStage { get; private set; } = "idle";

        /// <summary>准备事件优先锁输入和计时。此时视图可能未加载，所以只排队，随后等待真实就绪。</summary>
        private void OnBossPrepare(int round)
        {
            CancelBossIntro();
            var battle = RevServices.GetRequired<IBattleService>();
            if (_disposed || Screen != GameScreen.Battle || !battle.IsBossRound) { return; }
            IsBossIntroPlaying = true; BossIntroStage = "waiting"; ++_introVersion;
            SetPrepareHidden(true);
            _introRunner ??= new RevSequenceRunner();
            RevMono.AddUpdate(TickBossIntro,owner:this);
            _introHandle = _introRunner.Play(BossIntroSequence,this);
        }
        private void TickBossIntro()
        {
            if (IsBossIntroPlaying) { SetPrepareHidden(true); }
            _introRunner?.Tick(Time.unscaledDeltaTime);
        }
        private async RevTask IntroReadyAsync()
        {
            int version = _introVersion;
            float deadline = Time.realtimeSinceStartup + 40;
            while (true)
            {
                CheckIntroVersion(version);
                if (Time.realtimeSinceStartup > deadline) { throw new TimeoutException("Boss入场等待资源超时"); }
                var board = UnityEngine.Object.FindObjectOfType<BoardView>();
                if (!IsTransitioning && _loading == null && board != null && board.IsReady && _activePresentation != null
                    && _activePresentation.IsChapterBackgroundReady && !IsStoryPlaying)
                { _introBoard = board; _introPresentation = _activePresentation; break; }
                await RevTask.Yield();
            }
            var battle = RevServices.GetRequired<IBattleService>();
            foreach (var piece in battle.EnemyPieces)
            {
                if (piece.IsKing && piece.IsAlive)
                { _introInfo = new BossDefeatedInfo(battle.Chapter,battle.Round,piece.Id,piece.Pos,piece.Size,battle.ChapterScore,false); break; }
            }
            if (_introInfo == null) { throw new InvalidOperationException("入场没有真实主将"); }
            SetPrepareHidden(true);
            _introBoard.BeginBossIntro(_introInfo);
        }
        private Vector3 IntroCenter
        {
            get { return _introInfo.Position.ToWorldPosition(Consts.CellSize,Consts.BoardOriginOffset) + new Vector3(_introInfo.BodySize-1,_introInfo.BodySize-1,0)*Consts.CellSize*.5f; }
        }
        private async RevTask IntroFocusAsync()
        {
            BossIntroStage = "focus";
            await IntroTweenAsync(.4f,t => _introPresentation.SetBossFocus(IntroCenter,_introInfo.BodySize,Mathf.SmoothStep(0,1,t)));
        }
        private async RevTask IntroRevealAsync()
        {
            BossIntroStage = "reveal";
            _introSound = RevSound.Play("BossEntrance", volume: .55f);
            await IntroTweenAsync(_introBoard.BossIntroSeconds,t => _introBoard.SetBossIntroProgress(t));
            _introBoard.EndBossIntroVisual();
        }
        /// <summary>等待镜头Tween结束并归零后再开弹幕，避免横幅挡住Boss特写；取消仍由finally归位。</summary>
        private async RevTask IntroReturnCameraAsync()
        {
            BossIntroStage = "return-camera";
            await IntroTweenAsync(.45f,t => _introPresentation.SetBossFocus(IntroCenter,_introInfo.BodySize,1-Mathf.SmoothStep(0,1,t)));
            _introPresentation.ClearBossFocus();
        }
        private async RevTask IntroAnnounceAsync()
        {
            BossIntroStage = "banner-loading";
            int version = _introVersion;
            await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(new[]{"UIMenus","UIBattle"});
            CheckIntroVersion(version);
            BossDefeatedInfo snapshot = _introInfo;
            var panel = await RevUI.OpenAsync<BossIntroPanel,BossDefeatedInfo>(snapshot);
            if (_disposed || version != _introVersion)
            {
                if (panel != null && ReferenceEquals(panel.Data,snapshot)) { RevUI.Close(panel); }
                throw new OperationCanceledException("登场弹幕过期");
            }
            if (panel == null) { throw new InvalidOperationException("Boss登场弹幕加载失败"); }
            BossIntroStage = "announce";
            await IntroTweenAsync(2.4f,t =>
            {
                _introBoard.SetBossIntroDance(t);
                panel.SetFlashProgress(t);
            });
            RevUI.Close(panel);
        }
        /// <summary>纯表现秒数与非缩放时钟。Kill不能被当作成功，版本检查使中途返大厅的旧续体失效。</summary>
        private async RevTask IntroTweenAsync(float seconds,Action<float> update)
        {
            int version = _introVersion; float progress = 0;
            Tween tween = DOTween.To(()=>progress,t=>{progress=t;update(t);},1,seconds).SetUpdate(true).SetEase(Ease.Linear);
            _introTween = tween;
            while (tween.IsActive() && tween.IsPlaying()) { CheckIntroVersion(version); await RevTask.Yield(); }
            CheckIntroVersion(version);
            if (progress < .999f) { throw new InvalidOperationException("Boss入场动画提前停止"); }
            if (ReferenceEquals(_introTween,tween)) { _introTween = null; }
        }
        private void CheckIntroVersion(int version)
        { if (_disposed || !IsBossIntroPlaying || version != _introVersion) { throw new OperationCanceledException("Boss入场已取消"); } }
        /// <summary>只隐藏CanvasGroup而不关闭准备面板，六张手牌仍参与场景就绪屏障；显示后恢复原交互。</summary>
        public void SetPrepareHidden(bool hidden)
        {
            var panel = RevUI.Get<PreparePanel>();
            if (panel == null) { return; }
            var group = panel.GetComponent<CanvasGroup>();
            if (group == null) { throw new InvalidOperationException("准备面板缺少CanvasGroup"); }
            group.alpha = hidden ? 0 : 1; group.interactable = !hidden; group.blocksRaycasts = !hidden;
        }
        private void CleanupBossIntro()
        {
            _introTween?.Kill(false); _introTween = null;
            // 取消/换场先停声音再释放，迟到的加载由RevSound处理，不影响战斗其它音效。
            RevSound.Stop(_introSound);
            _introSound = default;
            RevSound.Unload("BossEntrance");
            if (_introBoard == null) { _introBoard = UnityEngine.Object.FindObjectOfType<BoardView>(); }
            if (_introBoard != null) { _introBoard.EndBossIntroVisual(); _introBoard.ClearVictoryBoardWave(); }
            if (_introPresentation != null) { _introPresentation.ClearBossFocus(); }
            RevUI.Close<BossIntroPanel>(); SetPrepareHidden(false);
        }
        private void FinishBossIntro(bool success)
        {
            if (!IsBossIntroPlaying) { return; }
            IsBossIntroPlaying = false; BossIntroStage = success ? "completed" : "cancelled";
            RevMono.RemoveUpdate(TickBossIntro);
            if (!success && !_disposed) { RevLog.Warn("Boss入场未完整播放，已恢复准备操作","RevGame"); }
            _introInfo = null; _introBoard = null; _introPresentation = null;
            if (!_disposed) { RevEvent.DispatchEvent(Events.BossIntroFinished); }
        }
        private void CancelBossIntro()
        {
            if (!IsBossIntroPlaying) { return; }
            ++_introVersion; _introHandle.Stop(); _introRunner?.Tick(0);
        }
    }
}
