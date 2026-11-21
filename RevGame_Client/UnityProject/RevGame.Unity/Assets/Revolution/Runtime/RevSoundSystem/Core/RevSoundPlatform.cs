// RevSoundPlatform.cs —— 根据运行宿主声明音频能力。
// 编辑器沿用桌面引擎行为，网页与小游戏由实际宿主控制首次播放和前后台恢复。
namespace Revolution
{
    /// <summary>音频播放能力表，供业务选择首次播放时机和恢复策略。</summary>
    public static class RevSoundPlatform
    {
        /// <summary>网页或小游戏 Player 返回 true。切到 WebGL 构建目标的桌面编辑器仍用本地音频。</summary>
        public static bool AudioManagedByPlatform
        {
            get
            {
#if (UNITY_WEBGL || UNITY_WEIXINMINIGAME || UNITY_BYTEDANCE_MINIGAME || UNITY_BYTEDANCEMINIGAME) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>是否应在用户点击或触摸后尝试播放，具体的解锁行为由目标浏览器或小游戏宿主决定。</summary>
        public static bool NeedsUserGestureUnlock => AudioManagedByPlatform;

        /// <summary>提示当前宿主的播放注意事项，调用方可用于诊断日志。</summary>
        public static string Note
        {
            get
            {
                if (!AudioManagedByPlatform)
                {
                    return "音频由桌面编辑器或原生引擎播放";
                }
                return "小游戏 / WebGL：在用户手势中尝试首次播放，按目标宿主选择导入设置，切回前台时检查播放状态";
            }
        }
    }
}
