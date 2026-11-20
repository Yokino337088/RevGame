using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>虚拟列表的一次行绑定。外部异步结果必须确认绑定代次，回收时框架会撤销绑定。</summary>
    public sealed class RevUIVirtualRow
    {
        /// <summary>仅在行确实发出异步工作时保存取消源。</summary>
        private RevCancellationTokenSource _cancellation;
        /// <summary>绑定方返回的事件、租约等收尾回调。</summary>
        private Action _unbind;

        /// <summary>当前复用的行实例。</summary>
        public RectTransform Root { get; }
        /// <summary>正在显示的数据键；闲置时为空。</summary>
        public string Key { get; private set; }
        /// <summary>正在显示的索引；未绑定时为 -1。</summary>
        public int Index { get; private set; } = -1;
        /// <summary>每次解绑更新的版本号，异步完成时和此值比较。</summary>
        public int BindingVersion { get; private set; }

        /// <summary>由列表创建，直至退出列表寿命才销毁实例。</summary>
        internal RevUIVirtualRow(RectTransform root)
        {
            Root = root;
        }

        /// <summary>当前绑定的可选取消令牌。同步绑定无需获取。</summary>
        public RevCancellationToken GetCancellationToken()
        {
            if (Index < 0)
            {
                throw new InvalidOperationException("空闲行不能发起绑定任务。");
            }
            _cancellation ??= new RevCancellationTokenSource();
            return _cancellation.Token;
        }

        /// <summary>确认结果属于当前仍绑定的这一行。</summary>
        public bool IsCurrent(int version)
        {
            return Index >= 0 && BindingVersion == version && Root != null;
        }

        /// <summary>先设置新数据身份和位置，供绑定方安装事件。</summary>
        internal void Begin(int index, string key, float height)
        {
            Index = index;
            Key = key;
            Vector2 position = Root.anchoredPosition;
            position.y = -height * index;
            Root.anchoredPosition = position;
        }

        /// <summary>保存这一次绑定的清理函数。</summary>
        internal void SaveCleanup(Action unbind)
        {
            _unbind = unbind;
        }

        /// <summary>先使旧身份失效，再取消任务、拆订阅并隐藏行。</summary>
        internal void Unbind()
        {
            BindingVersion++;
            Index = -1;
            Key = null;
            _cancellation?.Cancel();
            _cancellation = null;
            Action cleanup = _unbind;
            _unbind = null;
            try
            {
                cleanup?.Invoke();
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "虚拟列表行清理失败", "UI");
            }
            if (Root != null)
            {
                Root.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>固定行距的竖向虚拟列表。数据方提交稳定键的只读快照，列表只维护视口与缓冲范围内的实例。</summary>
    public sealed class RevUIVirtualList<T> : IDisposable
    {
        /// <summary>视口内的绑定数据和真实行实例。</summary>
        private sealed class Slot
        {
            /// <summary>池里复用的行。</summary>
            internal RevUIVirtualRow Row;
            /// <summary>当前绘制的只读数据，用于跳过相同的重复刷新。</summary>
            internal T Item;
        }

        /// <summary>玩家滚轮、触摸和惯性滚动统一走这里。</summary>
        private readonly ScrollRect _scroll;
        /// <summary>视口节点，尺寸以本地 UI 单位计。</summary>
        private readonly RectTransform _viewport;
        /// <summary>内容根节点，需要顶部锚定。</summary>
        private readonly RectTransform _content;
        /// <summary>作为原型的行，运行期间保持隐藏。</summary>
        private readonly RectTransform _template;
        /// <summary>还原列表构造前的模板可见性。</summary>
        private readonly bool _templateWasActive;
        /// <summary>单行步进距离（含空隙），单位为视口本地 UI 单位。</summary>
        private readonly float _rowHeight;
        /// <summary>视口两侧各额外保留的行数。</summary>
        private readonly int _overscan;
        /// <summary>业务为数据提供稳定且唯一的键。</summary>
        private readonly Func<T, string> _key;
        /// <summary>绑定行，返回供本次解绑调用的清理；纯绘制可返回 null。</summary>
        private readonly Func<RevUIVirtualRow, T, Action> _bind;
        /// <summary>正在占用的索引到行。滚动时只遍历视口窗口。</summary>
        private readonly Dictionary<int, Slot> _active = new Dictionary<int, Slot>();
        /// <summary>待复用的空闲行，无仍绑定的业务身份。</summary>
        private readonly Stack<Slot> _idle = new Stack<Slot>();
        /// <summary>回收索引的复用缓冲，避免滚动时分配临时集合。</summary>
        private readonly List<int> _leaving = new List<int>();
        /// <summary>在下一次 SetItems 前有效的显示快照。</summary>
        private IReadOnlyList<T> _items = Array.Empty<T>();
        /// <summary>防止 ScrollRect 通知或绑定函数重入绘制。</summary>
        private bool _refreshing;
        /// <summary>停止监听和资源回收之后，所有后续刷新都忽略。</summary>
        private bool _disposed;

        /// <summary>数据总行数，和实例数量独立。</summary>
        public int Count => _items.Count;
        /// <summary>当前正在绘制的行（含缓冲）。</summary>
        public int ActiveCount => _active.Count;
        /// <summary>逻辑上仍由列表持有的克隆数量（含空闲行）。</summary>
        public int InstanceCount => _active.Count + _idle.Count;
        /// <summary>视口顶部对应数据行，空列表时为 -1。</summary>
        public int FirstVisibleIndex => Count == 0 ? -1 : Mathf.Clamp(Mathf.FloorToInt(Offset / _rowHeight), 0, Count - 1);
        /// <summary>顶部滚动偏移，弹性超滚按有效数据范围处理。</summary>
        private float Offset => Mathf.Clamp(_content.anchoredPosition.y, 0, Mathf.Max(0, Count * _rowHeight - _viewport.rect.height));

        /// <summary>接管指定 ScrollRect。模板需为顶部锚定的矩形，内容根使用顶部拉伸布局。</summary>
        public RevUIVirtualList(ScrollRect scroll, RectTransform template, float rowHeight, Func<T, string> key,
            Func<RevUIVirtualRow, T, Action> bind, int overscan = 1)
        {
            if (scroll == null || scroll.content == null || template == null || !scroll.vertical
                || scroll.horizontal || template.parent != scroll.content)
            {
                throw new ArgumentException("虚拟列表需要竖向 ScrollRect、内容根和内容下的行模板。");
            }
            if (rowHeight <= 0 || float.IsInfinity(rowHeight) || float.IsNaN(rowHeight) || overscan < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rowHeight));
            }
            _scroll = scroll;
            _content = scroll.content;
            _viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            _template = template;
            _templateWasActive = template.gameObject.activeSelf;
            _rowHeight = rowHeight;
            _overscan = overscan;
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _bind = bind ?? throw new ArgumentNullException(nameof(bind));

            _template.gameObject.SetActive(false);
            _content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 0);
            _scroll.onValueChanged.AddListener(OnScroll);
        }

        /// <summary>提交新快照。优先保留顶部数据键；若该键已删除，就保持原索引并裁到有效范围。</summary>
        public void SetItems(IReadOnlyList<T> items)
        {
            if (_disposed)
            {
                return;
            }
            if (_refreshing)
            {
                throw new InvalidOperationException("行绑定期间不能同步替换列表数据。");
            }
            items ??= Array.Empty<T>();
            if (!ReferenceEquals(items, _items))
            {
                int previousIndex = FirstVisibleIndex;
                string previousKey = previousIndex >= 0 ? _key(_items[previousIndex]) : null;
                float withinRow = previousIndex >= 0 ? Offset - previousIndex * _rowHeight : 0;
                _items = items;
                _content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Count * _rowHeight);

                int anchor = Mathf.Clamp(previousIndex, 0, Mathf.Max(0, Count - 1));
                if (previousKey != null && Count > 0 && _key(_items[anchor]) != previousKey)
                {
                    for (int i = 0; i < Count; i++)
                    {
                        if (_key(_items[i]) == previousKey)
                        {
                            anchor = i;
                            break;
                        }
                    }
                }
                Vector2 position = _content.anchoredPosition;
                position.y = Mathf.Clamp(anchor * _rowHeight + withinRow, 0, Mathf.Max(0, Count * _rowHeight - _viewport.rect.height));
                if (position != _content.anchoredPosition)
                {
                    _scroll.StopMovement();
                    _content.anchoredPosition = position;
                }
            }
            RefreshViewport();
        }

        /// <summary>滚动时只更新与视口相交的窗口。</summary>
        private void OnScroll(Vector2 position)
        {
            RefreshViewport();
        }

        /// <summary>布局或分辨率变化后调用，重新计算范围并回收超出新窗口容量的实例。</summary>
        public void RefreshViewport()
        {
            if (_disposed || _refreshing)
            {
                return;
            }
            _refreshing = true;
            try
            {
                float height = Mathf.Max(0, _viewport.rect.height);
                float offset = Offset;
                int first = Mathf.Max(0, Mathf.FloorToInt(offset / _rowHeight) - _overscan);
                int end = Mathf.Min(Count, Mathf.CeilToInt((offset + height) / _rowHeight) + _overscan);
                RecycleOutside(first, end);

                for (int i = first; i < end && !_disposed; i++)
                {
                    BindRow(i);
                }
                int capacity = Mathf.Min(Count, Mathf.CeilToInt(height / _rowHeight) + 1 + 2 * _overscan);
                while (!_disposed && InstanceCount > capacity && _idle.Count != 0)
                {
                    RevUIRoot.DestroyObject(_idle.Pop().Row.Root.gameObject);
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>调试和自动化可查看当前绑定实例。没有实例的索引应等待滚入视口。</summary>
        public bool TryGetRow(int index, out RevUIVirtualRow row)
        {
            bool found = _active.TryGetValue(index, out Slot slot);
            row = found ? slot.Row : null;
            return found;
        }

        /// <summary>将离开窗口的索引撤销绑定，并把实例交回空闲池。</summary>
        private void RecycleOutside(int first, int end)
        {
            _leaving.Clear();
            foreach (int index in _active.Keys)
            {
                if (index < first || index >= end)
                {
                    _leaving.Add(index);
                }
            }
            for (int i = 0; i < _leaving.Count && !_disposed; i++)
            {
                int index = _leaving[i];
                Slot slot = _active[index];
                _active.Remove(index);
                slot.Item = default;
                _idle.Push(slot);
                slot.Row.Unbind();
            }
        }

        /// <summary>同键同值跳过绘制；索引换人时先撤销旧事件，再绑定这一项。</summary>
        private void BindRow(int index)
        {
            T item = _items[index];
            string key = _key(item);
            if (_active.TryGetValue(index, out Slot slot))
            {
                if (slot.Row.Key == key && EqualityComparer<T>.Default.Equals(slot.Item, item))
                {
                    return;
                }
                slot.Row.Unbind();
            }
            else
            {
                slot = _idle.Count > 0 ? _idle.Pop() : new Slot { Row = new RevUIVirtualRow(UnityEngine.Object.Instantiate(_template, _content, false)) };
                _active.Add(index, slot);
            }
            if (_disposed)
            {
                return;
            }
            slot.Item = item;
            slot.Row.Begin(index, key, _rowHeight);
            Action cleanup;
            try
            {
                cleanup = _bind(slot.Row, item);
            }
            catch
            {
                if (!_disposed)
                {
                    _active.Remove(index);
                    slot.Item = default;
                    _idle.Push(slot);
                    slot.Row.Unbind();
                }
                throw;
            }
            if (_disposed)
            {
                cleanup?.Invoke();
                return;
            }
            slot.Row.SaveCleanup(cleanup);
            slot.Row.Root.gameObject.SetActive(true);
        }

        /// <summary>关闭列表时停止监听、解绑所有行并释放实例。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_scroll != null)
            {
                _scroll.onValueChanged.RemoveListener(OnScroll);
            }
            foreach (Slot slot in _active.Values)
            {
                slot.Row.Unbind();
                RevUIRoot.DestroyObject(slot.Row.Root == null ? null : slot.Row.Root.gameObject);
            }
            _active.Clear();
            while (_idle.Count != 0)
            {
                RectTransform rect = _idle.Pop().Row.Root;
                RevUIRoot.DestroyObject(rect == null ? null : rect.gameObject);
            }
            if (_template != null)
            {
                _template.gameObject.SetActive(_templateWasActive);
            }
            _items = Array.Empty<T>();
        }
    }
}
