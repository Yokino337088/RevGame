using System;
using UnityEngine;

namespace Revolution
{
    /// <summary>一次开窗的有序阶段。省略的阶段耗时为零。</summary>
    public enum RevUIOpenStage
    {
        /// <summary>等待预制体资源可用。</summary>
        Resource,
        /// <summary>资源就绪，在主线程装配队列等待。</summary>
        Queued,
        /// <summary>创建或取回面板实例。</summary>
        Instance,
        /// <summary>装配控件、生命周期和初始数据。</summary>
        Binding,
        /// <summary>准备首屏需要的内容。</summary>
        FirstScreen,
        /// <summary>发布新面板并移交输入。</summary>
        Commit,
        /// <summary>播放入场转场。</summary>
        Reveal
    }

    /// <summary>面板请求是否完成，所有合并的等待者读取同一结果。</summary>
    public enum RevUIOpenStatus
    {
        /// <summary>仍在开窗。</summary>
        Pending,
        /// <summary>已交付面板。</summary>
        Succeeded,
        /// <summary>打开失败，具体阶段见票据。</summary>
        Failed,
        /// <summary>主动关闭、退域或超时终止。</summary>
        Cancelled,
        /// <summary>被互斥组内的新请求取代。</summary>
        Superseded
    }

    /// <summary>有界开窗记录。没有 Unity 对象，等待结束后仍能读取阶段和旧页回收状态。</summary>
    public sealed class RevUIOpenTicket
    {
        /// <summary>阶段累计毫秒数，阶段结束时写入。</summary>
        private readonly double[] _elapsed = new double[(int)RevUIOpenStage.Reveal + 1];
        /// <summary>完成源仅在管理器撤下请求后发布。</summary>
        private readonly RevTaskCompletionSource<RevUIOpenStatus> _completion = RevTask<RevUIOpenStatus>.CreateSource();
        /// <summary>当前阶段起始的实时时钟，单位秒。</summary>
        private double _since = Time.realtimeSinceStartupAsDouble;

        /// <summary>一次实际开窗的标识。合并的请求共享该值。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>面板逻辑资源键。</summary>
        public string PanelKey { get; }
        /// <summary>正在进行或最后执行的阶段。</summary>
        public RevUIOpenStage Stage { get; private set; }
        /// <summary>未结束时为 Pending。</summary>
        public RevUIOpenStatus Status { get; private set; }
        /// <summary>供测试或排障使用的稳定结果原因。</summary>
        public string Reason { get; private set; } = string.Empty;
        /// <summary>首次内容准备是否已通过；成功复用现有面板时也为真。</summary>
        public bool FirstScreenReady { get; internal set; }
        /// <summary>请求对新页资源的处置。常见值为 Pending、Panel、Released 或 None。</summary>
        public string ResourceDisposition { get; internal set; } = "None";
        /// <summary>成功交割时取代的旧页键，首次打开时为空。</summary>
        public string PreviousPanelKey { get; internal set; } = string.Empty;
        /// <summary>旧页退场进度。回池为 Pooled，销毁为 Released；被取回前淘汰会从 Pooled 更新为 Released。</summary>
        public string PreviousDisposition { get; internal set; } = "None";

        /// <summary>由管理器在接单时创建，立即开始记录资源阶段。</summary>
        internal RevUIOpenTicket(string panelKey)
        {
            PanelKey = panelKey;
        }

        /// <summary>可重复等待同一终态。观察者用面板键或原类型从 RevUI 读取已交付界面。</summary>
        public RevTask<RevUIOpenStatus> WaitAsync()
        {
            return _completion.Task;
        }

        /// <summary>读取指定阶段的壁钟毫秒数，正在执行的阶段包含已花费的时间。</summary>
        public double DurationMs(RevUIOpenStage stage)
        {
            return _elapsed[(int)stage] + (Status == RevUIOpenStatus.Pending && Stage == stage
                ? (Time.realtimeSinceStartupAsDouble - _since) * 1000d : 0d);
        }

        /// <summary>先关闭上一阶段的计时，再进入新的阶段。</summary>
        internal void Advance(RevUIOpenStage stage)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            _elapsed[(int)Stage] += (now - _since) * 1000d;
            Stage = stage;
            _since = now;
        }

        /// <summary>收尾后发布终态，防止迟到回调覆盖已有结果。</summary>
        internal void Finish(RevUIOpenStatus status, string reason)
        {
            if (Status != RevUIOpenStatus.Pending)
            {
                return;
            }
            Advance(Stage);
            Status = status;
            Reason = reason;
            _completion.SetResult(status);
        }
    }
}
