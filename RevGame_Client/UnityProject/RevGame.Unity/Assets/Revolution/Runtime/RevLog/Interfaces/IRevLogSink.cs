// ============================================================
// IRevLogSink.cs —— 输出通道契约
//
// 位置：Runtime\RevLog\Interfaces\
//
// 【★ 门面不写文件，只交给 Sink】
//   王者原版的门面自己不写文件，只发事件 —— 好处是"新增输出通道（上报/网络/自定义面板）
//   不改门面"。这里用的是它的升级版：**Sink 列表**（王者抽离方案里的改造点），
//   而不是单一事件（事件只能挂一个、还容易漏挂）。
//
//   所以：
//     · 控制台  = RevConsoleSink（Unity 下由钩子自动装上，零配置）
//     · 写文件  = RevFileSink（业务显式 AddSink，因为"要不要落盘"是产品决策）
//     · 上报    = 你自己实现一个 Sink，或订阅 RevLog.OnReport（更轻）
//
// 【约定】Sink 抛异常一律被 RevLog 隔离（日志系统绝不能让宿主崩）——
//   但"第几次开始持续失败"会被记下来并可查（见 RevLog.SinkErrors），不静默。
// ============================================================

namespace Revolution
{
    /// <summary>输出通道：把一条日志送到某个地方（控制台 / 文件 / 上报 / 面板）。</summary>
    public interface IRevLogSink
    {
        /// <summary>名字（日志/自检用，例如 "Console" / "File"）。</summary>
        string Name { get; }

        /// <summary>写一条。<b>实现里不要抛异常排队等事</b>：慢活请自己丢后台（参考 RevFileSink）。</summary>
        void Write(in RevLogEntry entry);

        /// <summary>把还在路上的日志落下去（关服/切场景/崩溃前调）。</summary>
        void Flush();

        /// <summary>收尾（关文件句柄、停线程）。调用后本 Sink 不应再被使用。</summary>
        void Dispose();
    }
}
