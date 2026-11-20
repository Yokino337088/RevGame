using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>面板实例的一条只读缓存记录，供框架和开发工具定位持有原因。</summary>
    public readonly struct RevUICacheEntry
    {
        /// <summary>面板预制体的完整逻辑键。</summary>
        public string PanelKey { get; }
        /// <summary>该面板的 Unity 实例 ID，可对照可见控件及其他开窗记录。</summary>
        public int InstanceId { get; }
        /// <summary>面板回池或处置时的域名。未归域时为空。</summary>
        public string Scope { get; }
        /// <summary>域进入时的单调时钟刻度。可区分同名域，未归域时为 0。</summary>
        public long ScopeEnteredTicks { get; }
        /// <summary>取得本实例的最近一次开窗 ID。</summary>
        public Guid OpenId { get; }
        /// <summary>保留或回收的原因代码。</summary>
        public string Reason { get; }
        /// <summary>记录产生时距启动的实时时间，单位秒。</summary>
        public double AtSeconds { get; }
        /// <summary>查询空闲状态时或回收后，预制体该代缓存的实际总引用数，包含其他使用者。</summary>
        public int PrefabRefCount { get; }
        /// <summary>记录产生时，本轮关闭是否已完成入池。</summary>
        public bool WasIdle { get; }
        /// <summary>实例是否已归还自己的预制体租约。</summary>
        public bool LeaseReleased { get; }

        /// <summary>复制纯诊断数据。结果不保留实例、资源句柄或域对象。</summary>
        internal RevUICacheEntry(string key, int instanceId, RevScopeHandle scope, Guid openId,
            string reason, double atSeconds, int prefabRefCount, bool wasIdle, bool leaseReleased)
        {
            PanelKey = key;
            InstanceId = instanceId;
            Scope = scope?.Name;
            ScopeEnteredTicks = scope?.EnteredTicks ?? 0;
            OpenId = openId;
            Reason = reason;
            AtSeconds = atSeconds;
            PrefabRefCount = prefabRefCount;
            WasIdle = wasIdle;
            LeaseReleased = leaseReleased;
        }
    }

    /// <summary>主线程查询生成的面板缓存快照，包含当前额度、本会话查池统计和最近处置。</summary>
    public sealed class RevUICacheSnapshot
    {
        /// <summary>当前全局空闲额度，0 表示关闭缓存。</summary>
        public int MaxIdlePanels { get; }
        /// <summary>当前单面板类型空闲额度。</summary>
        public int MaxCachedPanels { get; }
        /// <summary>本次会话在实例池取到同域面板的次数。</summary>
        public int Hits { get; }
        /// <summary>本次会话在实例池未取到面板的次数，包含预制体缓存命中。</summary>
        public int Misses { get; }
        /// <summary>本次会话按缓存额度、归属或低内存原因直接处置的实例总数。</summary>
        public int TotalReleased { get; }
        /// <summary>当前空闲实例，最久未使用的排在开头。</summary>
        public IReadOnlyList<RevUICacheEntry> Idle { get; }
        /// <summary>最近 32 条直接处置记录，由早到晚排列。</summary>
        public IReadOnlyList<RevUICacheEntry> RecentReleases { get; }

        /// <summary>保存本次查询读到的数值和只读记录，之后的开关窗不会更改本快照。</summary>
        internal RevUICacheSnapshot(int hits, int misses, int totalReleased, RevUICacheEntry[] idle, RevUICacheEntry[] recentReleases)
        {
            MaxIdlePanels = RevUISetting.MaxIdlePanels;
            MaxCachedPanels = RevUISetting.MaxCachedPanels;
            Hits = hits;
            Misses = misses;
            TotalReleased = totalReleased;
            Idle = Array.AsReadOnly(idle);
            RecentReleases = Array.AsReadOnly(recentReleases);
        }
    }
}
