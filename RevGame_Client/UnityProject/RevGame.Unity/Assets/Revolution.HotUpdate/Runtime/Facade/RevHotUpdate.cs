// ============================================================
// RevHotUpdate.cs —— 热更门面（业务唯一入口）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Facade\
//
// 【它就是"启动时的一次热更"】
//   游戏启动流程里调用一次 InitializeAsync，这一行就是全部：
//     拉清单 → 大版本校验 → 版本比对 → 差量下载 → 校验落地 → 切版本 → 重装资源策略
//   返回之后资源系统已经是"热更后的状态"，业务照常写加载代码（一行不用改）。
//
//   ★ 它内部会自动调用 Install()（装框架钩子）与 RevResBootstrap.Init()（重装资源策略），
//     所以不需要再手动调 RevResBootstrap.Init —— 调了也没关系（幂等）。
//
// 【失败纪律】
//   热更失败绝不破坏现有版本：下载/校验失败时 current.txt 没动，玩家照常玩旧版；
//   业务拿到 Success=false + Error（分类 + 人话），自行决定重试 / 跳过 / 提示。
//
// 【并发纪律】
//   同一时间只允许"一轮"热更在跑：重复调用 InitializeAsync 会直接等正在跑的那一轮，
//   不会下载两遍、也不会把资源策略重装两次。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Revolution;

namespace Revolution.HotUpdate
{
    /// <summary>热更门面：状态机 + 对外动作 + 进度事件。</summary>
    public static class RevHotUpdate
    {
        private static RevHotState _state = RevHotState.Idle;
        private static RevHotError _lastError;
        private static string _localResVersion = "";
        private static RevHotConfig _activeConfig;

        // ---------- "一轮热更"的挂起状态 ----------
        // _pending 不为 null = 有一轮正在跑：重复调用 InitializeAsync、以及别处的 WaitReadyAsync，
        // 都会等到它结束，而不是并发再跑一轮（并发双跑 = 下载两遍 + 资源策略重装两次，必须防住）。
        private static RevTaskCompletionSource<RevHotUpdateResult> _pending;
        private static RevTask _initTask;                // 保活引用：防止在跑的那一轮任务被回收
        private static RevHotUpdateResult _lastResult;   // 最近一轮的最终结果
        private static bool _hasResult;

        /// <summary>
        /// 进度事件：任何一次热更（无论从哪个入口发起）都会往这里推。
        /// ★ 回调参数 onProgress 与这个事件二选一即可，两个都会收到。
        /// </summary>
        public static event Action<RevHotProgress> Progress;

        /// <summary>当前进行到哪一步（业务可轮询，也可只用进度回调 / 事件）。</summary>
        public static RevHotState State { get { return _state; } }

        /// <summary>最近一次失败的原因（成功后仍保留，便于"失败后进游戏再上报"）。</summary>
        public static RevHotError LastError { get { return _lastError; } }

        /// <summary>本地当前生效的资源版本（首次启动为空）。</summary>
        public static string LocalResVersion { get { return _localResVersion; } }

        // ============================================================
        // 对外 API
        // ============================================================

        /// <summary>
        /// 只检查：不做网络下载。返回"有没有更新 / 要下多少 / 是否需要更新客户端"。
        /// 适合先弹窗问玩家（WIFI 提示、大包确认）再决定要不要下。
        /// <para>唯一例外：首包落地（把 StreamingAssets 的内置包拷到本地，纯磁盘操作、幂等）——
        /// Android 不落地的话，检查后不更新直接进游戏也读不到内置资源。</para>
        /// </summary>
        public static RevTask<RevHotCheckResult> CheckAsync(RevHotConfig config, Action<RevHotProgress> onProgress = null, RevCancellationToken token = null)
        {
            return RunCheckAsync(config, WithEventSink(onProgress), token);
        }

        /// <summary>
        /// 按 CheckAsync 的结果执行更新（下载 → 校验 → 落地 → 切版本 → 重装资源策略）。
        /// ★ 必须把 CheckAsync 返回的 check 原样传进来（差量计划在里面）。
        /// </summary>
        public static RevTask<RevHotUpdateResult> UpdateAsync(RevHotCheckResult check, Action<RevHotProgress> onProgress = null, RevCancellationToken token = null)
        {
            return RunUpdateAsync(check, WithEventSink(onProgress), token);
        }

        /// <summary>
        /// ★ 启动时手动调用的"一次热更"：检查 →（有更新则）差量下载 → 校验 → 落地 → 切版本 → 重装资源策略。
        /// <para>放在"加载任何业务资源"之前；返回后资源系统就是热更后的状态，业务照常写加载代码。</para>
        /// <para>重复调用不会跑两遍 —— 第二次会直接等正在跑的那一轮的结果。</para>
        /// </summary>
        public static RevTask<RevHotUpdateResult> InitializeAsync(RevHotConfig config, Action<RevHotProgress> onProgress = null, RevCancellationToken token = null)
        {
            if (_pending != null) return _pending.Task;      // 已有一轮在跑：等它，别并发双跑
            return RunInitializeAsync(config, WithEventSink(onProgress), token);
        }

