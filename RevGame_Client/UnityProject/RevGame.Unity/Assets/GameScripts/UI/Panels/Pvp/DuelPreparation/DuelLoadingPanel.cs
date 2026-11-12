using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>双人决斗加载页。两张卡各自显示真实进度，不用动画猜测对方是否完成。</summary>
    [RevUIPanel("UI/Panel/Pvp/DuelPrep", RevUILayer.Top, InBackStack = false, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class DuelLoadingPanel : DuelPrepPanelBase
    {
        [RevBind("layout/prompt")] private Text _prompt; // 双端加载、等人和放行状态。

        // 慢的一席仍显示具体百分比，快的一席可看到为什么还留在这里。
        protected override void PaintCenter()
        {
            bool released = Data.State?.phase == DuelPrepPhase.Released;
            bool ownDone = Data.State?.Progress(Data.LocalSeat) == 100;
            _prompt.text = released ? "双方就绪 · 即将开战" : ownDone ? "已就绪 · 等待对方加载" : "正在准备决斗";
        }

        // 取消请求由房间权威处理，场景门会正常卸载未使用的附加场景。
        [RevButtonClick("layout/cancel")]
        private void Cancel()
        {
            RevServices.GetRequired<PvpService>().AbortDuelPreparation();
        }
    }
}
