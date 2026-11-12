using System;
using RevGame.Core;
using RevGame.Pvp;
using RevGame.Pvp.Duel;
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
        // 准备页随当前 PvP 域存活，两个阶段换同一套图集，不在每帧加载资源。
        private DuelPrepPanelBase _duelPreparationPanel;
        // 只在可见状态变化时传新数据，进度补间不会被网络轮询反复重启。
        private (int revision, int seconds, DuelRole black, DuelRole white, bool canPick, int side, string blackName, string whiteName) _duelPainted;

        /// <summary>接受请求和实际完成分开；所有UI/GM只走此入口，防止连点创建两条连接或场景加载链。</summary>
        public string PvpDestination { get; private set; } = "Entry";

        public bool EnterPvp(string destination = "Entry")
        {
            if (IsGameplayBlocked || Screen != GameScreen.Home)
            {
                LastError = "请从大厅进入九路围棋";
                return false;
            }
            PvpDestination = destination;
            IsTransitioning = true;
            EnterPvpFromButton();
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
                // 先准备真正的 Unity 场景。不到预加载完成的门禁回调，任何帧都不能上报 100%。
                await RevScene.LoadWhenReadyAsync(DuelSceneName, value => service.ReportDuelSceneProgress(Mathf.FloorToInt(value * 99f)),
                    () => WaitForDuelActivation(service, duelId), minSeconds: .55f);
                ThrowIfDisposed();
                if (RevScene.CurrentName != DuelSceneName || !DuelStillPreparing(service, duelId))
                {
                    return;
                }
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
                if (!completed && Screen == GameScreen.Pvp && service.DuelId == duelId)
                {
                    ReturnHome();
                }
            }
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

        // 生成当前面板快照；本席预选可立即反馈，对端选择只读权威回包。
        private static DuelPrepViewData DuelViewData(PvpService service)
        {
            return new DuelPrepViewData
            {
                State = service.DuelPrep, Black = service.DuelPreviewFor(1), White = service.DuelPreviewFor(2),
                BlackName = string.IsNullOrWhiteSpace(service.BlackName) ? "黑方棋友" : service.BlackName,
                WhiteName = string.IsNullOrWhiteSpace(service.WhiteName) ? "白方棋友" : service.WhiteName,
                LocalSeat = (int)service.MySide,
                Seconds = service.DuelPickSeconds, CanPick = service.DuelCanPick
            };
        }

        // PvP Logic 每帧先处理通信再调此入口。只在数据有变化时刷新 RevUI。
        private void RefreshDuelPreparation(PvpService service)
        {
            if (_duelPreparationPanel == null)
            {
                return;
            }
            var stamp = (service.DuelPrep?.revision ?? 0, service.DuelPickSeconds,
                service.DuelPreviewFor(1), service.DuelPreviewFor(2), service.DuelCanPick,
                (int)service.MySide, service.BlackName, service.WhiteName);
            if (_duelPainted != stamp)
            {
                _duelPainted = stamp;
                _duelPreparationPanel.SetData(DuelViewData(service));
            }
        }
        /// <summary>Unity按钮的void适配仅捕获异常；实际跨帧工作仍由RevTask承载。</summary>
        private async void EnterPvpFromButton()
        {
            try
            {
                await EnterPvpAsync();
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
                IsTransitioning = false;
            }
        }
        private async RevTask EnterPvpAsync()
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
            RevServices.GetRequired<PvpService>().Begin(config);
            await RevScene.LoadAsync(PvpSceneName, p => Report(.2f + p * .5f, "正在进入象牙白棋盘…"));
            ThrowIfDisposed();
            if (RevScene.CurrentName != PvpSceneName)
            {
                throw new InvalidOperationException("九路围棋场景未完成加载");
            }
            _activePresentation = null;
            Change(GameScreen.Pvp);
            PvpPanel panel = await RevUI.OpenAsync<PvpPanel>();
            await panel.PrepareAsync();
            panel.ShowDestination(PvpDestination);
            ThrowIfDisposed();
            Report(1, "九路围棋已就绪");
            await _loading.FadeOutAsync();
            RevUI.Close<LoadingPanel>();
            _loading = null;
            LastError = "";
        }
    }
}
