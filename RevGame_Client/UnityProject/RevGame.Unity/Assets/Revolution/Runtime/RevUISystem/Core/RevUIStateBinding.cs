// RevUIStateBinding.cs — 将 UGUI 字段和操作绑定到同一份业务快照。
// 变更后才写控件，发送前重新读取许可。关窗时由 RevUIPanel 自动解除本轮接线。
using System;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Revolution
{
    /// <summary>一轮面板的快照接线。使用面板的 CreateStateBinding 自动持有及清理。</summary>
    public sealed class RevUIStateBinding<T> : IDisposable
    {
        /// <summary>每次刷新调用一次，返回同一轮投影的业务快照。</summary>
        private readonly Func<T> _snapshot;
        /// <summary>按声明顺序执行的字段更新，闭包各自保存上一值。</summary>
        private readonly List<Action<T>> _apply = new List<Action<T>>();
        /// <summary>面板关闭时释放的监听器。本轮登记逆序清理。</summary>
        private readonly List<Action> _detach = new List<Action>();
        /// <summary>关闭界面时取消等待，网络等不可取消工作可以自行完成。</summary>
        private readonly RevScopedOperations<bool> _commands = new RevScopedOperations<bool>();
        /// <summary>本次打开的绑定是否已经回收。</summary>
        private bool _closed;

        /// <summary>由面板创建并按打开代次持有。</summary>
        internal RevUIStateBinding(Func<T> snapshot)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        /// <summary>登记字段投影。首次和字段值变化时写入目标控件。</summary>
        public void Bind<TValue>(Func<T, TValue> select, Action<TValue> apply, IEqualityComparer<TValue> comparer = null)
        {
            EnsureOpen();
            if (select == null || apply == null)
            {
                throw new ArgumentException("字段接线需要选择器和写入器。");
            }
            comparer ??= EqualityComparer<TValue>.Default;
            TValue last = default;
            bool initialized = false;
            _apply.Add(snapshot =>
            {
                TValue value = select(snapshot);
                if (!initialized || !comparer.Equals(last, value))
                {
                    apply(value);
                    last = value;
                    initialized = true;
                }
            });
        }

        /// <summary>在指定框架事件后重新读取快照。面板关闭时自动移除订阅。</summary>
        public void Observe(string eventName)
        {
            EnsureOpen();
            RevEvent.AddEventListener(eventName, Refresh, owner: this);
        }

        /// <summary>
        /// 给按钮安装异步命令。点击前使用最新快照复核权限，在途期间锁同一按钮。
        /// 返回 false 仍会刷新最新数据，异常进入指定显示入口。业务可在执行函数内继续检查权威状态。
        /// </summary>
        public void BindCommand(string key, Button button, Func<T, bool> allowed,
            Func<T, RevCancellationToken, RevTask<bool>> execute, Action<Exception> failed = null, float timeoutSeconds = 0)
        {
            EnsureOpen();
            if (button == null || allowed == null || execute == null || string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("按钮命令需要有效控件、键、许可和处理函数。");
            }
            bool busy = false;
            Bind(value => !busy && allowed(value), enabled =>
            {
                if (button != null)
                {
                    button.interactable = enabled;
                }
            });

            // 监听本轮面板；解绑动作保存相同委托实例，重复打开不会积累监听。
            UnityAction listener = () => ClickAsync().Forget();
            button.onClick.AddListener(listener);
            _detach.Add(() =>
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(listener);
                }
            });

            /// <summary>读取最新许可并在调用业务前立即锁定，避免同帧连点重复提交。</summary>
            async RevTask ClickAsync()
            {
                if (_closed || busy || button == null || !button.IsInteractable())
                {
                    return;
                }
                try
                {
                    T current = _snapshot();
                    if (!allowed(current))
                    {
                        Refresh();
                        return;
                    }
                    busy = true;
                    Refresh();
                    await _commands.Run(key, token => execute(current, token), timeoutSeconds: timeoutSeconds);
                }
                catch (Exception error)
                {
                    if (!_closed && error is not RevOperationCanceledException)
                    {
                        if (failed == null)
                        {
                            RevLog.Exception(error, "界面命令失败：" + key, "RevUI");
                        }
                        else
                        {
                            RevUILog.Guard("界面命令错误处理", () => failed(error));
                        }
                    }
                }
                finally
                {
                    busy = false;
                    if (!_closed)
                    {
                        RevUILog.Guard("命令结束刷新", Refresh);
                    }
                }
            }
        }

        /// <summary>读取一次快照并交付字段。允许业务在已有刷新入口中调用。</summary>
        public void Refresh()
        {
            if (_closed)
            {
                return;
            }
            T snapshot = _snapshot();
            for (int i = 0; i < _apply.Count && !_closed; i++)
            {
                _apply[i](snapshot);
            }
        }

        /// <summary>禁止已关闭的面板再向旧代次登记接线。</summary>
        private void EnsureOpen()
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(RevUIStateBinding<T>));
            }
        }

        /// <summary>断开事件、按钮监听和在途命令，阻止迟到结果更新旧控件。</summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            RevEvent.RemoveAllByOwner(this);
            _commands.Dispose();
            for (int i = _detach.Count - 1; i >= 0; i--)
            {
                RevUILog.Guard("解绑界面投影", _detach[i]);
            }
            _detach.Clear();
            _apply.Clear();
        }
    }
}
