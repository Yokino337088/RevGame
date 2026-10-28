namespace RevGame.Core
{
    /// <summary>帮手只读值快照：坐标以棋盘格为单位，外圈可为-1/9；不持有棋子或Unity对象。</summary>
    public readonly struct HelperSnapshot
    {
        /// <summary>Active控制本回合可见；Outside表示位置在棋盘外，无敌资格来自非Piece身份而非这个标记。</summary>
        public readonly bool Active, Outside;
        /// <summary>模型坐标，转换世界坐标仅在视图进行。</summary>
        public readonly BoardPos Position;
        /// <summary>当前存活敌棋Id与积分档位，目标0表示没有可攻击敌人。</summary>
        public readonly int TargetId, Tier;
        /// <summary>重量级FSM状态名与最近动作短台词，只读用于HUD/GM，不接受外部直接切换。</summary>
        public readonly string State, Speech;
        public HelperSnapshot(bool active, bool outside, BoardPos position, int targetId, int tier, string state, string speech)
        {
            Active = active; Outside = outside; Position = position; TargetId = targetId;
            Tier = tier; State = state; Speech = speech;
        }
    }

    /// <summary>积分消耗与真实扣血完成后才发出；飞行动画绝不能延迟扣血或再扣一次积分。</summary>
    public readonly struct HelperShotInfo
    {
        /// <summary>真实攻击发生时的起终点，敌人随后死亡/移动不改变这条视觉快照。</summary>
        public readonly BoardPos Source, Target;
        /// <summary>目标Id、章节、发射颗数与本次已扣能源，纯值不保存Unity或Piece引用。</summary>
        public readonly int TargetId, Chapter, Projectiles, Cost;
        /// <summary>持续蓝束或章节积分弹幕，用于选当前已租用的共享材质。</summary>
        public readonly bool Beam;
        public HelperShotInfo(BoardPos source, BoardPos target, int targetId, int chapter, int projectiles, int cost, bool beam)
        {
            Source = source; Target = target; TargetId = targetId; Chapter = chapter;
            Projectiles = projectiles; Cost = cost; Beam = beam;
        }
    }
}
