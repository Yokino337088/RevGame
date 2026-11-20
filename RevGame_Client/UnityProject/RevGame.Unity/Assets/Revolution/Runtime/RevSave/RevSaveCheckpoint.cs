// RevSaveCheckpoint.cs — 合并频繁变化后的保存请求，执行宿主声明的安全检查点。
// 生命周期事件只刷新已标脏的数据。失败保留脏标记并等待下一次明确触发。
using System;

namespace Revolution
{
    /// <summary>由业务或域持有的主线程保存检查点，可用文件、设置或测试存储作为后台。</summary>
    public sealed class RevSaveCheckpoint : IDisposable
    {
        /// <summary>业务提供的保存动作。失败应抛异常，成功表示快照已提交。</summary>
        private readonly Action _save;
        /// <summary>首次改动到合并刷新的非缩放秒数。</summary>
        private readonly float _delay;
        /// <summary>登记的延迟刷新，释放时停止。</summary>
        private RevTimerHandle _timer;
        /// <summary>有尚未保存的变化。</summary>
        private bool _dirty;
        /// <summary>正在保存时阻止事件触发重入。</summary>
        private bool _saving;
        /// <summary>释放后不再登记新工作。</summary>
        private bool _closed;

        /// <summary>建立自动刷新。订阅前台切后台及正常退出由宿主按数据语义选择。</summary>
        public RevSaveCheckpoint(Action save, float delaySeconds = 0.2f, bool flushOnPause = true)
        {
            _save = save ?? throw new ArgumentNullException(nameof(save));
            if (delaySeconds < 0 || float.IsNaN(delaySeconds) || float.IsInfinity(delaySeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(delaySeconds));
            }
            _delay = delaySeconds;
            if (flushOnPause)
            {
                RevEvent.AddEventListener(RevAppLifecycle.Paused, FlushAutomatically, owner: this);
                RevEvent.AddEventListener(RevAppLifecycle.Quitting, FlushAutomatically, owner: this);
            }
        }

        /// <summary>声明当前数据变动。多个同窗口修改由同一次刷新持久化。</summary>
        public void MarkDirty()
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(RevSaveCheckpoint));
            }
            _dirty = true;
            if (!_timer.IsAlive)
            {
                _timer = RevTimer.After(_delay, FlushAutomatically, RevTimeDomain.Unscaled, this);
                if (_timer.IsEmpty)
                {
                    // 计时器满载时直接提交本次变更，避免把即时保存悄悄降为仅在切后台才保存。
                    FlushAutomatically();
                }
            }
        }

        /// <summary>立即执行已登记的保存。异常交给业务决定是否暂停进度或重试。</summary>
        public void Flush()
        {
            if (_closed || !_dirty || _saving)
            {
                return;
            }
            _timer.Stop();
            _dirty = false;
            _saving = true;
            try
            {
                _save();
            }
            catch
            {
                _dirty = true;
                throw;
            }
            finally
            {
                _saving = false;
            }
        }

        /// <summary>自动触发时记录保存失败，保留待保存内容给下一次显式检查点。</summary>
        private void FlushAutomatically()
        {
            try
            {
                Flush();
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "保存检查点失败", "RevSave");
            }
        }

        /// <summary>关闭定时器和应用事件。需要提交的数据由宿主在释放前调用 Flush。</summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            _timer.Stop();
            RevEvent.RemoveAllByOwner(this);
        }
    }
}
