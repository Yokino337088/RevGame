using RevGame.Core;
using RevGame.Flow;
using Revolution;
namespace RevGame.Endless
{
    /// <summary>无尽固定节拍，归专用域。过场门禁与服务阶段共同控制推进，不修改全局Time.timeScale。</summary>
    [RevLogic(GameDomain.Endless)]
    public sealed class EndlessLogic : RevLogicBase
    {
        private float _step;
        protected override void OnStart()
        {
            _step = EndlessConfig.Value("SimulationStep");
            Every(_step, Step);
            RevEvent.AddEventListener(RevAppLifecycle.Paused, OnSuspending, owner: this);
            RevEvent.AddEventListener(RevAppLifecycle.Quitting, OnSuspending, owner: this);
        }
        private void Step()
        {
            if (!RevServices.GetRequired<GameFlow>().IsGameplayBlocked)
            {
                RevServices.GetRequired<EndlessService>().Tick(_step);
            }
        }
        protected override void OnStop()
        {
            RevEvent.RemoveAllByOwner(this);
            // 正常返回前保存由按钮显式决定；退出不覆盖已经写入的完整快照。
            RevServices.Get<EndlessService>()?.Dispose();
        }
        /// <summary>切后台先冻结并保存，回来后由玩家继续；不依赖移动端不可靠的退出回调。</summary>
        private void OnSuspending()
        {
            var service = RevServices.GetRequired<EndlessService>();
            if (service.Run.phase == EndlessPhase.Fighting)
            {
                service.Pause();
            }
            service.Save();
        }
    }
}
