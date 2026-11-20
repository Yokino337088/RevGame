// ============================================================
// RevHotDownloader.cs —— 下载引擎（并发 / 重试退避 / 多源降级 / 断点续传 / 校验 / 原子落地）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Download\
//
// 【失败处理的三层结构】（从里到外）
//   TryDownloadOnceAsync ：一次尝试 = 断点续传下载 → 校验 → 原子提交；
//                          失败抛 RevHotException，其中"校验失败"会先删掉半成品并标记为可重试。
//   DownloadOneAsync     ：一个文件 = 换源（外层）× 重试退避（内层）；只保留身份匹配的同源 .part。
//   DownloadPlanAsync    ：整批 = N 个工人从共享游标领任务；任何失败 → 停止领新任务，等在跑的收尾后统一上抛。
//
// 【为什么"取消"要单独处理】
//   RevOperationCanceledException 是控制流：取消时既不能重试也不能换源，必须立刻向上传递
//   （并把 www 中止掉，否则下载会在后台继续烧流量）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Revolution;
using UnityEngine;
using UnityEngine.Networking;

namespace Revolution.HotUpdate
{
    /// <summary>下载引擎（纯静态：状态都在会话对象里，方便一次一清）。</summary>
    internal static class RevHotDownloader
    {
        // ============================================================
        // 对外一：拉取小文本（清单 / 内置清单）。多源 + 重试，返回文本。
        // ============================================================

        /// <summary>
        /// 拉取一段文本。urlBuilder 负责"给定源 → 拼出该源上的 URL"（换源重试时会对每个源都拼一次）。
        /// 全部失败抛 RevHotException(ManifestFetchFailed, 可重试)。
        /// </summary>
        public static async RevTask<string> FetchTextAsync(RevHotConfig config, Func<RevHotSource, string> urlBuilder, RevCancellationToken token)
        {
            List<RevHotSource> sources = RevHotSources.Build(config);
            if (sources.Count == 0)
            {
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.ConfigInvalid, "没有可用的下载源（RemoteRoot 未配置）"), false);
            }

            RevHotException last = null;
            for (int s = 0; s < sources.Count; s++)
            {
                string url = urlBuilder(sources[s]);

                for (int attempt = 0; attempt <= config.RetryCount; attempt++)
                {
                    token.ThrowIfCancelled();
                    try
                    {
                        return await FetchTextOnceAsync(url, config, token);
                    }
                    catch (RevOperationCanceledException)
                    {
                        throw;
                    }
                    catch (RevHotException e) when (e.Retryable == false)
                    {
                        throw;
                    }
                    catch (RevHotException e)
                    {
                        last = e;
                        RevLog.Warn("清单拉取失败（" + sources[s] + "，第 " + (attempt + 1) + " 次）：" + e.Error.Message, config.LogTag);
                        await BackoffDelayAsync(BackoffMs(config, attempt), token);
                    }
                }

                RevLog.Warn("下载源不可用，切换下一个：" + sources[s], config.LogTag);
            }

