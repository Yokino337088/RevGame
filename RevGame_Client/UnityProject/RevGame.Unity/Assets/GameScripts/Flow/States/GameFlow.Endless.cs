using System;
using RevGame.Core;
using RevGame.Endless;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    public sealed partial class GameFlow
    {
        /// <summary>专属场景只含相机；棋盘/HUD资源通过门面在域内装配，Bootstrap仍只存在一份。</summary>
        public const string EndlessSceneName = "EndlessScene";

        /// <summary>无尽棋盘和 HUD 首屏依赖的既有图集，按实际完成数报告加载进度。</summary>
        private static readonly string[] EndlessAtlases =
        {
            "PieceFrames", "BoardStatus", "CombatFrames", "EndlessHeartEye", "EndlessCardArrow", "UIBattle", "UIMenus", "EndlessMenu"
        };

        /// <summary>提交新开或续档；仅大厅可接单，返回 true 表示过场已接受。</summary>
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
        /// <summary>
        /// 依次准备配置、场景和实际首屏。候选局的存档写入直到遮挡完成才开放。
        /// 未交付的候选在本方法释放，现有续档保留给玩家从大厅继续。
        /// </summary>
        private async void EnterEndlessAsync(int formation, int difficulty, int seed, bool resume, bool isolated)
        {
            EndlessService service = null;
            try
            {
                RevUI.Close<EndlessEntryPanel>();
                _loading = await RevUI.OpenAsync<LoadingPanel>();
                ThrowIfDisposed();
                RevScope.Detach(_loading);
                _loading.ResetCover();
                Report(0.02f, "正在唤醒棋心…");
                await LoadEndlessTablesAsync();
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(EndlessAtlases,
                    progress: value => Report(Mathf.Lerp(0.18f, 0.35f, value), "正在准备棋子与界面图集…"));

                // 在内存中准备候选。新局的自动保存、后台保存和首存都必须遵守同一提交边界。
                service = RevServices.GetRequired<EndlessService>();
                if (resume)
                {
                    if (!service.Restore(isolated, pauseOnEntry: true))
                    {
                        throw new InvalidOperationException(service.LastError);
                    }
                }
                else
                {
                    service.Begin(formation, difficulty, seed, isolated, deferFirstSave: true);
                }

                Report(0.38f, "正在展开象牙金圣坛…");
                await RevScene.LoadAsync(EndlessSceneName,
                    value => Report(Mathf.Lerp(0.38f, 0.62f, value), "正在展开象牙金圣坛…"));
                ThrowIfDisposed();
                if (RevScene.CurrentName != EndlessSceneName)
                {
                    throw new InvalidOperationException("无尽场景未完成加载");
                }
                _activePresentation = null;
                Change(GameScreen.Endless);
                EndlessHudPanel panel = await OpenEndlessHudAsync();
                await panel.PrepareAsync(value => Report(Mathf.Lerp(0.74f, 0.98f, value), "正在布置棋盘与守护棋子…"));
                ThrowIfDisposed();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                if (!resume && isolated)
                {
                    CheckDevelopmentFailure("endless-before-save");
                }
#endif

                // 揭幕结束前始终持有输入门禁。最后一次跨帧成功返回后才让新局替换续档。
                Report(1f, "棋阵幸存者已就绪");
                await _loading.FadeOutAsync();
                ThrowIfDisposed();
                RevUI.Close<LoadingPanel>();
                _loading = null;
                LastError = string.Empty;
                if (!resume)
                {
                    service.CommitFirstSave();
                }
            }
            catch (Exception error)
            {
                service?.Dispose();
                if (!_disposed)
                {
                    LastError = "无尽加载失败：" + error.Message;
                    RevLog.Exception(error, LastError, "RevGame");
                    if (_loading != null)
                    {
                        _loading.SetData(new LoadingPanel.ProgressData(LoadProgress, LoadStep, LastError));
                    }
                }
            }
            finally
            {
                IsTransitioning = false;
            }
        }

        /// <summary>按无尽配置的实际读取顺序推进读条，然后验证整套规则。</summary>
        private async RevTask LoadEndlessTablesAsync()
        {
            await LoadEndlessTableAsync<EndlessRuleTable>(0.04f);
            await LoadEndlessTableAsync<EndlessRouteTable>(0.06f);
            await LoadEndlessTableAsync<EndlessBossTable>(0.08f);
            await LoadEndlessTableAsync<EndlessBossSkillTable>(0.10f);
            await LoadEndlessTableAsync<EndlessMusicTable>(0.12f);
            await LoadEndlessTableAsync<EndlessComboTable>(0.14f);
            await LoadEndlessTableAsync<EndlessFormationTable>(0.16f);
            await LoadEndlessTableAsync<EndlessBoonTable>(0.18f);
            EndlessConfig.Validate();
        }

        /// <summary>单张表成功读取后报告已完成节点，失败继续交给过场的恢复入口。</summary>
        private async RevTask LoadEndlessTableAsync<T>(float progress) where T : class, RevIDataTable, new()
        {
            await RevDataTableManager.LoadAsync<T>();
            Report(progress, "正在读取无尽规则…");
        }

        /// <summary>使用真实开窗票据推进界面段。只有 HUD 交付成功后才返回给棋盘装配。</summary>
        private async RevTask<EndlessHudPanel> OpenEndlessHudAsync()
        {
            var ticket = RevUI.OpenTracked<EndlessHudPanel>(deferAssembly: true);
            while (ticket.Status == RevUIOpenStatus.Pending)
            {
                float progress = ticket.Stage switch
                {
                    RevUIOpenStage.Resource => 0.63f,
                    RevUIOpenStage.Queued => 0.67f,
                    RevUIOpenStage.Instance => 0.69f,
                    _ => 0.71f
                };
                Report(progress, "正在展开战场界面…");
                await RevTask.Yield();
            }

            ThrowIfDisposed();
            EndlessHudPanel panel = RevUI.Get<EndlessHudPanel>();
            if (ticket.Status != RevUIOpenStatus.Succeeded || panel == null)
            {
                throw new InvalidOperationException("无尽界面未就绪：" + ticket.Reason);
            }
            Report(0.74f, "正在布置棋盘与守护棋子…");
            return panel;
        }
    }
}
