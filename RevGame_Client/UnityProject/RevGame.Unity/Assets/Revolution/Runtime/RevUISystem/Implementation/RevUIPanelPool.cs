using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Revolution
{
    /// <summary>面板空闲实例池。按面板键查找，按回池先后淘汰，由管理器负责实例和租约的释放。</summary>
    internal sealed class RevUIPanelPool
    {
        /// <summary>一轮空闲期的实例和归属快照。面板离开池后这个快照失效。</summary>
        internal readonly struct IdlePanel
        {
            /// <summary>本轮空闲期对应的面板实例。</summary>
            internal readonly RevUIPanel Panel;
            /// <summary>入池时登记的实际域句柄，用于隔离同名域的不同生命周期。</summary>
            internal readonly RevScopeHandle Scope;
            /// <summary>面板预制体的完整逻辑键，用于空闲索引和诊断。</summary>
            internal readonly string Key;
            /// <summary>入池时的实例 ID，面板被外部销毁后仍可用于诊断。</summary>
            internal readonly int InstanceId;
            /// <summary>入池时距启动的实时时间，单位秒。</summary>
            internal readonly double SinceSeconds;

            /// <summary>面板完成退场且归属仍有效时，由池记录本轮空闲信息。</summary>
            internal IdlePanel(RevUIPanel panel, RevScopeHandle scope)
            {
                Panel = panel;
                Scope = scope;
                Key = panel.PanelKey;
                InstanceId = panel.GetInstanceID();
                SinceSeconds = Time.realtimeSinceStartupAsDouble;
            }
        }

        /// <summary>按面板键定位空闲节点，每桶从最旧到最新。</summary>
        private readonly Dictionary<string, List<LinkedListNode<IdlePanel>>> _byKey =
            new Dictionary<string, List<LinkedListNode<IdlePanel>>>(StringComparer.Ordinal);

        /// <summary>整池的使用顺序，头部是最早结束使用的空闲实例。</summary>
        private readonly LinkedList<IdlePanel> _oldestFirst = new LinkedList<IdlePanel>();

        /// <summary>当前空闲实例数，活动和退场中的实例不占额度。</summary>
        internal int Count => _oldestFirst.Count;

        /// <summary>供主线程诊断即时遍历的空闲实例，旧实例排在前面。</summary>
        internal IEnumerable<IdlePanel> OldestFirst => _oldestFirst;

        /// <summary>判断同键空闲实例是否已达到单类型额度，非正额度关闭该缓存。</summary>
        internal bool IsFull(string key)
        {
            return RevUISetting.MaxCachedPanels <= 0 ||
                (_byKey.TryGetValue(key, out var entries) && entries.Count >= RevUISetting.MaxCachedPanels);
        }

        /// <summary>登记已经关闭并失活的面板。调用者先检查域和额度，入池不触发 Unity 回调。</summary>
        internal void Put(RevUIPanel panel, RevScopeHandle scope)
        {
            var entry = new IdlePanel(panel, scope);
            if (!_byKey.TryGetValue(entry.Key, out var entries))
            {
                entries = new List<LinkedListNode<IdlePanel>>(1);
                _byKey.Add(entry.Key, entries);
            }
            entries.Add(_oldestFirst.AddLast(entry));
        }

        /// <summary>取出同域最新的实例，同时摘除该键的过期或异域实例。管理器在索引稳定后释放摘出的实例。</summary>
        internal RevUIPanel Take(string key, RevScopeHandle scope, out List<IdlePanel> discarded)
        {
            discarded = null;
            if (!_byKey.TryGetValue(key, out var entries))
            {
                return null;
            }

            RevUIPanel result = null;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                LinkedListNode<IdlePanel> node = entries[i];
                IdlePanel entry = node.Value;
                // 用实际域确认缓存期间未被业务重新登记或解绑。
                if (entry.Panel != null && scope != null && entry.Scope == scope && scope.IsOpen &&
                    RevScope.Core.FindOwnerOf(entry.Panel) == scope)
                {
                    if (result == null)
                    {
                        result = entry.Panel;
                        Remove(node);
                    }
                    continue;
                }

                (discarded ??= new List<IdlePanel>()).Add(entry);
                Remove(node);
            }
            return result;
        }

        /// <summary>收走指定域的全部空闲实例，随后由管理器统一处理销毁和归还引用。</summary>
        internal List<IdlePanel> RemoveScope(RevScopeHandle scope)
        {
            List<IdlePanel> removed = null;
            for (LinkedListNode<IdlePanel> node = _oldestFirst.First; node != null;)
            {
                LinkedListNode<IdlePanel> next = node.Next;
                if (node.Value.Scope == scope)
                {
                    (removed ??= new List<IdlePanel>()).Add(node.Value);
                    Remove(node);
                }
                node = next;
            }
            return removed;
        }

        /// <summary>按最少最近使用顺序摘除超额实例，保留最新的 limit 份。</summary>
        internal List<IdlePanel> RemoveOldestUntil(int limit)
        {
            if (Count <= limit)
            {
                return null;
            }
            var removed = new List<IdlePanel>(Count - limit);
            while (Count > limit)
            {
                LinkedListNode<IdlePanel> node = _oldestFirst.First;
                removed.Add(node.Value);
                Remove(node);
            }
            return removed;
        }

        /// <summary>外部销毁时移除空闲索引。使用引用身份，以便处理 Unity 假空对象。</summary>
        internal void Forget(RevUIPanel panel)
        {
            if (ReferenceEquals(panel, null) || panel.PanelKey == null || !_byKey.TryGetValue(panel.PanelKey, out var entries))
            {
                return;
            }
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(entries[i].Value.Panel, panel))
                {
                    Remove(entries[i]);
                    return;
                }
            }
        }

        /// <summary>从键索引与全局顺序同时摘除一个节点。两份索引必须始终包含相同实例。</summary>
        private void Remove(LinkedListNode<IdlePanel> node)
        {
            string key = node.Value.Key;
            List<LinkedListNode<IdlePanel>> entries = _byKey[key];
            entries.Remove(node);
            if (entries.Count == 0)
            {
                _byKey.Remove(key);
            }
            _oldestFirst.Remove(node);
        }

        /// <summary>清空空闲索引。根重建前由管理器归还旧租约。</summary>
        internal void Clear()
        {
            _byKey.Clear();
            _oldestFirst.Clear();
        }

        /// <summary>汇总当前各面板类型的空闲数，供框架诊断输出。</summary>
        internal string DumpStats()
        {
            if (Count == 0)
            {
                return "实例池：空";
            }

            var result = new StringBuilder();
            result.Append("实例池：").Append(Count).Append(" 个空闲实例，").Append(_byKey.Count).Append(" 种面板\n");
            foreach (var pair in _byKey)
            {
                result.Append("  ").Append(pair.Key).Append(" × ").Append(pair.Value.Count).Append('\n');
            }
            return result.ToString();
        }
    }
}
