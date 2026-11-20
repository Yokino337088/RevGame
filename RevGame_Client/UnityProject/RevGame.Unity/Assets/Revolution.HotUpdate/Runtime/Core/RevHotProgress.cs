// ============================================================
// RevHotProgress.cs —— 进度（状态机 + 一次上报的数据 + 聚合器）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【为什么要节流】
//   下载循环是"每帧"跑的，如果每次都把进度对象发出去（再让 UI 刷新文本），会白耗很多帧；
//   聚合器做了 120ms 节流：UI 拿到的进度足够顺滑，日志也不会刷屏。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Revolution.HotUpdate
{
    /// <summary>热更进行到哪一步了（业务可以根据它切 UI 文案）。</summary>
    public enum RevHotState
    {
        /// <summary>还没开始。</summary>
        Idle = 0,

        /// <summary>正在拉取/比对清单。</summary>
        Checking = 1,

        /// <summary>正在下载差量文件。</summary>
        Downloading = 2,

        /// <summary>正在校验（尺寸 / hash）。</summary>
        Verifying = 3,

        /// <summary>正在应用更新（写版本标记 / 切版本指针 / 清理旧版本）。</summary>
        Applying = 4,

        /// <summary>就绪（当前资源版本可用）。</summary>
        Ready = 5,

        /// <summary>失败（LastError 里有人话与分类）。</summary>
        Failed = 6,
    }

    /// <summary>一次进度上报（给 loading 条用的全部信息）。</summary>
    public sealed class RevHotProgress
    {
        /// <summary>当前阶段。</summary>
        public RevHotState State = RevHotState.Idle;

        /// <summary>正在处理第几个文件（从 1 开始）。</summary>
        public int FileIndex;

        /// <summary>总共要处理多少个文件。</summary>
        public int FileCount;

        /// <summary>正在处理的文件名（排查"卡在哪个包"全靠它）。</summary>
        public string CurrentFile = "";

        /// <summary>已完成字节（含已完成的文件 + 当前文件已下载部分）。</summary>
        public long DownloadedBytes;

        /// <summary>总字节。</summary>
        public long TotalBytes;

        /// <summary>百分比 0~100（TotalBytes 为 0 时恒为 100）。</summary>
        public double Percent;

        /// <summary>下载速度（字节/秒，按两次上报的间隔算）。</summary>
        public double SpeedBytesPerSec;

        /// <summary>拼好的一句话，可直接放进 loading 条（"正在更新 3/12 · 12.4 MB / 48.1 MB · 2.3 MB/s"）。</summary>
        public string Text = "";

        /// <summary>字节数转人话（B / KB / MB / GB）。</summary>
        public static string FormatBytes(long bytes)
        {
            double value = bytes;
            if (bytes >= 1073741824L) return (value / 1073741824.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1048576L) return (value / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024L) return (value / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }
    }

    /// <summary>
    /// 进度聚合器：把"N 个文件 + 当前文件的内嵌进度"折成一个 RevHotProgress，
    /// 按固定频率回调业务（120ms 节流）。
    /// </summary>
    internal sealed class RevHotProgressAggregator
    {
        private readonly RevHotState _phase;
        private readonly long _totalBytes;
        private readonly int _fileCount;
        private readonly Action<RevHotProgress> _sink;
        private readonly string _logTag;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReportMs = long.MinValue;
        private long _lastBytes;
        private double _speed;

        private long _doneBytes;
        private string _currentFile = "";
        private int _fileIndex;
        private RevHotProgress _last;

        // ★ 进行中文件的字节按"文件名"分账：并发工人（最多 8 个）同时下载时，
        //   单字段互相覆盖会把进度显示成"只有最后一个工人的字节"（约 1/N），每完成一个文件才跳一截。
        //   RevTask 是主线程模型，所有调用都在主线程 —— 无需加锁。
        private readonly Dictionary<string, long> _activeBytes = new Dictionary<string, long>(8);

        public RevHotProgressAggregator(RevHotState phase, long totalBytes, int fileCount, Action<RevHotProgress> sink, string logTag)
        {
            _phase = phase;
            _totalBytes = totalBytes;
            _fileCount = fileCount;
            _sink = sink;
            _logTag = logTag;
        }

        /// <summary>开始处理某个文件（name 用于分账与"卡在哪个包"排查；并发时显示最后开始的一个）。</summary>
        public void BeginFile(string name)
        {
            _fileIndex++;
            _currentFile = name;
            _activeBytes[name] = 0;
            Report(true);
        }

        /// <summary>某个进行中文件已下载/已处理的字节（由下载循环每帧按名字喂进来）。</summary>
        public void SetFileBytes(string name, long bytes)
        {
            if (_activeBytes.ContainsKey(name) == false) return;   // 保险：完成后的迟到上报直接忽略
            _activeBytes[name] = bytes;
            Report(false);
        }

        /// <summary>一个文件彻底完成（尺寸参数用于累计；名字用于把它的"进行中"账清掉）。</summary>
        public void CompleteFile(string name, long size)
        {
            _activeBytes.Remove(name);
            _doneBytes += size;
            Report(true);
        }

        private void Report(bool force)
        {
            if (_sink == null) return;
            long now = _clock.ElapsedMilliseconds;

            // 节流：非强制上报时，两次间隔至少 120ms（loading 条 8 帧/秒足够顺滑）
            if (force == false && now - _lastReportMs < 120) return;

            // ★ 先存上一次的上报时刻，再更新 —— 速度 = 字节增量 / 两次上报的间隔。
            //   （旧实现先覆盖 _lastReportMs 再算 dt，dt 恒为 0，速度永远显示 0。）
            long lastMs = _lastReportMs;
            _lastReportMs = now;

            long active = 0;
            foreach (KeyValuePair<string, long> kv in _activeBytes) active += kv.Value;
            long done = _doneBytes + active;
            double percent = _totalBytes <= 0 ? 100.0 : done * 100.0 / _totalBytes;

            long dt = now - lastMs;
            if (dt > 0)
            {
                double inst = (done - _lastBytes) * 1000.0 / dt;
                // 平滑一下（50/50），避免瞬时抖动让速度数字来回跳
                _speed = lastMs == long.MinValue ? inst : (_speed + inst) * 0.5;
            }

            _lastBytes = done;

            RevHotProgress progress = new RevHotProgress();
            progress.State = _phase;
            progress.FileIndex = _fileIndex;
            progress.FileCount = _fileCount;
            progress.CurrentFile = _currentFile;
            progress.DownloadedBytes = done;
            progress.TotalBytes = _totalBytes;
            progress.Percent = percent > 100 ? 100 : percent;
            progress.SpeedBytesPerSec = _speed;
            progress.Text = BuildText(done, percent);
            _last = progress;

            _sink(progress);
        }

        private string BuildText(long done, double percent)
        {
            var sb = new StringBuilder(96);
            switch (_phase)
            {
                case RevHotState.Downloading:
                    sb.Append("正在更新 ").Append(_fileIndex).Append('/').Append(_fileCount);
                    sb.Append(" · ").Append(RevHotProgress.FormatBytes(done)).Append(" / ").Append(RevHotProgress.FormatBytes(_totalBytes));
                    if (_speed > 0) sb.Append(" · ").Append(RevHotProgress.FormatBytes((long)_speed)).Append("/s");
                    break;

                case RevHotState.Verifying:
                    sb.Append("正在校验 ").Append(_currentFile).Append(" · ").Append(percent.ToString("F0")).Append('%');
                    break;

                case RevHotState.Checking:
                    sb.Append("正在检查更新…");
                    break;

                case RevHotState.Applying:
                    sb.Append("正在应用更新…");
                    break;

                default:
                    sb.Append(RevHotProgress.FormatBytes(done)).Append(" / ").Append(RevHotProgress.FormatBytes(_totalBytes));
                    break;
            }

            return sb.ToString();
        }

        /// <summary>最后一次上报（没上报过返回 null）。</summary>
        public RevHotProgress Last { get { return _last; } }
    }
}