        /// <summary>
        /// 等"正在进行的那一轮热更"结束（没有在跑就立即返回最近一次的结果）。
        /// <para>给"启动流程某处发起了热更、另一个地方（比如首场景的业务）需要等它结束"的场景用 ——
        /// 它不会发起新的热更，只是等。</para>
        /// </summary>
        public static RevTask<RevHotUpdateResult> WaitReadyAsync()
        {
            if (_pending != null) return _pending.Task;
            if (_hasResult) return RevTask<RevHotUpdateResult>.FromResult(_lastResult);

            // 谁都没发起过：明确告诉业务"你还没接过热更"，而不是装作成功
            RevHotError notRun = RevHotError.Of(RevHotErrorCode.NotRun, "还没有执行过热更：请在加载业务资源前调用 RevHotUpdate.InitializeAsync（一次即可）");
            return RevTask<RevHotUpdateResult>.FromResult(RevHotUpdateResult.Fail(notRun, 0));
        }

        /// <summary>
        /// 装载框架钩子（幂等）。
        /// ★ 必须发生在 RevResBootstrap.Init 之前 —— 之后每次 Init 都会带着热更钩子读映射表。
        ///   InitializeAsync 内部已自动调用；只有"自己接管资源系统初始化"的工程才需要手动调它。
        /// </summary>
        public static void Install()
        {
            RevHotResBridge.Install();
        }

        /// <summary>排查用：把当前状态、错误、路径快照一次性打出来（贴给同事排查用）。</summary>
        public static string Dump()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.AppendLine("[RevHotUpdate] 状态 " + _state);
            sb.AppendLine("  本地资源版本：" + (_localResVersion.Length == 0 ? "（无）" : _localResVersion));
            sb.AppendLine("  最近错误：" + (_lastError == null ? "（无）" : _lastError.ToString()));
            sb.AppendLine("  " + RevHotStore.Describe());
            return sb.ToString();
        }

        /// <summary>进 Play / 域重载复位（只清内存；磁盘上的版本数据是玩家数据，绝不碰）。</summary>
        internal static void ResetForNewSession()
        {
            _state = RevHotState.Idle;
            _lastError = null;
            _localResVersion = "";
            _activeConfig = null;
            _pending = null;
            _initTask = default;
            _lastResult = null;
            _hasResult = false;
            Progress = null;        // ★ 清订阅者：业务只订阅不退订时，二次 Play 会经静态事件持有已销毁的对象
        }

        // ============================================================
        // 检查
        // ============================================================

