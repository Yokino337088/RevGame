// ============================================================
// IRevUIPartHost.cs —— Part 的宿主契约
//
// 位置：Runtime\RevUISystem\Interfaces\
//
// 【为什么 Part 只认这个接口，而不是直接认 RevUIPanel】
//   Part 要能被**多个面板复用**（王者的 From/Part 就是靠这个解耦：同一个商店页签单元
//   给背包、商城、活动三个面板用）。如果 Part 里写 `BagPanel host`，它就再也不能给
//   别的面板用了 —— 所以 Part 只持有这个接口，只能做"任何宿主都支持"的三件事。
//
// 【Part 与宿主 / Part 与 Part 的通信纪律】
//   · Part → 宿主：调 RequestClose() / NotifyPartChanged() / 通过宿主暴露的方法；
//   · Part → Part：**不直接耦合**，经宿主中转（NotifyPartChanged() → 宿主的 OnPartChanged）；
//   · 宿主 → Part：宿主直接调 Part 的公开方法（宿主当然知道自己有哪些 Part）。
//   这条纪律来自王者的《05-完整链路与事件系统》：界面之间不直接通信，否则依赖会织成一张网。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>Part（可复用 UI 单元）的宿主：面板自己实现它，Part 只依赖它</summary>
    public interface IRevUIPartHost
    {
        /// <summary>Part 该挂在哪个节点下（通常就是宿主面板的根节点）</summary>
        Transform PartRoot { get; }

        /// <summary>宿主当前是否已打开（Part 用它决定"要不要立刻刷新"）</summary>
        bool IsHostOpened { get; }

        /// <summary>Part 想"返回/关掉自己这一层"时调用（宿主决定是真关面板还是只切页签）</summary>
        void RequestClose();

        /// <summary>
        /// Part 有变化、需要宿主协调（或需要通知别的 Part）时调用。
        /// ★ 这是"Part 之间不直接耦合"的中转口：具体怎么处理由宿主的 <c>OnPartChanged</c> 决定。
        /// </summary>
        void NotifyPartChanged(RevUIPart part);
    }
}
