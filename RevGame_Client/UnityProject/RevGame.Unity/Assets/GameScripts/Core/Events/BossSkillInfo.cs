// ============================================================
// BossSkillInfo.cs —— 一次 Boss 技能的不可变快照（预警与命中两种事件共用）。
//
// 为什么用 struct 快照而不是把 Piece 传给表现层：
// 技能特效要播一两秒，这期间 Boss 可能被打死、瞬移或回合结束。
// 快照只保存“编号 + 坐标 + 数字”，表现层拿到的永远是发生那一刻的事实，
// 不会因为延迟播放去读一个已经回池或换位的棋子对象。
// 不引用任何 Unity 类型，纯 C# 离线测试也能直接断言事件内容。
// ============================================================
namespace RevGame.Core
{
    public readonly struct BossSkillInfo
    {
        /// <summary>施放技能的 Boss 棋子编号。表现层用它找到 Boss 的图像做蓄力动作。</summary>
        public readonly int BossId;
        /// <summary>技能种类，决定表现层播哪一套特效。</summary>
        public readonly BossSkillKind Kind;
        /// <summary>施放时 Boss 身体的左下角格子（瞬移技能里就是“出发点”）。</summary>
        public readonly BoardPos Origin;
        /// <summary>Boss 身体边长（1~3）。用于把特效放到整个身体的中心，而不是左下角那一格。</summary>
        public readonly int OriginSize;
        /// <summary>
        /// 技能的三个目标点，含义随技能而定：
        /// 狙击 = A 为目标棋子位置；轰炸 = A/B/C 三发炸弹落点；瞬移 = A 为落点（身体左下角）；
        /// 其他范围技能只用 Origin，A/B/C 保持与 Origin 相同。
        /// </summary>
        public readonly BoardPos A;
        public readonly BoardPos B;
        public readonly BoardPos C;
        /// <summary>伤害半径（格数）。0 表示单体技能。</summary>
        public readonly int Radius;
        /// <summary>蓄力秒数：预警事件里表示“还有多久命中”，表现层用它对齐倒计时动画。</summary>
        public readonly float Windup;
        /// <summary>多段技能里这是第几段（从 0 开始）；预警事件恒为 0。</summary>
        public readonly int Index;
        /// <summary>总段数（炸弹 3 发、激光 3 段…），表现层据此决定特效数量。</summary>
        public readonly int Count;
        /// <summary>Boss 是否已狂暴。狂暴后特效换成更危险的红色调。</summary>
        public readonly bool Enraged;

        public BossSkillInfo(int bossId, BossSkillKind kind, BoardPos origin, int originSize, BoardPos a, BoardPos b, BoardPos c, int radius, float windup, int index, int count, bool enraged)
        {
            BossId = bossId;
            Kind = kind;
            Origin = origin;
            OriginSize = originSize;
            A = a;
            B = b;
            C = c;
            Radius = radius;
            Windup = windup;
            Index = index;
            Count = count;
            Enraged = enraged;
        }

        /// <summary>复制一份只改“第几段”的快照：命中事件逐段派发时使用，其余字段保持预警时的计划。</summary>
        public BossSkillInfo WithIndex(int index)
        {
            return new BossSkillInfo(BossId, Kind, Origin, OriginSize, A, B, C, Radius, Windup, index, Count, Enraged);
        }

        /// <summary>瞬移命中时出发点已经变了，复制一份新的 Origin（A 仍是落点）。</summary>
        public BossSkillInfo WithOrigin(BoardPos origin)
        {
            return new BossSkillInfo(BossId, Kind, origin, OriginSize, A, B, C, Radius, Windup, Index, Count, Enraged);
        }
    }
}
