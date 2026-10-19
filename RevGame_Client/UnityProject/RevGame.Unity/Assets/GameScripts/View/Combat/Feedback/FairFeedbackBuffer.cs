// ============================================================
// FairFeedbackBuffer.cs —— 给有限的表现名额排队，避免“先落下的棋子永远抢到动画”。
//
// 【为什么需要它】战斗规则按固定名单结算；简单的“满了就拒绝后来者”会永久偏爱名单前面的棋子。
// 本类只保存值类型快照，优先照顾最近没有显示过的来源；它不攻击、不扣血，也不创建 Unity 对象。
// BoardView 独占这份缓冲，回合/场景结束 Clear；容量固定，过期反馈直接舍弃，绝不补播旧战况。
// ============================================================
using System.Collections.Generic;

namespace RevGame.View
{
    /// <summary>
    /// 有界、公平的表现候选缓冲。T 应当是只包含编号、坐标等值的快照，不能保存 Piece 或 GameObject。
    /// 选择规则是“最久没有获得显示名额者优先”；同分按来源编号稳定选择，方便固定输入重放。
    /// 不依赖 Unity 时间：调用者显式传入同一时间域的秒数，因此可用普通 C# 程序验证调度行为。
    /// </summary>
    internal sealed class FairFeedbackBuffer<T>
    {
        /// <summary>只保存当前有效的候选，List 的初始容量就是硬上限；不会随齐射数量无限扩容。</summary>
        private readonly List<Entry> _entries;
        /// <summary>本回合来源最后一次拿到显示名额的秒数；不保存棋子引用，Clear 时一起清除。</summary>
        private readonly Dictionary<int, float> _lastDisplayedAt = new Dictionary<int, float>(81);
        /// <summary>允许保留的候选总数；攻击口号传 12，弹道传 24，保持原来的手机表现预算。</summary>
        private readonly int _capacity;
        /// <summary>每颗棋子的待播数上限；口号只留最新一条，弹道可留少量连射，避免把齐射变成单发。</summary>
        private readonly int _perSourceLimit;
        /// <summary>一次反馈最多等多久，单位秒；只筛掉过期画面，不影响已经结算的攻击与伤害。</summary>
        private readonly float _lifetime;

        /// <summary>候选是纯数据；CreatedAt 指这次真实攻击发生的时刻，不能在显示时重新开始计时。</summary>
        private struct Entry
        {
            public int SourceId;
            public T Snapshot;
            public float CreatedAt;
        }

        /// <summary>当前待播候选数，始终不超过构造时的容量；可能包含尚未轮到清理的过期项。</summary>
        public int Count => _entries.Count;

        /// <summary>由一个 BoardView 创建并持有；参数是明确的表现预算，非法参数立即报出原因。</summary>
        public FairFeedbackBuffer(int capacity, int perSourceLimit, float lifetime)
        {
            if (capacity < 1 || perSourceLimit < 1 || perSourceLimit > capacity)
            {
                throw new System.ArgumentOutOfRangeException(nameof(capacity), "表现容量必须大于零，每来源上限必须在容量以内");
            }
            if (float.IsNaN(lifetime) || float.IsInfinity(lifetime) || lifetime <= 0f)
            {
                throw new System.ArgumentOutOfRangeException(nameof(lifetime), "表现候选有效期必须是有限的正秒数");
            }
            _capacity = capacity;
            _perSourceLimit = perSourceLimit;
            _lifetime = lifetime;
            _entries = new List<Entry>(capacity);
        }

