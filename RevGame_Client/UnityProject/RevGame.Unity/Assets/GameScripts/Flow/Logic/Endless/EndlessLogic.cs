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
        /// <summary>后台时保存已交付的可玩局，结算只补写失败文件，避免覆盖已归档的旧版存档。</summary>
        private void OnSuspending()
        {
            var service = RevServices.GetRequired<EndlessService>();
            if (RevServices.GetRequired<GameFlow>().IsTransitioning || service.Run == null)
            {
                return;
            }
            if (service.Run.phase == EndlessPhase.Result)
            {
                service.RetryResultSave();
                return;
            }
            if (service.Run.phase == EndlessPhase.Fighting)
            {
                service.Pause();
            }
            service.Save();
        }
    }
}
