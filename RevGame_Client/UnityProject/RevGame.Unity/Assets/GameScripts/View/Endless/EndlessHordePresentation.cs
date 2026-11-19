using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>
    /// 竞技场持有的外围敌人只读显示快照。逻辑提交变化时采样，渲染时只读位置补间，不修改敌人或结算。
    /// </summary>
    internal sealed class EndlessHordePresentation
    {
        /// <summary>同一槽位的一段显示轨迹。身份或位移断点变化时，轨迹以最新坐标重新开始。</summary>
        private struct Pose
        {
            /// <summary>最近一次提交的敌人数据。只供视图读外观、生命和目标坐标。</summary>
            internal EndlessHorde Current;
            /// <summary>本段插值起点，单位为棋格。</summary>
            internal Vector2 From;
            /// <summary>槽位的实体代次，防止清局或复用槽位时串用轨迹。</summary>
            internal int Generation;
            /// <summary>同一实体中最近一次瞬移或演出跳位的编号。</summary>
            internal int PositionCut;
        }

        /// <summary>按外围槽号复用的显示数据，容量由正式并发配置决定。</summary>
        private readonly Pose[] _slots;
        /// <summary>两次固定步之间的现实缩放秒数。慢速改阵缩小逻辑位移，采样间隔仍由同一个节拍驱动。</summary>
        private readonly float _interval;
        /// <summary>最近采样的敌潮组件。回池释放引用，换世界时所有敌人直接对齐。</summary>
        private EndlessHordeComponents _source;
        /// <summary>最近采样的局次对象，避免同一组件池被新局复用时沿用旧位置。</summary>
        private EndlessRun _run;
        /// <summary>最近采样的逻辑秒，用于判定是否提交了下一步和状态外观是否过期。</summary>
        private float _seconds;
        /// <summary>最近采样的阶段。跨暂停、结算和整备边界直接对齐确认坐标。</summary>
        private EndlessPhase _phase;
        /// <summary>最近读到的组件版本，覆盖同一个逻辑秒内的出生、掉血等提交。</summary>
        private uint _revision;
        /// <summary>当前插值段的起始缩放时间，和无尽计时器使用同一时间域。</summary>
        private double _startedAt;
        /// <summary>任一可显示槽仍有一段位移时为真。没有位移时，渲染帧无需连续刷新网格。</summary>
        private bool _moving;

        /// <summary>出池时按正式容量和固定步长分配一次，后续采样复用数组。</summary>
        internal EndlessHordePresentation(int capacity, float interval)
        {
            _slots = new Pose[capacity];
            _interval = interval;
        }

        /// <summary>有新提交才重建快照。晚到一帧时从当前画面应显示的位置续上，避免回跳到更旧的样本。</summary>
        internal bool Capture(EndlessHordeComponents source, EndlessRun run, double now)
        {
            bool sameWorld = ReferenceEquals(source, _source) && ReferenceEquals(run, _run);
            if (sameWorld && _revision == source.PresentationVersion && _seconds == run.seconds && _phase == run.phase)
            {
                return false;
            }

            bool continuous = sameWorld && _phase == EndlessPhase.Fighting && run.phase == EndlessPhase.Fighting &&
                run.seconds >= _seconds;
            bool advanced = continuous && run.seconds > _seconds;
            float oldBlend = Blend(now);
            int active = source.ActiveCount;
            int cursor = 0;
            _moving = false;
            // 活动槽按原顺序递增。清除消失槽的样本，不能让旧出生特效跟着新槽移动。
            for (int slot = 0; slot < _slots.Length; slot++)
            {
                ref Pose pose = ref _slots[slot];
                if (cursor >= active || source.ActiveSlot(cursor) != slot)
                {
                    pose.Current.active = false;
                    continue;
                }

                cursor++;
                EndlessHorde next = source[slot];
                int generation = source.Generation(slot);
                int positionCut = source.PositionCut(slot);
                Vector2 target = new Vector2(next.x, next.y);
                Vector2 from = target;
                if (continuous && pose.Current.active && pose.Current.id == next.id &&
                    pose.Generation == generation && pose.PositionCut == positionCut)
                {
                    if (advanced)
                    {
                        from = Position(slot, oldBlend);
                    }
                    else if (pose.Current.x == next.x && pose.Current.y == next.y)
                    {
                        // 同一步内只有外观或生命变化时，保留既有插值起点和播放时钟。
                        from = pose.From;
                    }
                }
                pose.Current = next;
                pose.From = from;
                pose.Generation = generation;
                pose.PositionCut = positionCut;
                _moving |= from.x != target.x || from.y != target.y;
            }

            if (advanced || !continuous)
            {
                _startedAt = now;
            }
            _source = source;
            _run = run;
            _revision = source.PresentationVersion;
            _seconds = run.seconds;
            _phase = run.phase;
            return true;
        }

        /// <summary>以本地缩放时间计算本段比例。补间达到终点就停住，等待下一次真实提交。</summary>
        internal float Blend(double now)
        {
            return _moving ? Mathf.Clamp01((float)((now - _startedAt) / _interval)) : 1f;
        }

        /// <summary>查看最近采样的槽位数据。返回只读引用，避免表现帧逐个复制组件。</summary>
        internal ref readonly EndlessHorde Read(int slot)
        {
            return ref _slots[slot].Current;
        }

        /// <summary>读取同一帧的显示位置。身体、头顶血条和出生动画共用这个坐标。</summary>
        internal Vector2 Position(int slot, float blend)
        {
            ref readonly Pose pose = ref _slots[slot];
            Vector2 target = new Vector2(pose.Current.x, pose.Current.y);
            return blend < 1f ? Vector2.LerpUnclamped(pose.From, target, blend) : target;
        }

        /// <summary>离开场景时释放本局引用。数组留给下一位竞技场租用者，首次采样会完整覆盖。</summary>
        internal void Reset()
        {
            _source = null;
            _run = null;
            _moving = false;
        }
    }
}
