// ============================================================
// HomePanel.cs —— 主界面入口。
// 界面不负责开战斗域，只向唯一的 GameFlow 提交「进入选关」请求。
// ============================================================
using RevGame.Flow;
using Revolution;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]
    public sealed class HomePanel : RevUIPanel
    {
        [RevBind("btnPvP/label")]
        private Text _pvpLabel;
        /// <summary>
        /// 用框架的打开转场钩子播放“淡入 + 弹入”：透明度从 0 到 1，同时轻微缩放回弹到原大小。
        /// 必须把 onDone 交给动画，框架收到完成通知后才会结束 Opening 并回调打开方。
        /// </summary>
        protected override void PlayOpenTransition(System.Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeIn, 0.1f, onDone, owner: this);
        }

        /// <summary>
        /// 用关闭转场钩子播放“淡出 + 弹出”：透明度渐隐并轻微缩小。
        /// onDone 触发后框架才会关闭并回收面板，避免动画尚未播完就被隐藏。
        /// </summary>
        protected override void PlayCloseTransition(System.Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.PopOut, 0.1f, onDone, owner: this);
        }

        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform);
        }

        /// <summary>框架按按钮节点名自动接线，面板复用时不会叠加手动监听。</summary>
        [RevButtonClick("btnPlay")]
        private void OnPlay()
        {
            RevServices.GetRequired<GameFlow>().OpenLevels();
        }

        /// <summary>默认解锁的独立模式入口，不查询六章通关条件。</summary>
        [RevButtonClick("btnEndless")]
        private async void OnEndless()
        {
            // Entry首次打开前等待真实图集像素，避免AB冷启动出现白膜。
            await RevServices.GetRequired<RevGame.View.GameSpriteAtlasService>().PreloadAsync(new[] { "UIBattle", "UIMenus" });
            await RevUI.OpenAsync<RevGame.Endless.EndlessEntryPanel>();
        }

        /// <summary>标准围棋经过唯一流程入口进入专属场景，联网方式在模式面板中选择。</summary>
        [RevButtonClick("btnPvP")]
        private async void OnPvP()
        {
            try
            {
                await RevServices.GetRequired<RevGame.View.GameSpriteAtlasService>().PreloadAsync(new[] { "UIBattle", "UIMenus" });
                await RevUI.OpenAsync<RevGame.Pvp.PvpEntryPanel>();
            }
            catch (System.Exception error)
            {
                _pvpLabel.text = "对战入口加载失败";
                RevLog.Exception(error, "对战入口加载失败", "Pvp");
            }
        }

        [RevButtonClick("btnQuit")]
        private void OnQuit()
        {
            Bootstrap.GameBootstrap.RequestQuit();
        }
        [RevButtonClick("btnSettings")]
        private void OnSettings() { RevServices.GetRequired<GameFlow>().OpenAudioSettings(); }
    }
}
