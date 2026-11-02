using System;
using RevGame.Core;
using RevGame.Endless;
using RevGame.UI;
using RevGame.View;
using Revolution;
namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>专属场景只含相机；棋盘/HUD资源通过门面在域内装配，Bootstrap仍只存在一份。</summary>
        public const string EndlessSceneName = "EndlessScene";
        public bool EnterEndless(int formation, int difficulty, int seed, bool resume = false, bool isolated = false)
        {
            if (IsGameplayBlocked || Screen != GameScreen.Home || formation < 0 || formation > 2 || difficulty < 0 || difficulty > 2)
            {
                LastError = "请在大厅选择合法棋阵进入无尽";
                return false;
            }
            IsTransitioning = true;
            EnterEndlessAsync(formation, difficulty, seed, resume, isolated);
            return true;
        }
        private async void EnterEndlessAsync(int formation, int difficulty, int seed, bool resume, bool isolated)
        {
            try
            {
                RevUI.Close<EndlessEntryPanel>();
                _loading = await RevUI.OpenAsync<LoadingPanel>();
                ThrowIfDisposed();
                RevScope.Detach(_loading);
                _loading.ResetCover();
                Report(0.02f, "正在唤醒棋心…");
                await RevDataTableManager.LoadAsync<EndlessRuleTable>();
                await RevDataTableManager.LoadAsync<EndlessRouteTable>();
                await RevDataTableManager.LoadAsync<EndlessBossTable>();
                await RevDataTableManager.LoadAsync<EndlessBossSkillTable>();
                await RevDataTableManager.LoadAsync<EndlessMusicTable>();
                await RevDataTableManager.LoadAsync<EndlessComboTable>();
                await RevDataTableManager.LoadAsync<EndlessFormationTable>();
                await RevDataTableManager.LoadAsync<EndlessBoonTable>();
                EndlessConfig.Validate();
                ThrowIfDisposed();
                // UI_Tavern_imgBK（升级卡牌底板）单独成图集，只在无尽里用，进场前一起预载，避免首次弹窗白图。
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(new[]
                {
                    "PieceFrames", "BoardStatus", "CombatFrames", "UIBattle", "UIMenus", "UI_Tavern_imgBKAtlas"
                }
                );
                ThrowIfDisposed();
                var service = RevServices.GetRequired<EndlessService>();
                if (resume)
                {
                    if (!service.Restore(isolated))
                    {
                        throw new InvalidOperationException(service.LastError);
                    }
                }
                else
                {
                    service.Begin(formation, difficulty, seed, isolated);
                }
                await RevScene.LoadAsync(EndlessSceneName, p => Report(0.2f + p * 0.5f, "正在展开象牙金圣坛…"));
                ThrowIfDisposed();
                if (RevScene.CurrentName != EndlessSceneName)
                {
                    throw new InvalidOperationException("无尽场景未完成加载");
                }
                _activePresentation = null;
                Change(GameScreen.Endless);
                EndlessHudPanel panel = await RevUI.OpenAsync<EndlessHudPanel>();
                ThrowIfDisposed();
                await panel.PrepareAsync();
                ThrowIfDisposed();
                Report(1, "棋阵幸存者已就绪");
                await _loading.FadeOutAsync();
                RevUI.Close<LoadingPanel>();
                _loading = null;
                LastError = "";
            }
            catch (Exception error)
            {
                LastError = "无尽加载失败：" + error.Message;
                RevLog.Exception(error, LastError, "RevGame");
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
    }
}