        /// <summary>
        /// 登记一次刚刚真实发生的表现。返回 false 仅代表此次没有取得有限显示名额，不能回滚伤害。
        /// 同来源达到待播上限时更新其最新快照；队满时比较“谁更久没显示”，允许后来者替换近期常显示者。
        /// sourceId=0 表示旧接口/环境事件没有来源编号：仍受总容量限制，但不错误地把全部环境弹道合并为一发。
        /// </summary>
        public bool Offer(int sourceId, T snapshot, float now)
        {
            RemoveExpired(now);
            var incoming = new Entry { SourceId = sourceId, Snapshot = snapshot, CreatedAt = now };
            if (sourceId > 0)
            {
                int count = 0;
                int newest = -1;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].SourceId != sourceId) { continue; }
                    count++;
                    if (newest < 0 || _entries[i].CreatedAt > _entries[newest].CreatedAt) { newest = i; }
                }
                if (count >= _perSourceLimit)
                {
                    // 保留少量真实连射即可；不能让一颗高攻速棋子塞满全场队列，挤掉新落下的一星棋子。
                    _entries[newest] = incoming;
                    return true;
                }
            }
            if (_entries.Count < _capacity)
            {
                _entries.Add(incoming);
                return true;
            }

            int worst = 0;
            for (int i = 1; i < _entries.Count; i++)
            {
                if (HasHigherPriority(_entries[worst].SourceId, _entries[i].SourceId)) { worst = i; }
            }
            if (!HasHigherPriority(sourceId, _entries[worst].SourceId)) { return false; }
            // ★ 满队时不按“谁先来”永久封死入口。已播过的早期棋子优先让位，尚未播过的棋子逐批获得名额。
            _entries[worst] = incoming;
            return true;
        }

        /// <summary>
        /// 找出当前最应显示且同来源冷却已结束的候选，但暂不移除。
        /// 全局预算不足时调用者可直接等待；不会像旧代码一样把整队 Dequeue 后全部丢掉。
        /// 某颗棋子仍在冷却时略过它，继续找其它棋子，避免队头阻塞整个队伍。
        /// </summary>
        public bool TryPeek(float now, float sourceInterval, out T snapshot, out int sourceId)
        {
            RemoveExpired(now);
            int best = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry candidate = _entries[i];
                float last = LastDisplayed(candidate.SourceId);
                if (now - last < sourceInterval) { continue; }
                if (best < 0 || HasHigherPriority(candidate.SourceId, _entries[best].SourceId)
                    || (candidate.SourceId == _entries[best].SourceId && candidate.CreatedAt < _entries[best].CreatedAt))
                {
                    best = i;
                }
            }
            if (best < 0)
            {
                snapshot = default(T);
                sourceId = 0;
                return false;
            }
            snapshot = _entries[best].Snapshot;
            sourceId = _entries[best].SourceId;
            return true;
        }

        /// <summary>
        /// 只有显示预约成功后才提交：移除这颗来源最早的一份候选，并记住它已经获得过名额。
        /// 异步加载是否成功仍由 BoardView 收尾；本类不会保存资源句柄或参与对象池生命周期。
        /// </summary>
        public void MarkDisplayed(int sourceId, float now)
        {
            int oldest = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].SourceId != sourceId) { continue; }
                if (oldest < 0 || _entries[i].CreatedAt < _entries[oldest].CreatedAt) { oldest = i; }
            }
            if (oldest < 0) { return; }
            _entries.RemoveAt(oldest);
            _lastDisplayedAt[sourceId] = now;
        }

        /// <summary>回合或场景退出时由所有者调用；候选和公平性历史一起清掉，新回合从相同起点开始。</summary>
        public void Clear()
        {
            _entries.Clear();
            _lastDisplayedAt.Clear();
        }

        /// <summary>倒序删除不会漏过相邻的过期项，容器容量保留；过期项不再抢占任何显示名额。</summary>
        private void RemoveExpired(float now)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (now - _entries[i].CreatedAt > _lifetime) { _entries.RemoveAt(i); }
            }
        }

        /// <summary>没显示过用负无穷表示，因此天然排在已显示来源之前，不需要为每次攻击创建额外状态对象。</summary>
        private float LastDisplayed(int sourceId)
        {
            float last;
            return _lastDisplayedAt.TryGetValue(sourceId, out last) ? last : float.NegativeInfinity;
        }

        /// <summary>只比较公平性；不看星级、攻击力或落子顺序，同分编号只用于保持固定输入下的确定性。</summary>
        private bool HasHigherPriority(int sourceId, int otherId)
        {
            float last = LastDisplayed(sourceId);
            float otherLast = LastDisplayed(otherId);
            return last < otherLast || (last == otherLast && sourceId < otherId);
        }
    }
}
