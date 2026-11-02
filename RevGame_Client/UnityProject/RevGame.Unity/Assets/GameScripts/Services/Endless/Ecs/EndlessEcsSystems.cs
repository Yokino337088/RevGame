namespace RevGame.Endless
{
    /// <summary>无尽系统调度：明确读写顺序，死亡提交晚于本步攻击，表现层不参与结算。</summary>
    public static class EndlessEcsSystems
    {
        /// <summary>生成和移动改变坐标后失效索引，首次攻击查询重建一次，后续共用。</summary>
        public static void Movement(EndlessService service, float step)
        {
            service.UpdateDirector(step);
            service.UpdateHorde(step);
            service.World.InvalidateSpatial();
        }
        /// <summary>各演员攻击/领主技能、气眼武器与持续地面效果按旧顺序执行，不改变随机源顺序。</summary>
        public static void Combat(EndlessService service, float step)
        {
            service.UpdateUnits(step);
            service.UpdateEyes(step);
            service.UpdateBurnZones();
        }
        public static void Death(EndlessService service)
        {
            service.ResolveDeaths();
        }
    }
}
