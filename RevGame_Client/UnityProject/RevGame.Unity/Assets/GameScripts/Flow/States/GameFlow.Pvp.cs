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
        public const string PvpSceneName = "PvpScene";

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
