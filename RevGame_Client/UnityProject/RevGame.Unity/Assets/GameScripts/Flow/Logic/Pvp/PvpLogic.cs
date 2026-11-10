using RevGame.Core;
using Revolution;

namespace RevGame.Pvp
{
    /// <summary>网络轮询归PvP域；回首页即停止并关闭Socket。用不缩放的真实时钟，单机暂停不会让网络超时失效。</summary>
    [RevLogic(GameDomain.Pvp)]
    public sealed class PvpLogic : RevLogicBase
    {
#if UNITY_STANDALONE || UNITY_EDITOR
        // 进入 PvP 之前的桌面后台运行设置，离开 PvP 域时原样归还。
        private bool _backgroundBefore;
        // 桌面后台仍持续轮询网络。实际角色预测由 HUD 生命周期管理。
        protected override void OnStart()
        {
            // 同机双窗口和切到设置/服务端控制台时仍要发送心跳与推进Agent；不能把失焦当作暂停联机。
            _backgroundBefore = UnityEngine.Application.runInBackground;
            UnityEngine.Application.runInBackground = true;
        }
#endif
        // 当前 PvP 域每帧驱动一次。网络时钟由服务自管，不使用此帧的可缩放 deltaTime。
        protected override void OnTick(float deltaTime)
        {
            RevServices.GetRequired<PvpService>().Poll();
            // 先轮询网络拿到权威邀请，再由统一流程切表现场景。PvP 域继续持有原连接。
            RevServices.GetRequired<RevGame.Flow.GameFlow>().TryEnterPvpDuel();
        }
        // 退出 PvP 域后释放连接、原生副本和预测沙盒。
        protected override void OnStop()
        {
            RevServices.Get<PvpService>()?.Dispose();
            RevDataTableManager.Unload<PvpYayaLineTable>();
#if UNITY_STANDALONE || UNITY_EDITOR
            // 只在本模式生命周期内改变桌面后台策略，退出归还原设置，不干涉移动端系统挂起。
            UnityEngine.Application.runInBackground = _backgroundBefore;
#endif
        }
    }
}