            throw last ?? new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestFetchFailed, "所有下载源都失败了（检查网络与 RemoteRoot 配置）"), true);
        }

        private static async RevTask<string> FetchTextOnceAsync(string url, RevHotConfig config, RevCancellationToken token)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(url))
            {
                ApplyHeaders(www, config);
                www.timeout = config.TimeoutSeconds;

                var op = www.SendWebRequest();
                while (op.isDone == false)
                {
                    token.ThrowIfCancelled();
                    await RevTask.Yield();
                }

                if (www.result != UnityWebRequest.Result.Success)
                {
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestFetchFailed, "清单拉取失败（" + www.error + "）", url), true);
                }

                string text = www.downloadHandler.text;
                if (string.IsNullOrEmpty(text))
                {
                    // 拿到空内容多半是"上传到一半的清单"——按可重试处理，换源 / 重试都可能拿到完整的
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ManifestFetchFailed, "清单内容为空", url), true);
                }

                return text;
            }
        }

        // ============================================================
        // 对外二：按差量计划下载全部文件
        // ============================================================

        /// <summary>
        /// 下载一个差量计划里的全部文件（并校验、原子落地）。
        /// 任何一个文件最终失败 → 抛 RevHotException；已完成的文件保留（下次从计划重算，hash 一致的自然跳过）。
        /// </summary>
        public static async RevTask DownloadPlanAsync(RevHotPlan plan, RevHotConfig config, string resVersion, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            List<WorkItem> items = BuildItems(plan, resVersion);
            if (items.Count == 0) return;

            var aggregator = new RevHotProgressAggregator(RevHotState.Downloading, plan.TotalBytes, items.Count, onProgress, config.LogTag);

            var session = new DownloadSession(items);
            int workers = Math.Min(Math.Max(config.Concurrency, 1), items.Count);

            var tasks = new RevTask[workers];
            for (int i = 0; i < workers; i++)
            {
                tasks[i] = WorkerLoopAsync(session, config, resVersion, aggregator, token);
            }

            await RevTask.WhenAll(tasks);

            // ★ 取消必须在这里显式上抛：WhenAll 只计数完成、不看异常 —— 工人抛出的
            //   RevOperationCanceledException 到这里已经丢了。不补抛的话，"取消下载"会
            //   被当成"更新成功"继续走完 stamp / 切版本，半截版本直接上线。
            if (session.Cancelled) throw new RevOperationCanceledException();

            // 全部工人结束后：有失败就上抛（第一个错误最有诊断价值）
            if (session.FirstError != null) throw session.FirstError;
        }

        // ============================================================
        // 工人与单文件
        // ============================================================

        /// <summary>共享任务队列：工人从游标领任务；一旦有失败，停止领新任务（做完手头的就收工）。</summary>
        private sealed class DownloadSession
        {
            public readonly List<WorkItem> Items;
            public readonly object Gate = new object();
            public RevHotException FirstError;
            public bool Stop;

            /// <summary>
            /// 取消专用标志（与 Stop 分开：Stop 复用于"失败也停"，
            /// 聚合处要区分"该报失败"还是"该报取消"—— RevTask 的 WhenAll 不传播异常，取消只能靠它带出来）。
            /// </summary>
            public bool Cancelled;

            private int _cursor;

            public DownloadSession(List<WorkItem> items)
            {
                Items = items;
            }

            /// <summary>领下一个任务（没有 / 已失败停止 → null）。</summary>
            public WorkItem Next()
            {
                lock (Gate)
                {
                    if (Stop || _cursor >= Items.Count) return null;
                    WorkItem item = Items[_cursor];
                    _cursor++;
                    return item;
                }
            }

            /// <summary>记录失败（只记第一个：它最有诊断价值）。</summary>
            public void Fail(RevHotException error)
            {
                lock (Gate)
                {
                    Stop = true;
                    if (FirstError == null) FirstError = error;
                }
            }
        }

        /// <summary>一个待下载文件（包或映射表）。</summary>
        private sealed class WorkItem
        {
            public bool IsResMap;
            public string Name;
            public long Size;
            public string Sha256;
            public string LocalPath;

            /// <summary>
            /// 用构造参数填字段（而不是对象初始化块 `new WorkItem { ... }`）：
            /// 这样调用处能写成一行，避免"括号后面换行"。
            /// </summary>
            public WorkItem(bool isResMap, string name, long size, string sha256, string localPath)
            {
                IsResMap = isResMap;
                Name = name;
                Size = size;
                Sha256 = sha256;
                LocalPath = localPath;
            }
        }

        private static List<WorkItem> BuildItems(RevHotPlan plan, string resVersion)
        {
            // 本地路径必须与 RevHotStore 的目录约定严格一致（直接用它的方法算，避免两处拼路径出现分歧）
            var items = new List<WorkItem>(plan.FileCount);

            Action<RevHotBundleInfo> addBundle = delegate(RevHotBundleInfo bundle)
            {
                string localPath = Path.Combine(RevHotStore.BundlesDir(resVersion), bundle.Name);
                items.Add(new WorkItem(false, bundle.Name, bundle.Size, bundle.Sha256, localPath));
            };

            for (int i = 0; i < plan.Added.Count; i++) addBundle(plan.Added[i]);
            for (int i = 0; i < plan.Changed.Count; i++) addBundle(plan.Changed[i]);

            if (plan.ResMapChanged)
            {
                items.Add(new WorkItem(true, plan.ResMap.Name, plan.ResMap.Size, plan.ResMap.Sha256, RevHotStore.ResMapPath(resVersion)));
            }

            return items;
        }

        private static async RevTask WorkerLoopAsync(DownloadSession session, RevHotConfig config, string resVersion, RevHotProgressAggregator progress, RevCancellationToken token)
        {
            while (true)
            {
                if (session.Stop) return;

                WorkItem item = session.Next();
                if (item == null) return;

                try
                {
                    await DownloadOneAsync(item, config, resVersion, progress, token);
                    progress.CompleteFile(item.Name, item.Size);
                }
                catch (RevOperationCanceledException)
                {
                    session.Cancelled = true;                  // ★ 先记账：这个异常到 WhenAll 聚合处会被吞，只能靠标志带出去
                    session.Stop = true;                       // 取消：让别的工人也别再领新任务
                    throw;
                }
                catch (RevHotException e)
                {
                    // 记失败 + 停止领新任务：继续下载剩下的没有意义（玩家看到的将是"更新失败，可重试"）
                    session.Fail(e);
                }
            }
        }

        private static async RevTask DownloadOneAsync(WorkItem item, RevHotConfig config, string resVersion, RevHotProgressAggregator progress, RevCancellationToken token)
        {
            progress.BeginFile(item.Name);

            List<RevHotSource> sources = RevHotSources.Build(config);
            RevHotException last = null;
            string partPath = RevHotStore.PartPath(item.LocalPath);

            for (int s = 0; s < sources.Count; s++)
            {
                token.ThrowIfCancelled();
                try
                {
                    // 首源允许跨启动续传；切源不拼接来自另一 CDN 的字节。
                    if (s > 0) ClearPart(partPath);
                }
                catch (IOException e)
                {
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, "清理旧下载分片失败：" + e.Message, partPath), false);
                }

                string url = item.IsResMap
                    ? RevHotUrlBuilder.ResMapUrl(config, sources[s].Root, resVersion)
                    : RevHotUrlBuilder.BundleUrl(config, sources[s].Root, resVersion, item.Name);

                for (int attempt = 0; attempt <= config.RetryCount; attempt++)
                {
                    token.ThrowIfCancelled();
                    try
                    {
                        await TryDownloadOnceAsync(item, resVersion, url, config, progress, token);
                        return;
                    }
                    catch (RevOperationCanceledException)
                    {
                        throw;
                    }
                    catch (RevHotException e) when (e.Retryable == false)
                    {
                        throw;
                    }
                    catch (RevHotException e)
                    {
                        last = e;
                        RevLog.Warn(item.Name + " 下载失败（" + sources[s] + "，第 " + (attempt + 1) + " 次）：" + e.Error.Message, config.LogTag);
                        if (attempt < config.RetryCount) await BackoffDelayAsync(BackoffMs(config, attempt), token);
                    }
                }

                RevLog.Warn(item.Name + " 在 " + sources[s] + " 全部重试失败，切换下一个源", config.LogTag);
            }

            throw last ?? new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, item.Name + " 下载失败（所有下载源都已尝试）"), true);
        }

        /// <summary>一次尝试：匹配分片身份 → 流式下载 → 校验 → 原子提交。</summary>
        private static async RevTask TryDownloadOnceAsync(WorkItem item, string resVersion, string url, RevHotConfig config, RevHotProgressAggregator progress, RevCancellationToken token)
        {
            string partPath = RevHotStore.PartPath(item.LocalPath);
            string dir = Path.GetDirectoryName(item.LocalPath);

            try
            {
                if (string.IsNullOrEmpty(dir) == false) Directory.CreateDirectory(dir);
                long offset = PreparePart(partPath, item, resVersion, url);

                // 上次可能刚下载完就退出：验证完整分片，不发送必然返回 416 的 Range 请求。
                if (offset == item.Size && File.Exists(partPath))
                {
                    try
                    {
                        await VerifyAndCommitAsync(partPath, item, config, token);
                        return;
                    }
                    catch (RevHotException e) when (e.Error.Code == RevHotErrorCode.VerifyFailed)
                    {
                        // 已满长度却校验失败，原有前缀不可信，立即从零重下。
                        offset = PreparePart(partPath, item, resVersion, url);
                    }
                }

                // 只有 Range 被无视 (200) 或已失效 (416) 时，允许本次尝试内重发一次完整 GET。
                for (int restart = 0; restart < 2; restart++)
                {
                    token.ThrowIfCancelled();
                    var handler = new RangeFileHandler(partPath, offset, item.Size);
                    using (UnityWebRequest www = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
                    {
                        handler.Attach(www);
                        www.downloadHandler = handler;
                        ApplyHeaders(www, config);
                        www.timeout = config.TimeoutSeconds;
                        if (offset > 0) www.SetRequestHeader("Range", "bytes=" + offset + "-");

                        var op = www.SendWebRequest();
                        try
                        {
                            while (op.isDone == false)
                            {
                                token.ThrowIfCancelled();
                                progress.SetFileBytes(item.Name, Math.Min(item.Size, offset + (long)www.downloadedBytes));
                                await RevTask.Yield();
                            }
                        }
                        finally
                        {
                            if (op.isDone == false) www.Abort();
                            handler.CloseFile();
                        }

                        if (offset > 0 && (www.responseCode == 200 || www.responseCode == 416))
                        {
                            ClearPart(partPath);
                            offset = PreparePart(partPath, item, resVersion, url);
                            progress.SetFileBytes(item.Name, 0);
                            continue;
                        }

                        if (handler.WriteError != null)
                        {
                            throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, "写入下载分片失败：" + handler.WriteError, partPath), false);
                        }

                        if (www.result != UnityWebRequest.Result.Success)
                        {
                            if (handler.InvalidResponse) ClearPart(partPath);
                            string reason = handler.InvalidResponse ? handler.Error : www.error;
                            throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, item.Name + " 下载失败（" + reason + "）", url), true);
                        }

                        if (handler.ValidateResponse() == false)
                        {
                            ClearPart(partPath);
                            throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, item.Name + " 响应无效（" + handler.Error + "）", url), true);
                        }

                        if (offset + handler.ReceivedBytes < item.Size)
                        {
                            // 合法但未传完的前缀仍可续传（有些 CDN 会返回短 206）。
                            throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, item.Name + " 响应内容未传完", url), true);
                        }
                    }

                    if (item.Size == 0 && File.Exists(partPath) == false)
                    {
                        using (File.Create(partPath)) { }
                    }

                    await VerifyAndCommitAsync(partPath, item, config, token);
                    return;
                }

                throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, item.Name + " 断点请求无法回退到完整下载", url), true);
            }
            catch (IOException e)
            {
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, "下载分片读写失败：" + e.Message, partPath), false);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, "下载分片无访问权限：" + e.Message, partPath), false);
            }
        }

        private static async RevTask VerifyAndCommitAsync(string partPath, WorkItem item, RevHotConfig config, RevCancellationToken token)
        {
            token.ThrowIfCancelled();
            try
            {
                await RevHotVerifier.VerifyAsync(partPath, item.Size, item.Sha256, config.VerifyMode, config, token);
            }
            catch (RevHotException e) when (e.Error.Code == RevHotErrorCode.VerifyFailed)
            {
                ClearPart(partPath);
                throw new RevHotException(e.Error, true);
            }

            token.ThrowIfCancelled();
            RevHotStore.CommitFile(partPath, item.LocalPath);
            RevHotStore.DeleteFile(partPath + ".meta");
        }

        /// <summary>身份与路径同时匹配才保留分片；写元数据在开始下载之前，取消时不会抹掉它。</summary>
        private static long PreparePart(string partPath, WorkItem item, string resVersion, string url)
        {
            string identity = string.Join("\n", new[]
            {
                "RevHotPart1", resVersion, item.Name, item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Sha256 ?? string.Empty, url
            });
            string metaPath = partPath + ".meta";
            bool canResume = File.Exists(partPath) && File.Exists(metaPath)
                && string.Equals(File.ReadAllText(metaPath), identity, StringComparison.Ordinal);
            long offset = canResume ? new FileInfo(partPath).Length : 0;
            if (!canResume || offset > item.Size || offset < 0)
            {
                ClearPart(partPath);
                File.WriteAllText(metaPath, identity);
                return 0;
            }

            return offset;
        }

        private static void ClearPart(string partPath)
        {
            if (File.Exists(partPath)) File.Delete(partPath);
            string metaPath = partPath + ".meta";
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }

        /// <summary>只在确认响应为完整 200 或起点/总长匹配的 206 后才将数据写入 .part。</summary>
        private sealed class RangeFileHandler : DownloadHandlerScript
        {
            private readonly string _partPath;
            private readonly long _offset;
            private readonly long _expectedSize;
            private UnityWebRequest _request;
            private FileStream _stream;
            private bool _checked;
            private long _rangeLength;
            private long _received;

            public bool RestartFromZero { get; private set; }
            public bool InvalidResponse { get; private set; }
            public string Error { get; private set; }
            public string WriteError { get; private set; }
            public long ReceivedBytes { get { return _received; } }

            public RangeFileHandler(string partPath, long offset, long expectedSize) : base(new byte[64 * 1024])
            {
                _partPath = partPath;
                _offset = offset;
                _expectedSize = expectedSize;
            }

            public void Attach(UnityWebRequest request)
            {
                _request = request;
            }

            public bool ValidateResponse()
            {
                if (_checked) return !InvalidResponse && !RestartFromZero;
                _checked = true;
                long status = _request.responseCode;
                if (status == 200 && _offset > 0)
                {
                    RestartFromZero = true;
                    Error = "服务器忽略 Range，必须重新下载";
                    return false;
                }

                if (status == 200 && _offset == 0)
                {
                    _rangeLength = _expectedSize;
                    return true;
                }

                if (status == 206 && _offset > 0 && ParseContentRange(_request.GetResponseHeader("Content-Range"), out long end))
                {
                    _rangeLength = end - _offset + 1;
                    string contentLength = _request.GetResponseHeader("Content-Length");
                    if (string.IsNullOrEmpty(contentLength) ||
                        (long.TryParse(contentLength, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out long length) && length == _rangeLength))
                    {
                        return true;
                    }
                }

                InvalidResponse = true;
                Error = "HTTP " + status + " 的 Content-Range 起点/总长与清单不符";
                return false;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                if (ValidateResponse() == false) return false;
                if (dataLength > _rangeLength - _received)
                {
                    InvalidResponse = true;
                    Error = "响应体超过声明的范围";
                    return false;
                }

                try
                {
                    if (_stream == null)
                    {
                        _stream = new FileStream(_partPath, _offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                    }

                    _stream.Write(data, 0, dataLength);
                    _received += dataLength;
                    return true;
                }
                catch (IOException e)
                {
                    WriteError = e.Message;
                    return false;
                }
                catch (UnauthorizedAccessException e)
                {
                    WriteError = e.Message;
                    return false;
                }
            }

            private bool ParseContentRange(string header, out long end)
            {
                end = 0;
                if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) return false;
                int dash = header.IndexOf('-', 6);
                int slash = header.IndexOf('/', dash + 1);
                if (dash <= 6 || slash <= dash + 1) return false;

                var style = System.Globalization.NumberStyles.None;
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                return long.TryParse(header.Substring(6, dash - 6), style, culture, out long start)
                    && long.TryParse(header.Substring(dash + 1, slash - dash - 1), style, culture, out end)
                    && long.TryParse(header.Substring(slash + 1), style, culture, out long total)
                    && start == _offset && total == _expectedSize && end >= start && end < total;
            }

            public void CloseFile()
            {
                if (_stream == null) return;
                try
                {
                    _stream.Dispose();
                }
                catch (IOException e)
                {
                    WriteError = e.Message;
                }
                finally
                {
                    _stream = null;
                }
            }
        }

        // ============================================================
        // 小工具
        // ============================================================

        /// <summary>把配置里的附加请求头套到请求上（临时令牌等）。</summary>
        private static void ApplyHeaders(UnityWebRequest www, RevHotConfig config)
        {
            if (config.ExtraHeaders == null) return;
            foreach (KeyValuePair<string, string> kv in config.ExtraHeaders)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                www.SetRequestHeader(kv.Key, kv.Value ?? string.Empty);
            }
        }

        /// <summary>单次退避的硬上限（配置值异常大时也最多等这么久）。</summary>
        private const int MaxBackoffMs = 30_000;

        /// <summary>指数退避：500ms → 1s → 2s → 4s（封顶左移 4 位，避免重试几十次后等几十秒）。</summary>
        private static int BackoffMs(RevHotConfig config, int attempt)
        {
            // 先升 long 再移位：int 移位在基数大时会把符号位移成负数（等待时间变 0 甚至更糟）
            long wait = (long)config.RetryBackoffMs << Math.Min(attempt, 4);
            if (wait > MaxBackoffMs) wait = MaxBackoffMs;
            return wait < 0 ? 0 : (int)wait;
        }

        /// <summary>
        /// 可取消的退避等待。★ 不用 RevTask.Delay：它不带取消令牌，玩家在退避期间取消，
        /// 还要白等满一次退避时长才响应 —— 改成逐帧检查，取消立刻生效。
        /// </summary>
        private static async RevTask BackoffDelayAsync(int milliseconds, RevCancellationToken token)
        {
            if (milliseconds <= 0) return;
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                token.ThrowIfCancelled();
                await RevTask.Yield();
            }
        }
    }
}
