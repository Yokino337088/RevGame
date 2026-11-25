using System;
using RevGame.Core;
using RevGame.Pvp;
using RevGame.Pvp.Duel;
using RevGame.Pvp.Networking;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        // 围棋场景。决斗切场前在这里完成房间资格校验。
        public const string PvpSceneName = "PvpScene";
        // 独立决斗表现场景名，与编辑器构建列表保持一致。
        public const string DuelSceneName = "PvpDuelScene";
        /// <summary>主线 StoryBook 中九路教学前的开场篇章。</summary>
        private const string PvpTutorialIntroId = "pvp_tutorial_intro";
        /// <summary>主线 StoryBook 中完成全部九路课程后的收尾篇章。</summary>
        private const string PvpTutorialOutroId = "pvp_tutorial_outro";
        // 准备页随当前 PvP 域存活，两个阶段换同一套图集，不在每帧加载资源。
        private DuelPrepPanelBase _duelPreparationPanel;
        // 决斗 HUD 独立于准备页，直到 HUD 关闭后才归还专用图集。
        private GameSpriteAtlasService.ScopedAtlasLease _duelHudAtlas;
        // 围棋面板关掉后提前持有同一张败北立绘，终局无需现取素材。
        private GameSpriteAtlasService.ScopedAtlasLease _duelDefeatAtlas;
        // 只在可见状态变化时传新数据，进度补间不会被网络轮询反复重启。
        private (int revision, int seconds, bool canPick, int side, string blackName, string whiteName) _duelPainted;

        /// <summary>接受请求和实际完成分开；所有UI/GM只走此入口，防止连点创建两条连接或场景加载链。</summary>
        public string PvpDestination { get; private set; } = "Entry";

        /// <summary>大厅只在九路教学尚未明确跳过或完成时询问玩家。</summary>
        public bool ShouldPromptPvpTutorial => !RevServices.GetRequired<StoryProgress>().PvpTutorialResolved;

        /// <summary>玩家主动选择「我是高手」后提交选择，普通模式由原菜单启动。</summary>
        public void SkipPvpTutorial()
        {
            RevServices.GetRequired<StoryProgress>().ResolvePvpTutorial();
        }

        /// <summary>首次进入和从空闲围棋菜单重看都先播放开场。接单时不创建练习、不写教学进度。</summary>
        public bool EnterPvpTutorial()
        {
            GameScreen origin = Screen;
            if (_disposed || _storyBook == null || IsGameplayBlocked ||
                (origin != GameScreen.Home && (origin != GameScreen.Pvp || RevScene.CurrentName != PvpSceneName ||
                    !RevUI.IsOpen<PvpPanel>() || !RevServices.GetRequired<PvpService>().CanStartTutorial)))
            {
                LastError = "请离开当前棋局或匹配后再打开教学";
                return false;
            }
            LastError = "";
            PlayStory(PvpTutorialIntroId, () => StartPvpTutorialAfterStory(origin));
            return true;
        }

        /// <summary>对白完成或跳过后才进入真实教学，旧场景或失效请求不得创建练习。</summary>
        private void StartPvpTutorialAfterStory(GameScreen origin)
        {
            if (_disposed || Screen != origin)
            {
                return;
            }
            if (origin == GameScreen.Home)
            {
                // 从大厅走原有的场景交接和棋盘首屏，开场此时已经归还背景和立绘。
                if (!EnterPvp("Tutorial", false))
                {
                    PvpNoticePanel.ShowMessage(LastError);
                }
                return;
            }
            var panel = RevUI.Get<PvpPanel>();
            if (IsGameplayBlocked || RevScene.CurrentName != PvpSceneName || panel == null ||
                !RevServices.GetRequired<PvpService>().StartTutorial(FinishPvpTutorial))
            {
                LastError = "请离开当前棋局或匹配后再打开教学";
                if (panel != null && panel.Menu == "Entry")
                {
                    panel.ShowDestination("Entry");
                }
                PvpNoticePanel.ShowMessage(LastError);
                return;
            }
            panel.ShowDestination("Tutorial");
        }

        /// <summary>仅在真实完成最后一课时提交进度。收起练习后播放收尾，读完或跳过才打开模式选择。</summary>
        private void FinishPvpTutorial()
        {
            var panel = RevUI.Get<PvpPanel>();
            if (_disposed || Screen != GameScreen.Pvp || RevScene.CurrentName != PvpSceneName || panel == null)
            {
                return;
            }

            RevServices.GetRequired<StoryProgress>().ResolvePvpTutorial();
            panel.HideTutorialBoard();
            PlayStory(PvpTutorialOutroId, () =>
            {
                // 只向发起这次教学的面板交付菜单，退场后不让旧回调重开对战入口。
                if (Screen == GameScreen.Pvp && RevScene.CurrentName == PvpSceneName &&
                    RevUI.IsOpen<PvpPanel>() && ReferenceEquals(RevUI.Get<PvpPanel>(), panel))
                {
                    panel.ShowDestination("Entry");
                }
            });
        }

        /// <summary>从大厅选择对战方式。教学请求先由统一的开场入口接单。</summary>
        public bool EnterPvp(string destination = "Entry")
        {
            return destination == "Tutorial" ? EnterPvpTutorial() : EnterPvp(destination, false);
        }

        /// <summary>接受大厅的一条真实局域网邀请，移交被动监听的票据给对战域。</summary>
        internal bool EnterPvpFromLobbyInvite()
        {
            return EnterPvp("Lan", true);
        }

        /// <summary>验证大厅和邀请状态后接单。取走的接收器由切场流程负责释放或交给围棋服务。</summary>
        private bool EnterPvp(string destination, bool useLobbyInvitation)
        {
            if (IsGameplayBlocked || Screen != GameScreen.Home)
            {
                LastError = "请从大厅进入九路围棋";
                return false;
            }
            LanPeerTransport invitation = useLobbyInvitation ? RevServices.GetRequired<PvpService>().TakeLobbyLanInvitation() : null;
            if (useLobbyInvitation && invitation == null)
            {
                LastError = "这条局域网邀请已失效";
                return false;
            }
            PvpDestination = destination;
            IsTransitioning = true;
            EnterPvpFromButton(invitation);
            return true;
        }

        // 决斗仍属于 PvP 域：切独立表现场景，但保留原房间的传输和权威命令流。
        // 收到权威决斗标识后尝试切场；重连时如果场景已就绪，只重新声明 Ready。
        internal void TryEnterPvpDuel()
        {
            var service = RevServices.GetRequired<PvpService>();
            RefreshDuelPreparation(service);
            if (Screen == GameScreen.Pvp && !IsTransitioning && RevScene.CurrentName == DuelSceneName && service.DuelId.Length != 0)
            {
                // 复连时场景仍可用。重新声明 Ready，并沿当前已确认游标补帧，预测等新锚点才恢复。
                service.MarkDuelSceneReady();
                return;
            }
            if (Screen != GameScreen.Pvp || IsTransitioning || RevScene.CurrentName != PvpSceneName ||
                service.DuelId.Length == 0 || service.DuelError.Length != 0)
            {
                return;
            }
            IsTransitioning = true;
            EnterPvpDuelAsync();
        }

        // GameFlow 保留旧棋盘域和连接，依次打开选角、显示真实预加载、等待权威放行。
        private async void EnterPvpDuelAsync()
        {
            var service = RevServices.GetRequired<PvpService>();
            string duelId = service.DuelId;
            bool completed = false;
            GameSpriteAtlasService.ScopedAtlasLease atlas = null;
            try
            {
                atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas("DuelPrep");
                await atlas.WaitReadyAsync();
                ThrowIfDisposed();
                // 收掉旧棋盘的面板、弹窗与通知，保留 PvP 域的连接和权威准备时钟。
                RevUI.Close<PvpPanel>();
                var select = await RevUI.OpenAsync<DuelSelectPanel, DuelPrepViewData>(DuelViewData(service));
                if (select == null)
                {
                    throw new InvalidOperationException("决斗选角面板加载失败。");
                }
                _duelPreparationPanel = select;
                _duelPainted = default;
                service.ShowDuelSelection();
                while (DuelStillPreparing(service, duelId) &&
                    (service.DuelPrep == null || service.DuelPrep.phase <= DuelPrepPhase.Choosing))
                {
                    await RevTask.Yield();
                }
                if (!DuelStillPreparing(service, duelId))
                {
                    return;
                }
                var loading = await RevUI.OpenAsync<DuelLoadingPanel, DuelPrepViewData>(DuelViewData(service));
                if (loading == null)
                {
                    throw new InvalidOperationException("决斗加载面板加载失败。");
                }
                _duelPreparationPanel = loading;
                _duelPainted = default;
                RevUI.Close<DuelSelectPanel>();
                var atlases = RevServices.GetRequired<GameSpriteAtlasService>();
                _duelHudAtlas = atlases.AcquireScopedAtlas("DuelHud");
                _duelDefeatAtlas = atlases.AcquireScopedAtlas("PvpDefeat");
                await _duelHudAtlas.WaitReadyAsync();
                await _duelDefeatAtlas.WaitReadyAsync();
                ThrowIfDisposed();
                if (!DuelStillPreparing(service, duelId))
                {
                    return;
                }
                // 先准备真正的 Unity 场景。不到预加载完成的门禁回调，任何帧都不能上报 100%。
                await RevScene.LoadWhenReadyAsync(DuelSceneName, value => service.ReportDuelSceneProgress(Mathf.FloorToInt(value * 99f)),
                    () => WaitForDuelActivation(service, duelId), minSeconds: .55f);
                ThrowIfDisposed();
                if (RevScene.CurrentName != DuelSceneName || !DuelStillPreparing(service, duelId))
                {
                    return;
                }
                var view = UnityEngine.Object.FindObjectOfType<PvpDuelView>();
                var hud = await RevUI.OpenAsync<DuelHudPanel>();
                ThrowIfDisposed();
                if (view == null || hud == null)
                {
                    throw new InvalidOperationException("决斗场景或 HUD 尚未就绪。");
                }
                if (!DuelStillPreparing(service, duelId))
                {
                    return;
                }
                view.AttachHud(hud);
                service.MarkDuelSceneReady();
                double deadline = Time.realtimeSinceStartupAsDouble + DuelPreparation.StageTimeoutMs / 1000.0;
                // 激活后仍保留原 Ready 屏障，直到双端场景对象已可用才移开遮挡。
                while (!service.DuelBothReady && DuelStillPreparing(service, duelId) && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    await RevTask.Yield();
                }
                completed = service.DuelBothReady && DuelStillPreparing(service, duelId);
            }
            catch (Exception error)
            {
                LastError = error.Message;
                service.SetDuelError(error.Message);
                service.AbortDuelPreparation();
                RevLog.Exception(error, "决斗切场失败", "Pvp");
            }
            finally
            {
                if (!completed && service.DuelId == duelId)
                {
                    service.AbortDuelPreparation();
                }
                if (!completed)
                {
                    CloseDuelHud();
                }
                _duelPreparationPanel = null;
                RevUI.Close<DuelSelectPanel>();
                RevUI.Close<DuelLoadingPanel>();
                // 收完两张面板的淡出才归还图集，避免退场尾帧的立绘变白。
                double deadline = Time.realtimeSinceStartupAsDouble + 1;
                while ((RevUI.Get<DuelSelectPanel>() != null || RevUI.Get<DuelLoadingPanel>() != null) &&
                    Time.realtimeSinceStartupAsDouble < deadline)
                {
                    await RevTask.Yield();
                }
                atlas?.Dispose();
                IsTransitioning = false;
                if (!completed && Screen == GameScreen.Pvp &&
                    (service.DuelId.Length == 0 || service.DuelId == duelId))
                {
                    ReturnHome();
                }
            }
        }

        // 遮挡画面后关闭 HUD，再释放它持有的图集，返回大厅和异常退出共用同一收尾。
        private void CloseDuelHud()
        {
            RevUI.Close<DuelHudPanel>();
            _duelHudAtlas?.Dispose();
            _duelHudAtlas = null;
            _duelDefeatAtlas?.Dispose();
            _duelDefeatAtlas = null;
        }

        // 门禁只等待网络状态，不请求新的 Unity AsyncOperation；否则挂起的加载队列无法继续。
        private async RevTask<bool> WaitForDuelActivation(PvpService service, string duelId)
        {
            service.ReportDuelSceneProgress(100);
            while (DuelStillPreparing(service, duelId) && service.DuelPrep?.phase != DuelPrepPhase.Released)
            {
                await RevTask.Yield();
            }
            return DuelStillPreparing(service, duelId) && service.DuelPrep?.phase == DuelPrepPhase.Released;
        }

        // 阶段和决斗号都匹配时才允许异步链继续，拒绝晚到的旧局回包。
        private static bool DuelStillPreparing(PvpService service, string duelId)
        {
            return service.DuelId == duelId && !service.DuelPreparationFailed;
        }

        // 生成准备页快照。两席共用固定阵容，面板只读取确认许可和当前业务状态。
        private static DuelPrepViewData DuelViewData(PvpService service)
        {
            return new DuelPrepViewData
            {
                State = service.DuelPrep,
                BlackName = string.IsNullOrWhiteSpace(service.BlackName) ? "黑方棋友" : service.BlackName,
                WhiteName = string.IsNullOrWhiteSpace(service.WhiteName) ? "白方棋友" : service.WhiteName,
                LocalSeat = (int)service.MySide,
                Seconds = service.DuelPickSeconds, CanPick = service.DuelCanPick
            };
        }

        // PvP Logic 每帧先处理通信，再按快照版本、倒计时和确认许可的变化刷新 RevUI。
        private void RefreshDuelPreparation(PvpService service)
        {
            if (_duelPreparationPanel == null)
            {
                return;
            }
            var stamp = (service.DuelPrep?.revision ?? 0, service.DuelPickSeconds, service.DuelCanPick,
                (int)service.MySide, service.BlackName, service.WhiteName);
            if (_duelPainted != stamp)
            {
                _duelPainted = stamp;
                _duelPreparationPanel.SetData(DuelViewData(service));
            }
        }
        /// <summary>Unity 按钮异步适配。切场失败时归还邀请端口和棋局接入状态。</summary>
        private async void EnterPvpFromButton(LanPeerTransport invitation)
        {
            bool completed = false;
            try
            {
                await EnterPvpAsync(invitation);
                completed = true;
            }
            catch (Exception error)
            {
                LastError = "九路围棋加载失败：" + error.Message;
                RevLog.Exception(error, LastError, "Pvp");
                if (_loading != null)
                {
                    _loading.SetData(new LoadingPanel.ProgressData(LoadProgress, LoadStep, LastError));
                }
            }
            finally
            {
                if (!completed && invitation != null)
                {
                    var service = RevServices.GetRequired<PvpService>();
                    if (ReferenceEquals(service.Lan, invitation))
                    {
                        service.Dispose();
                    }
                    else
                    {
                        invitation.Dispose();
                    }
                }
                IsTransitioning = false;
            }
        }
        /// <summary>装配围棋域、棋盘与首屏菜单，交割通过之后才撤掉场景遮挡。</summary>
        private async RevTask EnterPvpAsync(LanPeerTransport invitation)
        {
            _loading = await RevUI.OpenAsync<LoadingPanel>();
            ThrowIfDisposed();
            RevScope.Detach(_loading);
            _loading.ResetCover();
            Report(.05f, "正在铺开九路棋盘…");
            await RevDataTableManager.LoadAsync<PvpRuleTable>();
            await RevDataTableManager.LoadAsync<PvpYayaLineTable>();
            PvpConfig config = PvpConfig.Read(true);
            await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(new[] { "UIBattle", "UIMenus" });
            ThrowIfDisposed();
            var service = RevServices.GetRequired<PvpService>();
            // 围棋服务沿用当前启动会话的隔离选择，测试连通不会改写玩家记住的地址。
            service.Begin(config, isolateServerAddress: RevServices.GetRequired<StoryProgress>().IsIsolated);
            RevTimerHandle receiveDuringLoad = default;
            if (invitation != null && service.AdoptLobbyLanInvitation(invitation))
            {
                // 加载阶段 PvP Logic 尚未运行，短暂推进原传输，避免对方已接受后卡在握手。
                receiveDuringLoad = RevTimer.Every(.05f, invitation.Poll, domain: RevTimeDomain.Unscaled, owner: this);
            }
            try
            {
                await RevScene.LoadAsync(PvpSceneName, p => Report(.2f + p * .5f, "正在进入象牙白棋盘…"));
            }
            finally
            {
                // 场景交接后只由 PvP Logic 轮询，避免同一传输被两个计时器推进。
                receiveDuringLoad.Stop();
            }
            ThrowIfDisposed();
            if (RevScene.CurrentName != PvpSceneName)
            {
                throw new InvalidOperationException("九路围棋场景未完成加载");
            }
            _activePresentation = null;
            Change(GameScreen.Pvp);
            PvpPanel panel = await RevUI.OpenAsync<PvpPanel>();
            await panel.PrepareAsync();
            // 等棋盘和界面都装配好再分配练习盘，玩家中途退出时没有正式房间需要退。
            if (PvpDestination == "Tutorial" && !service.StartTutorial(FinishPvpTutorial))
            {
                throw new InvalidOperationException("九路围棋教学尚未就绪");
            }
            if (!await panel.ShowDestinationAsync(PvpDestination))
            {
                throw new InvalidOperationException("九路围棋首屏菜单未完成交割");
            }
            ThrowIfDisposed();
            Report(1, "九路围棋已就绪");
            await _loading.FadeOutAsync();
            RevUI.Close<LoadingPanel>();
            _loading = null;
            LastError = "";
        }
    }
}
