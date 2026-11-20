// ============================================================
// RevHotError.cs —— 热更的失败原因（"每个失败都有确定的出口"）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【为什么单独一个类型】
//   资源系统的 RevResLoadErrorReason 是框架的枚举，塞不进"磁盘满 / 清单过期"这类热更特有的失败；
//   而且热更失败要能直接变成人话（面板 / 弹窗拿去就能用）。
//   所以热更自己带一套错误码 + 一条人话消息，最后统一映射给框架（BundleLoadFail）。
//
// 【纪律】任何失败都必须有 RevHotError —— 消息为空的失败等于"静默失败"（最糟的那种）。
// ============================================================
using System;

namespace Revolution.HotUpdate
{
    /// <summary>热更失败的原因分类（排查时先看它，再看 Message）。</summary>
    public enum RevHotErrorCode
    {
        /// <summary>没有错误。</summary>
        None = 0,

        /// <summary>配置不对（RemoteRoot 没填 / 用了 http / 并发越界……）。</summary>
        ConfigInvalid = 1,

        /// <summary>清单拉不下来（网络 / CDN / 路径不对）。当前版本不受影响，可以重试。</summary>
        ManifestFetchFailed = 2,

        /// <summary>清单内容不对（格式坏 / 上传了一半）。当前版本不受影响。</summary>
        ManifestInvalid = 3,

        /// <summary>大版本不匹配：远端资源要求更新的包体 → 需要重新出包 / 引导玩家更新客户端。</summary>
        AppVersionMismatch = 4,

        /// <summary>文件下载失败（重试与换源都用尽）。Detail 里有最后一次的 URL 与 HTTP 错误。</summary>
        DownloadFailed = 5,

        /// <summary>校验不通过（尺寸 / hash 对不上）→ 已自动丢弃重下过，仍然失败才算失败。</summary>
        VerifyFailed = 6,

        /// <summary>磁盘空间不足（下载前预检拦下）。</summary>
        DiskFull = 7,

        /// <summary>玩家取消 / 切后台被中断。</summary>
        Cancelled = 8,

        /// <summary>切换版本失败（写 current.txt / 清理旧版本出错）。此时旧版本仍然可用。</summary>
        ApplyFailed = 9,

        /// <summary>预期外的异常（几乎不应出现；反馈问题时请附上 Dump）。</summary>
        Unexpected = 10,

        /// <summary>还没执行过热更（调用了 WaitReadyAsync，但没人发起过 InitializeAsync / 自动热更）。</summary>
        NotRun = 11,
    }

    /// <summary>一次热更失败的完整描述：分类 + 人话 + 定位用的细节（URL / 文件名）。</summary>
    public sealed class RevHotError
    {
        /// <summary>失败分类。</summary>
        public RevHotErrorCode Code = RevHotErrorCode.None;

        /// <summary>给人看的原因（弹窗 / 日志直接用）。</summary>
        public string Message = "";

        /// <summary>定位细节：出问题的 URL / 文件名 / 服务器返回（不给玩家看，给排查的人看）。</summary>
        public string Detail = "";

        /// <summary>构造一个错误。</summary>
        public static RevHotError Of(RevHotErrorCode code, string message, string detail = null)
        {
            return new RevHotError
            {
                Code = code,
                Message = string.IsNullOrEmpty(message) ? code.ToString() : message,
                Detail = detail ?? string.Empty,
            };
        }

        /// <summary>人话展示（弹窗 / 日志直接用）。</summary>
        public override string ToString()
        {
            string text = "[" + Code + "] " + Message;
            if (string.IsNullOrEmpty(Detail)) return text;
            return text + "\n" + Detail;
        }
    }

    /// <summary>
    /// 热更内部用异常做"控制流"（跳出到重试 / 换源 / 取消逻辑），对外一律转成 RevHotError ——
    /// 业务永远只会拿到 RevHotUpdateResult，不会收到异常。
    /// </summary>
    public sealed class RevHotException : Exception
    {
        /// <summary>携带的错误信息（内部流转用）。</summary>
        public readonly RevHotError Error;

        /// <summary>是否值得重试（网络抖动 = 是；磁盘满 / 大版本不匹配 = 否）。</summary>
        public readonly bool Retryable;

        public RevHotException(RevHotError error, bool retryable) : base(error.ToString())
        {
            Error = error;
            Retryable = retryable;
        }
    }
}
