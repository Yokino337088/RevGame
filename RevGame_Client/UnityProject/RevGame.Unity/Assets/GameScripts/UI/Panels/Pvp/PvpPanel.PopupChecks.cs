#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Revolution;

namespace RevGame.Pvp
{
    /// <summary>通过真实确认框运行合成请求，确认按钮只记录验收行为。</summary>
    public sealed partial class PvpPanel
    {
        /// <summary>等待正常棋盘打开后再做会打断输入的确认测试。</summary>
        internal bool PopupCheckReady => BattleReady && _confirmationQueue != null && _visibleConfirmation == null;

        /// <summary>固定请求用真实确认界面展示和隐藏，无棋局提交。</summary>
        internal RevTask<RevPopupStatus> ShowPopupForCheck(string key, int priority, string phase, double timeout, Action confirmed)
        {
            return EnqueueConfirmation(key, "确认验收 " + key, confirmed, priority, phase, timeout);
        }

        /// <summary>读取窗口实际可见状态，以及当前接受点击的唯一票据。</summary>
        internal Dictionary<string, object> PopupViewForCheck()
        {
            return new Dictionary<string, object>
            {
                ["ready"] = BattleReady,
                ["showing"] = _confirm.activeInHierarchy,
                ["active"] = _visibleConfirmation?.Key ?? string.Empty,
                ["text"] = _confirmText.text,
                ["pending"] = _confirmationQueue?.PendingCount ?? 0
            };
        }
    }
}
#endif
