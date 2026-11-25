// ============================================================
// HomePanel.cs —— 主界面入口。
// 界面不负责开战斗域，只向唯一的 GameFlow 提交「进入选关」请求。
// ============================================================
using System;
using RevGame.Core;
using RevGame.Flow;
using RevGame.View;
using Revolution;
using UnityEngine.UI;

namespace RevGame.UI
{
    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]
    public sealed partial class HomePanel : RevUIPanel
    {
        /// <summary>无尽入口依赖的图集，预热完再打开对应菜单。</summary>
        private static readonly string[] EndlessEntryAtlases = { "UIBattle", "UIMenus", "EndlessMenu" };
        /// <summary>围棋入口依赖的公共 UI 图集。</summary>
        private static readonly string[] PvpEntryAtlases = { "UIBattle", "UIMenus" };
        /// <summary>无尽模式入口，保留原图和文字作为错误提示的样式来源。</summary>
        [RevBind("btnEndless")] private Button _endlessEntry;
        /// <summary>九路对战入口，与无尽入口共用在途请求门禁。</summary>
        [RevBind("btnPvP")] private Button _pvpEntry;
        /// <summary>菜单请求代次。离开或重开大厅时作废，阻止跨界面落屏。</summary>
        private int _modeRequestVersion;
        /// <summary>正在加载的入口标题。非空表示本大厅已有一个模式菜单等待交付。</summary>
        private Text _loadingModeLabel;
        /// <summary>加载前的入口标题，交付、失败或离场时恢复。</summary>
        private string _loadingModeTitle;
        /// <summary>无法打开入口时的可关闭提示，随当前大厅退场销毁。</summary>
        private GameConfirmation _modeError;
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

        /// <summary>先安排公共入口，再由浏览器扩展复制底部按钮，最后把大厅放入安全区。</summary>
        protected override void OnBindView()
        {
            GameLobbyLayout.ConfigureContent(transform);
            BindCottageStoryEntry();
            GameMobileLayout.EnableSafePanel(transform);
        }

        /// <summary>框架按按钮节点名自动接线，面板复用时不会叠加手动监听。</summary>
        [RevButtonClick("btnPlay")]
        private void OnPlay()
        {
            RevServices.GetRequired<GameFlow>().OpenLevels();
        }

        /// <summary>无尽模式从大厅开放，先等圣坛图集就绪，再打开阵型选择。</summary>
        [RevButtonClick("btnEndless")]
        private void OnEndless()
        {
            OpenLobbyModeAsync<RevGame.Endless.EndlessEntryPanel>(_endlessEntry, EndlessEntryAtlases).Forget();
        }

        /// <summary>标准围棋经过唯一流程入口进入专属场景，联网方式在模式面板中选择。</summary>
        [RevButtonClick("btnPvP")]
        private void OnPvP()
        {
            OpenLobbyModeAsync<RevGame.Pvp.PvpEntryPanel>(_pvpEntry, PvpEntryAtlases).Forget();
        }

        /// <summary>同一次大厅只预热一个模式菜单。资源回来后重新核对大厅归属和遮挡。</summary>
        private async RevTask OpenLobbyModeAsync<TPanel>(Button entry, string[] atlases) where TPanel : RevUIPanel
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (_loadingModeLabel != null || _openingBook || _modeError?.IsOpen == true || !CanPresentModeEntry(flow))
            {
                return;
            }
            int version = ++_modeRequestVersion;
            _loadingModeLabel = entry.GetComponentInChildren<Text>(true);
            _loadingModeTitle = _loadingModeLabel.text;
            _loadingModeLabel.text = "正在打开…";
            try
            {
                // 图集像素交付后才创建菜单。玩家可能已去了选关或设置，旧请求此时直接收尾。
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(atlases);
                if (version != _modeRequestVersion || !CanPresentModeEntry(flow))
                {
                    return;
                }
                TPanel panel = await RevUI.OpenAsync<TPanel>();
                if (panel == null && version == _modeRequestVersion && CanPresentModeEntry(flow))
                {
                    throw new InvalidOperationException("入口面板未能打开");
                }
            }
            catch (Exception error)
            {
                if (version == _modeRequestVersion && CanPresentModeEntry(flow))
                {
                    RevLog.Warn(_loadingModeTitle + "入口打开失败：" + error.Message, "RevGame");
                    _modeError = new GameConfirmation(entry, "暂时无法打开" + _loadingModeTitle,
                        "请关闭提示，再点击入口重试。", null, "关闭", null);
                }
            }
            finally
            {
                if (version == _modeRequestVersion)
                {
                    RestoreModeLabel();
                }
            }
        }

        /// <summary>入口只归当前未被覆盖的大厅。迟到结果不得盖到其它玩家页面上。</summary>
        private bool CanPresentModeEntry(GameFlow flow)
        {
            return this != null && State == RevUIPanelState.Opened && !IsCovered
                && flow.Screen == GameScreen.Home && !flow.IsGameplayBlocked
                && !RevUI.IsOpen<RevGame.Endless.EndlessEntryPanel>() && !RevUI.IsOpen<RevGame.Pvp.PvpEntryPanel>();
        }

        /// <summary>结束当前请求的等待文案并解锁两枚模式按钮。</summary>
        private void RestoreModeLabel()
        {
            if (_loadingModeLabel != null)
            {
                _loadingModeLabel.text = _loadingModeTitle;
            }
            _loadingModeLabel = null;
            _loadingModeTitle = null;
        }

        [RevButtonClick("btnQuit")]
        private void OnQuit()
        {
            GameFlow.RequestQuit();
        }
        [RevButtonClick("btnSettings")]
        private void OnSettings() { RevServices.GetRequired<GameFlow>().OpenAudioSettings(); }
    }
}
