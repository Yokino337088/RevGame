// ============================================================
// RevHotResults.cs —— 对外结果对象（业务拿到的就是这个，永远不是异常）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【纪律】所有失败都变成 RevHotError（分类 + 人话），Success = false；
//   业务侧拿到结果后只需要问一句"Success 吗"，失败就直接把 Error.Message 给玩家看。
// ============================================================
namespace Revolution.HotUpdate
{
    /// <summary>CheckAsync 的结果：要不要更新、要下多少、是否需要强更客户端。</summary>
    public sealed class RevHotCheckResult
    {
        /// <summary>检查本身是否成功（false 时 Error 里有人话；此时可以按"跳过更新继续玩旧版"处理）。</summary>
        public bool Success;

        /// <summary>失败原因（Success 为 true 时为 null）。</summary>
        public RevHotError Error;

        /// <summary>是否有更新（清单一致且映射表没变 = false）。</summary>
        public bool HasUpdate;

        /// <summary>是否需要更新客户端（大版本不匹配 —— 资源热更救不了"代码变了"）。</summary>
        public bool ForceUpdateRequired;

        /// <summary>强更信息（远端要求的大版本 / 当前包体版本 / 人话）。</summary>
        public RevHotForceUpdateInfo ForceUpdate;

        /// <summary>本地当前资源版本（首次启动为空）。</summary>
        public string LocalResVersion = "";

        /// <summary>远端最新资源版本。</summary>
        public string RemoteResVersion = "";

        /// <summary>本次要下载的总字节数（WIFI 提示 / 大小弹窗用）。</summary>
        public long TotalBytes;

        /// <summary>总字节数的人话（"17.8 MB"）。</summary>
        public string TotalBytesText = "";

        /// <summary>新增包数。</summary>
        public int AddedCount;

        /// <summary>变更包数。</summary>
        public int ChangedCount;

        // ---------- 内部流转（业务不用碰；同一次 Check → Update 共享） ----------
        internal RevHotManifest RemoteManifest;
        internal RevHotPlan Plan;

        /// <summary>远端清单原文（更新时原样存进版本目录，作为下次差量计算的基线）。</summary>
        internal string ManifestText = "";

        /// <summary>
        /// 发起这次检查时的配置。★ 更新阶段从这里取配置，而不是从门面的"最近一次配置"取 ——
        /// 若 Check 与 Update 之间会话被复位（或被另一次 Check 覆盖），check 自带的配置仍然正确配对。
        /// </summary>
        internal RevHotConfig Config;

        /// <summary>失败结果。</summary>
        public static RevHotCheckResult Fail(RevHotError error)
        {
            return new RevHotCheckResult { Success = false, Error = error };
        }
    }

    /// <summary>UpdateAsync / InitializeAsync 的结果：成功了就是"当前版本可用"。</summary>
    public sealed class RevHotUpdateResult
    {
        /// <summary>是否成功（失败时资源系统仍可用 —— 热更失败绝不破坏现有版本）。</summary>
        public bool Success;

        /// <summary>失败原因。</summary>
        public RevHotError Error;

        /// <summary>人话结果（"已是最新版本 1.2.0.37" / "已更新到 1.2.0.37"）。</summary>
        public string Message = "";

        /// <summary>最终生效的资源版本。</summary>
        public string ResVersion = "";

        /// <summary>整个过程耗时（秒，给人看）。</summary>
        public double ElapsedSeconds;

        /// <summary>成功结果。</summary>
        public static RevHotUpdateResult Ok(string message, string resVersion, double elapsedSeconds)
        {
            return new RevHotUpdateResult { Success = true, Message = message, ResVersion = resVersion, ElapsedSeconds = elapsedSeconds };
        }

        /// <summary>失败结果。</summary>
        public static RevHotUpdateResult Fail(RevHotError error, double elapsedSeconds)
        {
            return new RevHotUpdateResult { Success = false, Error = error, ElapsedSeconds = elapsedSeconds };
        }
    }
}
