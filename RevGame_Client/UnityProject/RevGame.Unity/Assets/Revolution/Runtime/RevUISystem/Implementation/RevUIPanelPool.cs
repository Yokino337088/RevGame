// ============================================================
// RevUIPanelPool.cs —— 面板实例池（UI 专用，不套用通用对象池）
//
// 位置：Runtime\RevUISystem\Implementation\
//
// 【为什么不用框架里那个 RevPool】
//   王者文档（《03-UI框架UI4.0》）里有一条用血换来的结论：**form 不要用通用 GameObjectPool**。
//   因为界面"复用"要重置的东西比一个普通游戏对象多得多 ——
//   控件引用、事件监听、数据绑定、滚动位置、页签选中、输入框内容、倒计时……
//   通用池只懂"失活/激活"，这些它一概不知道。所以这里做一个"知道自己在池化的是界面"的专用池：
//     · 取出时**必须**走面板的 OnReuse（框架保证这个钩子一定被调用，业务只要重写它）；
//     · 关闭时优先回池（KeepAlive），池满了或声明 DestroyOnClose 就真销毁。
//
// 【池子里存的是"实例"，不是"资源"】
//   面板实例占着一份 prefab 的资源引用（创建时 LoadAsync 换来的）。
//   实例留在池里 → 引用留着 → 下次打开不重新加载 ✔；
//   实例被销毁 → 管理器负责把那一次引用还掉 ✔（一一配对，不会漏也不会多还）。
//
// 【上限】每个面板最多缓存 RevUISetting.MaxCachedPanels 个（默认 1）——
//   一个界面同时只会开一份，缓存 1 个就够了。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Revolution
{
    /// <summary>面板空闲实例池（内部按面板键分桶）</summary>
    internal sealed class RevUIPanelPool
    {
        private readonly Dictionary<string, List<RevUIPanel>> _idle =
            new Dictionary<string, List<RevUIPanel>>(StringComparer.Ordinal);

        /// <summary>空闲实例总数</summary>
        public int Count { get; private set; }

        /// <summary>有几种面板进了池</summary>
        public int KindCount => _idle.Count;

        /// <summary>某个面板现在池里有几个空闲实例</summary>
        public int IdleCountOf(string key)
            => key != null && _idle.TryGetValue(key, out List<RevUIPanel> list) ? list.Count : 0;

        /// <summary>
        /// 关闭一个面板：失活后放进池子。
        /// ★ 只做"存"这件事 —— OnReuse（清残留）由管理器在**取出时**调用，
        ///   因为清理时机越贴近"再次打开"越安全（从关闭到再打开之间，业务仍可能读到它）。
        /// </summary>
        public bool Put(RevUIPanel panel)
        {
            if (panel == null || panel.gameObject == null || panel.PanelKey == null) return false;

            panel.gameObject.SetActive(false);

            if (!_idle.TryGetValue(panel.PanelKey, out List<RevUIPanel> list))
            {
                list = new List<RevUIPanel>(1);
                _idle[panel.PanelKey] = list;
            }

            list.Add(panel);
            Count++;
            return true;
        }

        /// <summary>外部销毁实例时从池索引中摘掉它，避免池里留下 Unity 假 null 引用。</summary>
        public bool Forget(RevUIPanel panel)
        {
            if (ReferenceEquals(panel, null) || panel.PanelKey == null || !_idle.TryGetValue(panel.PanelKey, out List<RevUIPanel> list))
                return false;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(list[i], panel)) continue;
                list.RemoveAt(i);
                Count--;
                if (list.Count == 0) _idle.Remove(panel.PanelKey);
                return true;
            }

            return false;
        }

        /// <summary>取一个空闲实例（没有则返回 null，由管理器走"新建"路径）</summary>
        public RevUIPanel Take(string key)
        {
            if (key == null || !_idle.TryGetValue(key, out List<RevUIPanel> list) || list.Count == 0) return null;

            // 从尾部取：最近放进去的最"新鲜"（和对象池的 LIFO 一个道理）
            int last = list.Count - 1;
            RevUIPanel panel = list[last];
            list.RemoveAt(last);
            Count--;

            if (list.Count == 0) _idle.Remove(key);
            return panel;
        }

        /// <summary>池子满没满（满了就不再回池，直接销毁）</summary>
        public bool IsFull(string key) => IdleCountOf(key) >= RevUISetting.MaxCachedPanels;

        /// <summary>把所有空闲实例倒出来（回登录 / 退出时销毁用），并清空池</summary>
        public List<RevUIPanel> Drain()
        {
            var all = new List<RevUIPanel>(Count);
            foreach (KeyValuePair<string, List<RevUIPanel>> pair in _idle)
                all.AddRange(pair.Value);

            _idle.Clear();
            Count = 0;
            return all;
        }

        /// <summary>只清索引（进 Play / 关场景后，那些实例已经随场景没了）</summary>
        public void Clear()
        {
            _idle.Clear();
            Count = 0;
        }

        /// <summary>池快照（诊断面板 / 排查内存用）</summary>
        public string DumpStats()
        {
            if (Count == 0) return "实例池：空";

            var sb = new StringBuilder();
            sb.Append("实例池：").Append(Count).Append(" 个空闲实例，").Append(KindCount).Append(" 种面板\n");

            foreach (KeyValuePair<string, List<RevUIPanel>> pair in _idle)
                sb.Append("  ").Append(pair.Key).Append(" × ").Append(pair.Value.Count).Append('\n');

            return sb.ToString();
        }
    }
}
