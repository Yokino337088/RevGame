using RevGame.Core;
using Revolution;

namespace RevGame.Pvp
{
    /// <summary>网络轮询归PvP域；回首页即停止并关闭Socket。用不缩放的真实时钟，单机暂停不会让网络超时失效。</summary>
    [RevLogic(GameDomain.Pvp)]
    public sealed class PvpLogic : RevLogicBase
    {
#if UNITY_STANDALONE || UNITY_EDITOR
        private bool _backgroundBefore;
        protected override void OnStart()
        {
            // 同机双窗口和切到设置/服务端控制台时仍要发送心跳与推进Agent；不能把失焦当作暂停联机。
            _backgroundBefore = UnityEngine.Application.runInBackground;
            UnityEngine.Application.runInBackground = true;
        }
#endif
        protected override void OnTick(float deltaTime)
        {
            RevServices.GetRequired<PvpService>().Poll();
        }
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