        private static async RevTask<RevHotCheckResult> RunCheckAsync(RevHotConfig config, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            token = token ?? new RevCancellationToken();
            if (config == null)
            {
                // 不让 NRE 混进"预期外异常"：配置缺失是配置问题，要在第一步就给人话
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ConfigInvalid,
                    "配置为 null：CheckAsync 需要一个填好 RemoteRoot 的 RevHotConfig"));
            }

            _activeConfig = config;
            _state = RevHotState.Checking;
            Report(onProgress, RevHotState.Checking, "正在检查更新…");

            try
            {
                // ---------- ① 配置校验：错在源头，别等下载到一半 ----------
                string configError = config.Validate();
                if (configError != null)
                {
                    return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ConfigInvalid, configError));
                }

                // ---------- ② 存储装配 + 本地版本 ----------
                RevHotStore.Configure(config);
                RevHotStore.PrepareLocalRoot();
                RevHotStore.RecoverInterruptedActivation();
                _localResVersion = RevHotStore.ReadCurrentVersion();
                if (string.IsNullOrEmpty(_localResVersion) == false)
                    RevHotStore.SetActiveVersion(_localResVersion); // 扫描旧完整目录，为候选快照寻找同 hash 的来源

                RevHotManifest localBaseline = null;
                if (string.IsNullOrEmpty(config.ResVersionOverride))
                {
                    localBaseline = await ReadLocalBaselineAsync(config, _localResVersion, token);
                }
                else
                {
                    // 调试语义：指定资源版本 = "本地状态作废"，按空基线重新全量拉该版本
                    RevLog.Warn("ResVersionOverride=" + config.ResVersionOverride + "：忽略本地基线，按全量更新处理", config.LogTag);
                }

                // ---------- ②′ 首包落地（Android 的命门） ----------
                // ★ 把 StreamingAssets 里的内置包拷到 persistentDataPath：APK 内的包对 LoadFromFile
                //   不是文件路径，不落地就永远读不到内置资源（这条链路此前断了：方法存在但无人调用）。
                //   放在"检查"阶段是因为内置包是首包基线的一部分，与"是否执行更新"无关；
                //   幂等 —— 已落地且大小一致的直接跳过，二次启动零成本。WebGL 在方法内部直接跳过。
                if (localBaseline != null
                    && (RevHotPlatform.NeedsBuiltinCopy || config.FirstPackageMode == RevHotFirstPackageMode.CopyToLocal))
                {
                    await RevHotStore.CopyBuiltinPackagesAsync(localBaseline, config, onProgress, token);
                }

                // 检查阶段不修改已生效版本：旧版快照验证/修复留到 UpdateAsync 的安全点执行。
                // ---------- ③ 拉远端清单（多源 + 重试；失败保持当前版本，玩家照常玩） ----------
                Func<RevHotSource, string> manifestUrl = delegate(RevHotSource source) { return RevHotUrlBuilder.ManifestUrl(config, source.Root); };
                string manifestText = await RevHotDownloader.FetchTextAsync(config, manifestUrl, token);

                RevHotManifest remote = RevHotManifest.Parse(manifestText, out string parseError);
                if (remote == null)
                {
                    string detail = "清单前 200 字：" + (manifestText.Length > 200 ? manifestText.Substring(0, 200) : manifestText);
                    RevHotError invalid = RevHotError.Of(RevHotErrorCode.ManifestInvalid, "版本清单格式错误：" + parseError, detail);
                    return Fail(onProgress, invalid);
                }
                string manifestError = remote.ValidateForActivation(config.Platform, config.Channel, config.Env);
                if (manifestError != null)
                    return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ManifestInvalid, "远端清单拒绝激活：" + manifestError));

                // ---------- ④ 大版本锚定校验（跨大版本必须重出包） ----------
                bool appMismatch = RevHotVersion.AreEqual(remote.AppVersion, config.AppVersion) == false;
                bool belowMin = string.IsNullOrEmpty(remote.MinAppVersion) == false
                                && RevHotVersion.Compare(config.AppVersion, remote.MinAppVersion) < 0;

                if (appMismatch || belowMin)
                {
                    var info = new RevHotForceUpdateInfo
                    {
                        RequiredAppVersion = appMismatch ? remote.AppVersion : remote.MinAppVersion,
                        CurrentAppVersion = config.AppVersion,
                        Message = "本资源包需要客户端 " + (appMismatch ? remote.AppVersion : remote.MinAppVersion)
                                  + "，当前客户端是 " + config.AppVersion + "，请先更新游戏",
                    };

                    RevLog.Warn("大版本不匹配 → 需要更新客户端（" + info.Message + "）", config.LogTag);
                    if (config.OnForceUpdateRequired != null) config.OnForceUpdateRequired(info);

                    // "检查"本身是成功的：远端一切正常，只是客户端版本不行。状态回 Idle（等业务引导强更）。
                    _state = RevHotState.Idle;
                    return new RevHotCheckResult
                    {
                        Success = true,
                        ForceUpdateRequired = true,
                        ForceUpdate = info,
                        RemoteResVersion = remote.ResVersion,
                        LocalResVersion = _localResVersion,
                        ManifestText = manifestText,
                        Config = config,
                    };
                }

                // ---------- ⑤ 版本比对 ----------
                // ★ WebGL / 小游戏短路：那边没有文件系统，存不下清单副本，本地基线永远退化为内置清单 ——
                //   若不短路，二次启动仍会拿"内置基线 vs 远端清单"判出"有更新"，白白重拉一遍映射表、
                //   重打一遍完成标记。资源 URL 带版本段、内容不可变：上次生效版本 == 远端版本 = 什么都不用拉。
                if (RevHotPlatform.IsWebGL
                    && string.IsNullOrEmpty(config.ResVersionOverride)
                    && RevHotVersion.AreEqual(remote.ResVersion, _localResVersion))
                {
                    RevHotPlan unchanged = new RevHotPlan();
                    _state = RevHotState.Idle;
                    return new RevHotCheckResult
                    {
                        Success = true,
                        HasUpdate = false,
                        LocalResVersion = _localResVersion,
                        RemoteResVersion = remote.ResVersion,
                        Plan = unchanged,
                        RemoteManifest = remote,
                        ManifestText = manifestText,
                        Config = config,
                    };
                }

                RevHotPlan plan = RevHotPlanner.Build(remote, localBaseline);
                _state = RevHotState.Idle;

                return new RevHotCheckResult
                {
                    Success = true,
                    HasUpdate = plan.IsEmpty == false,
                    LocalResVersion = _localResVersion,
                    RemoteResVersion = remote.ResVersion,
                    TotalBytes = plan.TotalBytes,
                    TotalBytesText = RevHotProgress.FormatBytes(plan.TotalBytes),
                    AddedCount = plan.Added.Count,
                    ChangedCount = plan.Changed.Count,
                    Plan = plan,
                    RemoteManifest = remote,
                    ManifestText = manifestText,
                    Config = config,
                };
            }
            catch (RevOperationCanceledException)
            {
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.Cancelled, "检查更新已取消"));
            }
            catch (RevHotException e)
            {
                return Fail(onProgress, e.Error);
            }
            catch (Exception e)
            {
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.Unexpected, "检查更新时出现预期外的异常：" + e.Message, e.StackTrace));
            }
        }

        // ============================================================
        // 更新
        // ============================================================

        private static async RevTask<RevHotUpdateResult> RunUpdateAsync(RevHotCheckResult check, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            token = token ?? new RevCancellationToken();
            Stopwatch clock = Stopwatch.StartNew();

            if (check == null || check.Success == false || check.RemoteManifest == null || check.Plan == null)
            {
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ConfigInvalid, "UpdateAsync 需要 CheckAsync 的成功结果（先检查、再更新）"), clock);
            }

            // ★ 配置优先用 check 自带的（与生成计划时的配置严格配对）；
            //   旧数据没有携带时回退门面记录，再没有就只能明确失败，绝不能拿 null 往下走。
            RevHotConfig config = check.Config ?? _activeConfig;
            if (config == null)
            {
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ConfigInvalid,
                    "更新配置缺失：请把 CheckAsync 的结果原样传给 UpdateAsync（期间不要复位热更会话）"), clock);
            }

            // ★ 必须装钩子：UpdateAsync 是公开 API，"CheckAsync → 弹窗确认 → UpdateAsync"这条
            //   业务主路径不经过 InitializeAsync —— 不装的话，更新成功了但加载路径钩子没挂上，
            //   随后加载资源走的还是 StreamingAssets 旧路径（热更"成功"却读不到新内容）。幂等，重复装无害。
            RevHotResBridge.Install();
            RevHotManifest remote = check.RemoteManifest;
            string updateManifestError = remote.ValidateForActivation(config.Platform, config.Channel, config.Env);
            if (updateManifestError != null)
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ManifestInvalid, "待激活清单无效：" + updateManifestError), clock);
            string resVersion = remote.ResVersion;
            string previousVersion = _localResVersion;
            RevHotManifest previousManifest = RevHotStore.ActiveManifest;
            if (previousManifest == null && RevHotPlatform.SupportsLocalFiles && !string.IsNullOrEmpty(previousVersion))
                previousManifest = TryParseFile(RevHotStore.ManifestPath(previousVersion));
            Dictionary<string, string> previousResMap = RevHotStore.ActiveMemoryResMap;
            bool activating = false;

            try
            {
                // 更新会重装资源系统，不能在业务还持有资源句柄时强行切换。
                // 可在玩法结束后再调用 UpdateAsync；本检查不对下载中的资源施加新策略。
                foreach (RevResHandle handle in RevResManager.CachedHandles.Values)
                {
                    if (handle.RefCount <= 0 && !handle.IsLoading) continue;
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ApplyFailed,
                        "当前仍有业务资源被持有或正在加载，请先退出玩法并释放资源，再激活热更新"), false);
                }
            }
            catch (RevHotException e)
            {
                return Fail(onProgress, e.Error, clock);
            }

            try
            {
                // 先确认旧版真能回退：在业务句柄归零的安全点补齐并校验旧版快照。
                // 如果旧版本坏到无法修复，拒绝激活新版本，不能承诺“失败后恢复旧版”。
                if (RevHotPlatform.SupportsLocalFiles && !string.IsNullOrEmpty(previousVersion))
                {
                    RevHotManifest rollbackManifest = previousManifest ?? await ReadLocalBaselineAsync(config, previousVersion, token);
                    if (rollbackManifest == null)
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "没有可用的旧版本清单，无法保证更新失败后回退"), false);
                    await RevHotStore.ValidateSnapshotAsync(rollbackManifest, previousVersion, token);
                    previousManifest = rollbackManifest;
                }

                // ---------- ① 磁盘预检（文件型平台；小游戏没有磁盘概念，直接跳过） ----------
                _state = RevHotState.Downloading;
                RevHotStore.EnsureDiskSpace(check.Plan.TotalBytes);

                // ---------- ② 下载差量 ----------
                // 文件型平台（Android / iOS / PC）：下载 → 校验 → 原子落盘；
                // URL 型平台（WebGL / 小游戏）：AB 由引擎按 URL 拉取并缓存，这里没有"下载"这一步。
                if (RevHotPlatform.SupportsLocalFiles && check.Plan.IsEmpty == false)
                {
                    await RevHotDownloader.DownloadPlanAsync(check.Plan, config, resVersion, onProgress, token);
                }

                // URL 型平台：确保"热更映射表"已在内存里（资源系统 Init 时通过钩子取走）。
                // ★ 不能只在 ResMapChanged 时拉：重启后的新会话内存表必然为空 —— 那时就算本版本
                //   的表没变，也必须补拉一次，否则热更新增的资源在本次会话全部加载不到。
                // strict=true：首次安装/更新语义 —— 新版本的表拉不到就算更新失败（可重试）。
                // force=ResMapChanged：表变了就必须重拉 —— 同一会话的第二次热更时内存里还是旧表，
                // "已就位就跳过"会让新版本的新增资源全部加载不到（这是文件平台没有的坑：那边的表落盘按版本隔离）。
                await EnsureWebGLResMapAsync(config, resVersion, true, check.Plan.ResMapChanged, token, remote.ResMap.Sha256);

                // ---------- ③ 版本目录补齐（清单副本 + 映射表） ----------
                _state = RevHotState.Applying;
                if (RevHotPlatform.SupportsLocalFiles)
                {
                    // 清单原文存一份进版本目录：它是"下次差量计算的基线"，也是回滚 / 排查的证据
                    Directory.CreateDirectory(RevHotStore.VersionDir(resVersion));
                    File.WriteAllText(RevHotStore.ManifestPath(resVersion), check.ManifestText);
                    await EnsureResMapSnapshotAsync(check, config, resVersion, token);
                }

                // ---------- ④ 先验完整候选：每个包/ResMap SHA 与目标清单一致、主包可装载 ----------
                // 不能只验刚下载的差量：未变包可能藏在即将被清理的更老目录里。
                if (RevHotPlatform.SupportsLocalFiles)
                {
                    // 完整快照还需要拷贝未变包，不能只预留“计划下载字节数”。
                    RevHotStore.EnsureDiskSpace(remote.TotalBytes());
                    await RevHotStore.ValidateSnapshotAsync(remote, resVersion, token);
                }
                else await RevHotStore.ValidateWebGLCandidateAsync(remote, resVersion, token);
                await RevHotStore.ProbeRequiredAssetsAsync(remote, resVersion, config.RequiredAssets, token);
                token.ThrowIfCancelled();

                // ---------- ⑤ 切版本前留恢复日志；旧目录必须保留到重装成功之后 ----------
                // 下载及探测跨帧。玩家可能在此期间进入玩法或重新借到资源，提交前再次确认。
                if (config.CanActivate != null && !config.CanActivate(remote))
                {
                    throw new RevHotException(RevHotError.Of(RevHotErrorCode.ApplyFailed, "业务目前不允许激活候选版本"), false);
                }
                foreach (RevResHandle handle in RevResManager.CachedHandles.Values)
                {
                    if (handle.RefCount > 0 || handle.IsLoading)
                    {
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.ApplyFailed, "激活前出现了新的业务资源持有或加载"), false);
                    }
                }
                RevHotStore.MarkVersionComplete(resVersion);
                RevHotStore.BeginActivation(previousVersion);
                activating = true;
                // ★ 必须先用旧依赖图释放旧资源，再切到新图；倒过来旧包的引用计数会按新图释放。
                RevResBootstrap.Instance.ShutdownAll();
                RevHotStore.WriteCurrentVersion(resVersion);
                RevHotStore.SetActiveVersion(resVersion);
                RevHotStore.SetActiveBundleKeys(remote);
                _localResVersion = resVersion;

                // Init 不会主动加载主包；候选验证已在上面预先探测主包与所有文件。
                RevResBootstrap.Instance.Init();
                RevHotStore.CommitActivation();
                activating = false;
                RevHotStore.DeleteOldVersions(config.KeepVersions + 1, resVersion);

                _state = RevHotState.Ready;
                string message = check.HasUpdate ? "已更新到 " + resVersion : "已是最新版本 " + resVersion;
                RevLog.Info(message + "（耗时 " + clock.Elapsed.TotalSeconds.ToString("F1") + "s）", config.LogTag);
                return RevHotUpdateResult.Ok(message, resVersion, clock.Elapsed.TotalSeconds);
            }
            catch (RevOperationCanceledException)
            {
                RevHotError error = RevHotError.Of(RevHotErrorCode.Cancelled, "更新已取消（已下载内容已保留，下次继续）");
                return Fail(onProgress, RestoreIfNeeded(activating, previousVersion, previousManifest, previousResMap, error), clock);
            }
            catch (RevHotException e)
            {
                return Fail(onProgress, RestoreIfNeeded(activating, previousVersion, previousManifest, previousResMap, e.Error), clock);
            }
            catch (Exception e)
            {
                RevHotError error = RevHotError.Of(RevHotErrorCode.Unexpected, "更新过程出现预期外的异常：" + e.Message, e.StackTrace);
                return Fail(onProgress, RestoreIfNeeded(activating, previousVersion, previousManifest, previousResMap, error), clock);
            }
        }

        /// <summary>指针可能已切新时恢复旧版；恢复失败必须报错而不是假装可玩。</summary>
        private static RevHotError RestoreIfNeeded(bool activating, string previousVersion,
            RevHotManifest previousManifest, Dictionary<string, string> previousResMap, RevHotError original)
        {
            if (!activating)
            {
                if (RevHotPlatform.IsWebGL) RevHotStore.SetActiveResMap(previousResMap);
                return original;
            }
            try
            {
                RevHotStore.WriteCurrentVersion(previousVersion);
                RevHotStore.SetActiveVersion(previousVersion);
                RevHotStore.SetActiveBundleKeys(previousManifest);
                RevHotStore.SetActiveResMap(previousResMap);
                _localResVersion = previousVersion;
                RevHotResBridge.ReinitResourceSystem();
                RevHotStore.CommitActivation();
                return RevHotError.Of(RevHotErrorCode.ApplyFailed, "新版本激活失败，已恢复原版本：" + original.Message);
            }
            catch (Exception rollbackError)
            {
                // 留着 .pending：下次启动还会尝试恢复旧指针。此时不允许报告 Ready。
                return RevHotError.Of(RevHotErrorCode.ApplyFailed,
                    "资源激活与回退均失败，请停止进入游戏并修复本地资源：" + original.Message,
                    rollbackError.Message);
            }
        }

        // ============================================================
        // 初始化（一条龙）：登记"进行中的一轮" → 执行 → 回填结果
        // ============================================================

        /// <summary>
        /// 入口：先登记"有一轮在跑"（防并发双跑，也是 WaitReadyAsync 的等待目标），再执行真正的初始化。
        /// </summary>
        private static async RevTask<RevHotUpdateResult> RunInitializeAsync(RevHotConfig config, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            if (_pending != null) return await _pending.Task;

            RevTaskCompletionSource<RevHotUpdateResult> pending = RevTask<RevHotUpdateResult>.CreateSource();
            _pending = pending;
            _initTask = RunInitializeCoreAsync(config, onProgress, token);      // 保活引用，防止任务被回收
            return await pending.Task;
        }

        /// <summary>
        /// 真正的初始化流程。★ 结束时（无论成败）必须把结果回填给挂起的等待者 ——
        /// 否则 WaitReadyAsync / 重复调用会永远等下去（这类"静默卡死"是最难查的 bug）。
        /// </summary>
        private static async RevTask RunInitializeCoreAsync(RevHotConfig config, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            RevHotUpdateResult result;
            try
            {
                result = await RunInitializeCoreBodyAsync(config, onProgress, token);
            }
            catch (Exception e)
            {
                // 保险丝：任何漏网的异常都转成一次明确的失败，而不是把等待方永远挂在半空
                result = RevHotUpdateResult.Fail(RevHotError.Of(RevHotErrorCode.Unexpected, "热更初始化出现预期外的异常：" + e.Message, e.StackTrace), 0);
            }

            _lastResult = result;
            _hasResult = true;

            RevTaskCompletionSource<RevHotUpdateResult> pending = _pending;
            _pending = null;
            if (pending != null) pending.SetResult(result);
        }

        /// <summary>初始化主体（检查 → 下载 → 落地 → 切版本 → 重装资源策略）。</summary>
        private static async RevTask<RevHotUpdateResult> RunInitializeCoreBodyAsync(RevHotConfig config, Action<RevHotProgress> onProgress, RevCancellationToken token)
        {
            Stopwatch clock = Stopwatch.StartNew();
            token = token ?? new RevCancellationToken();

            Install();                                   // ① 先装钩子（幂等；之后业务自己的 Init 也会带上热更）

            RevHotCheckResult check = await RunCheckAsync(config, onProgress, token);
            if (check.Success == false)
            {
                return RevHotUpdateResult.Fail(check.Error, clock.Elapsed.TotalSeconds);
            }

            if (check.ForceUpdateRequired)
            {
                // 需要更新客户端：资源层保持"出包基线"可用（旧版本照常能玩），
                // 怎么引导玩家（跳商店 / 公告 / 整包下载）由业务在 OnForceUpdateRequired 回调里决定。
                // ★ 说了"照常能玩"就得真的能玩：钩子已装，资源系统必须初始化一次，
                //   否则首次启动 + 强更的场景下没人调过 Init，"基线可用"只是纸面承诺。
                if (string.IsNullOrEmpty(_localResVersion) == false)
                {
                    RevHotStore.SetActiveVersion(_localResVersion);
                }
                RevHotResBridge.ReinitResourceSystem();
                _state = RevHotState.Ready;
                return RevHotUpdateResult.Ok(check.ForceUpdate.Message, _localResVersion, clock.Elapsed.TotalSeconds);
            }

            if (check.HasUpdate)
            {
                return await RunUpdateAsync(check, onProgress, token);
            }

            // 无更新同样要检验本地版本：旧包可能损坏，或多轮差量后来源目录已被删除。
            // 不能仅凭“清单 hash 相同”就报告 Ready。
            if (RevHotPlatform.SupportsLocalFiles)
            {
                try
                {
                    Directory.CreateDirectory(RevHotStore.VersionDir(check.RemoteManifest.ResVersion));
                    await EnsureResMapSnapshotAsync(check, config, check.RemoteManifest.ResVersion, token);
                    await RevHotStore.ValidateSnapshotAsync(check.RemoteManifest, check.RemoteManifest.ResVersion, token);
                    RevHotStore.MarkVersionComplete(check.RemoteManifest.ResVersion);
                }
                catch (RevHotException e) { return Fail(onProgress, e.Error, clock); }
            }

            // ---------- 没有更新：把"生效版本"钉下来即可（首次启动 = 内置基线的版本号） ----------
            if (string.IsNullOrEmpty(_localResVersion))
            {
                // 内置基线的资源版本 = 远端清单的版本（比对一致才会走到这里）
                string baselineVersion = check.RemoteManifest.ResVersion;
                _localResVersion = baselineVersion; // 验证并重装成功后才持久化指针
            }

            RevHotStore.SetActiveVersion(_localResVersion);
            RevHotStore.SetActiveBundleKeys(check.RemoteManifest);    // ★ 同上：无更新时也要把 hash 表钉到当前版本
            // ★ WebGL 重启后走这里：静态内存表已清空，必须补拉映射表（strict=false，内置表兜底），
            //   否则"版本相等短路"会让热更新增的资源在本次会话全部加载不到。
            //   force=false：能走到这里说明远端版本没变 —— 内存里已有的表就是当前版本的，不必重拉。
            try
            {
                await EnsureWebGLResMapAsync(config, check.RemoteManifest.ResVersion, true, false, token, check.RemoteManifest.ResMap.Sha256);
                if (RevHotPlatform.IsWebGL)
                    await RevHotStore.ValidateWebGLCandidateAsync(check.RemoteManifest, check.RemoteManifest.ResVersion, token);
                RevHotResBridge.ReinitResourceSystem();
                if (string.IsNullOrEmpty(check.LocalResVersion))
                    RevHotStore.WriteCurrentVersion(_localResVersion);
            }
            catch (RevHotException e) { return Fail(onProgress, e.Error, clock); }
            catch (Exception e)
            {
                return Fail(onProgress, RevHotError.Of(RevHotErrorCode.ApplyFailed, "资源系统初始化失败：" + e.Message), clock);
            }

            _state = RevHotState.Ready;
            string okMessage = "已是最新版本 " + _localResVersion;
            RevLog.Info(okMessage, config.LogTag);
            return RevHotUpdateResult.Ok(okMessage, _localResVersion, clock.Elapsed.TotalSeconds);
        }

        // ============================================================
        // WebGL / 小游戏：热更映射表的内存供给
        // ============================================================

        /// <summary>
        /// 确保"热更映射表"已在内存里（那边没有文件系统，表只能放内存，资源系统 Init 时经钩子取走）。
        /// ★ 每个新会话（重启 / 快速进入 Play）静态内存都是空的 —— 已就位时直接跳过（同会话零成本）。
        /// <para>strict=true：拉不到就抛（更新语义：新版本的表必须到位，失败可整体重试）；</para>
        /// <para>strict=false：拉不到只警告（无更新/重启补拉语义：内置表兜底，游戏至少能跑）；</para>
        /// <para>force=true：无视"已就位"直接重拉（更新语义下表变了 —— 内存里那份是旧版本表的残留）。</para>
        /// </summary>
        private static async RevTask EnsureWebGLResMapAsync(RevHotConfig config, string resVersion, bool strict, bool force, RevCancellationToken token, string expectedSha256)
        {
            if (RevHotPlatform.IsWebGL == false) return;
            if (force == false && RevHotStore.HasMemoryResMap) return;

            Func<RevHotSource, string> resMapUrl = delegate(RevHotSource source) { return RevHotUrlBuilder.ResMapUrl(config, source.Root, resVersion); };
            try
            {
                string resMapText = await RevHotDownloader.FetchTextAsync(config, resMapUrl, token);
                // URL 平台没有落盘文件，但映射表仍需按清单指纹验证，不能信任一份碰巧能解析的旧表。
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(resMapText);
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    string actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "WebGL ResMap 与目标清单的 SHA-256 不一致"), false);
                }
                RevHotStore.SetActiveResMap(RevHotStore.ParseResMapText(resMapText));
            }
            catch (RevOperationCanceledException)
            {
                throw;
            }
            catch (RevHotException e)
            {
                if (strict) throw;
                RevLog.Warn("热更映射表补拉失败，本次使用内置表兜底（新增资源可能加载不到）：" + e.Error.Message, config.LogTag);
            }
        }

        // ============================================================
        // 本地基线 / 版本目录补齐
        // ============================================================

        /// <summary>
        /// 读本地基线清单：① 当前版本目录（热更过）→ ② 出包内置清单（首包基线）→ ③ null（纯远端）。
        /// 基线是"差量计算"的参照物 —— 大版本锚定下，基线天然就是"出包状态"。
        /// </summary>
        private static async RevTask<RevHotManifest> ReadLocalBaselineAsync(RevHotConfig config, string localVersion, RevCancellationToken token)
        {
            // WebGL / 小游戏没有本地文件：清单副本根本存不下来，基线永远走内置清单
            // （不拦的话每次启动都会对不存在的路径 File.ReadAllText 抛一次异常、打一条误导性警告）
            if (RevHotPlatform.IsWebGL) return null;

            if (string.IsNullOrEmpty(localVersion) == false && RevHotStore.IsVersionComplete(localVersion))
            {
                RevHotManifest fromDisk = TryParseFile(RevHotStore.ManifestPath(localVersion));
                if (fromDisk != null) return fromDisk;
                RevLog.Warn("当前版本的清单副本缺失（视为需要重新比对）：" + localVersion, config.LogTag);
            }

            try
            {
                string text = await RevHotStore.ReadTextAsync(RevHotStore.BuiltinManifestSource(), token);
                RevHotManifest builtin = RevHotManifest.Parse(text, out string parseError);
                if (builtin != null) return builtin;
                RevLog.Warn("内置清单解析失败（按纯远端处理）：" + parseError, config.LogTag);
            }
            catch (RevOperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // 没有内置清单 = 首包不含 AB（纯远端模式）：基线为空，所有远端包都算"新增"
                RevLog.Info("没有内置清单（按纯远端处理）：" + e.Message, config.LogTag);
            }

            return null;
        }

        /// <summary>把"当前版本目录"补齐：清单原文 + 映射表（映射表没变也要从基线复制过来）。</summary>
        private static async RevTask EnsureResMapSnapshotAsync(RevHotCheckResult check, RevHotConfig config, string resVersion, RevCancellationToken token)
        {
            // 映射表变化时，下载器已经把新表写到版本目录里了
            if (check.Plan.ResMapChanged) return;

            string target = RevHotStore.ResMapPath(resVersion);
            if (File.Exists(target)) return;

            // 映射表没变 → 内容与基线一致：从"旧版本目录 / 内置清单里的位置"复制一份过来。
            // ★ 这一步不能省：加载路径按"当前版本目录"找映射表，少了它就会悄悄退回内置表。
            string sourceText = await FindExistingResMapTextAsync(config, _localResVersion, token);
            if (sourceText != null)
            {
                File.WriteAllText(target, sourceText);
                return;
            }

            RevLog.Warn("没有找到可复用的映射表（纯远端首启）—— 本次版本将使用内置映射表", config.LogTag);
        }

        private static async RevTask<string> FindExistingResMapTextAsync(RevHotConfig config, string oldResVersion, RevCancellationToken token)
        {
            // ① 旧版本目录里的映射表（热更过至少一次）
            if (string.IsNullOrEmpty(oldResVersion) == false && RevHotStore.IsVersionComplete(oldResVersion))
            {
                string path = RevHotStore.ResMapPath(oldResVersion);
                if (File.Exists(path)) return File.ReadAllText(path);
            }

            // ② 出包时随包体发布的映射表（StreamingAssets；Android 上是 jar: URL）
            try
            {
                string source = RevHotUrlBuilder.BuiltinBundlePath(config, "ResMap.txt");
                return await RevHotStore.ReadTextAsync(source, token);
            }
            catch (RevOperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // 小工具
        // ============================================================

        private static RevHotManifest TryParseFile(string path)
        {
            try
            {
                if (File.Exists(path) == false) return null;
                string text = File.ReadAllText(path);
                return RevHotManifest.Parse(text, out _);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把"调用方的回调"与"静态 Progress 事件"合成一个 sink：两边都能收到进度。</summary>
        private static Action<RevHotProgress> WithEventSink(Action<RevHotProgress> onProgress)
        {
            if (onProgress == null) return RaiseProgress;
            return delegate(RevHotProgress p) { RaiseProgress(p); onProgress(p); };
        }

        private static void RaiseProgress(RevHotProgress progress)
        {
            Action<RevHotProgress> handler = Progress;
            if (handler != null) handler(progress);
        }

        private static RevHotCheckResult Fail(Action<RevHotProgress> onProgress, RevHotError error)
        {
            _state = RevHotState.Failed;
            _lastError = error;
            RevLog.Warn("热更检查失败：" + error.ToString(), LogTag());
            Report(onProgress, RevHotState.Failed, error.Message);
            return RevHotCheckResult.Fail(error);
        }

        private static RevHotUpdateResult Fail(Action<RevHotProgress> onProgress, RevHotError error, Stopwatch clock)
        {
            _state = RevHotState.Failed;
            _lastError = error;
            RevLog.Warn("热更失败：" + error.ToString(), LogTag());
            Report(onProgress, RevHotState.Failed, error.Message);
            return RevHotUpdateResult.Fail(error, clock == null ? 0 : clock.Elapsed.TotalSeconds);
        }

        private static void Report(Action<RevHotProgress> sink, RevHotState state, string text)
        {
            if (sink == null) return;
            sink(new RevHotProgress { State = state, Text = text });
        }

        private static string LogTag()
        {
            return _activeConfig == null ? "HotUpdate" : _activeConfig.LogTag;
        }
    }
}
