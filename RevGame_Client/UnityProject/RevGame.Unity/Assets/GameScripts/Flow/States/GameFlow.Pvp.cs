using System;
using RevGame.Core;
using RevGame.Pvp;
using RevGame.UI;
using RevGame.View;
using Revolution;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        // 围棋场景。决斗切场前在这里完成房间资格校验。
        public const string PvpSceneName = "PvpScene";
        // 独立决斗表现场景名，与编辑器构建列表保持一致。
        public const string DuelSceneName = "PvpDuelScene";

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

        // 由 GameFlow 串行换场并关闭旧棋盘面板。等新场景真实就绪再提交 Ready，异常显示给业务层。
        private async void EnterPvpDuelAsync()
        {
            try
            {
                RevUI.Close<PvpPanel>();
                await RevScene.LoadAsync(DuelSceneName);
                ThrowIfDisposed();
                if (RevScene.CurrentName != DuelSceneName)
                {
                    throw new InvalidOperationException("独立决斗场景未加载完成。");
                }
                RevServices.GetRequired<PvpService>().MarkDuelSceneReady();
            }
            catch (Exception error)
            {
                LastError = error.Message;
                RevServices.Get<PvpService>()?.SetDuelError(error.Message);
                RevLog.Exception(error, "决斗切场失败", "Pvp");
            }
            finally
            {
                IsTransitioning = false;
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
