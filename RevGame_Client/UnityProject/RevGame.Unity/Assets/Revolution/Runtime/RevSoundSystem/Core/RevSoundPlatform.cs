// ============================================================
// RevSoundPlatform.cs —— 音效的平台能力表（小游戏 / WebGL 的特殊约束都收在这里）
//
// 位置：Runtime\RevSoundSystem\Core\
//
// 【为什么单独一张表】
//   "能不能直接播、要不要先等用户点一下、被系统打断后该怎么办" —— 这些是**平台事实**，
//   不是业务开关。集中在一处，业务查一次就知道该不该额外处理，
//   而不是等到真机上"没声音、也不报错"时再来回试。
//
// 【★ 小游戏 / WebGL 上的三条硬约束（都不报错，只是"没声音 / 串音 / 回来不响"）】
//   ① 首次播放必须由用户手势触发 —— 浏览器的自动播放策略（iOS 上尤其严）。
//      所以"登录按钮 / 开始游戏按钮被点下"的那一次，是把 BGM 放出去的**唯一机会**：
//      进游戏就播 BGM 的写法在这些平台上必然静默失败。
//   ② AudioClip 的导入设置必须是 Decompress On Load，且不能使用平台不支持的压缩格式 ——
//      否则从 AB 里加载出来的 clip 是"哑的"（Unity 不报错、日志也没有）。
//      这是"音频没声音"最常见的原因，且排查方向极易被带偏。
//   ③ 切后台 / 来电会打断音频：回到前台后需要重新播（配合 RevAppLifecycle.Resumed 处理）。
//
// 【框架为什么只给表、不接 SDK】
//   微信 / 抖音各自的音频接口（InnerAudioContext 那一套）属于平台专属能力，
//   按本项目"框架只做通用能力"的取舍，不把它们编进框架；
//   但业务可以随时查这张表来决定"要不要走平台自己的音频通道"。
// ============================================================
namespace Revolution
{
    /// <summary>音效的平台能力表（只读；运行期间不变）。</summary>
    public static class RevSoundPlatform
    {
        /// <summary>
        /// 音频是不是由"浏览器 / 小游戏运行时"接管播放（WebGL / 小游戏 = true）。
        /// <para>true 时的三条硬约束见本文件头注释（用户手势解锁 / Decompress On Load / 后台打断）。</para>
        /// </summary>
        public static bool AudioManagedByPlatform
        {
#if UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_WEBGL
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>
        /// 首次播放是否需要"用户手势"解锁（WebGL / 小游戏 = true）。
        /// <para>★ 用法：把进游戏就播 BGM 的那句挪到"第一个按钮点击之后"，
        /// 或者在那次点击里补一句预播（播一个音量为 0 的短音），把音频通道先打开。</para>
        /// </summary>
        public static bool NeedsUserGestureUnlock
        {
#if UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_WEBGL
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>一行提示：当前平台的音频注意事项（打日志 / 排查时直接打它）。</summary>
        public static string Note
        {
            get
            {
                if (!AudioManagedByPlatform) { return "音频由引擎直接播放，无特殊约束"; }
                return "小游戏 / WebGL：首次播放需用户手势解锁；AudioClip 必须 Decompress On Load；切后台返回后需重播";
            }
        }
    }
}
