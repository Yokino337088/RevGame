using RevGame.Core;
using Revolution;

namespace RevGame.Pvp
{
    /// <summary>网络轮询归PvP域；回首页即停止并关闭Socket。用不缩放的真实时钟，单机暂停不会让网络超时失效。</summary>
    [RevLogic(GameDomain.Pvp)]
    public sealed class PvpLogic : RevLogicBase
    {
        protected override void OnTick(float deltaTime)
        {
            RevServices.GetRequired<PvpService>().Poll();
        }
        protected override void OnStop()
        {
            RevServices.Get<PvpService>()?.Dispose();
        }
    }
}
