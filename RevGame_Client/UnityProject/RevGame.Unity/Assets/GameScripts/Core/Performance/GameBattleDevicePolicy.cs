using System;

namespace RevGame.Core
{
    /// <summary>战斗显示预算生效的平台。测试可直接传平台，不需要启动 Unity。</summary>
    public enum GameBattlePlatform
    {
        /// <summary>平台未知时保留基础绘制路径和原本的显示设置。</summary>
        Other,
        /// <summary>Windows 使用实际图形适配器的拓扑。</summary>
        Windows,
        /// <summary>Android 按屏幕刷新率和热状态管理出帧预算。</summary>
        Android,
        /// <summary>普通网页 Player 由页面持有画布，禁用托管工作线程。</summary>
        Web
    }

    /// <summary>当前 Unity 选中的 Windows 硬件适配器类型。</summary>
    public enum GameBattleGpuKind
    {
        /// <summary>报告缺失、匹配有歧义或平台未提供此接口。</summary>
        Unknown,
        /// <summary>操作系统报告为集成式显卡。</summary>
        Integrated,
        /// <summary>操作系统报告为独立式显卡。</summary>
        Discrete
    }

    /// <summary>战斗表现预算的三个档位。档位不参与战斗规则与随机结果。</summary>
    public enum GameBattleDeviceTier
    {
        /// <summary>资源紧张或持续掉帧时使用较小预算。</summary>
        Economy,
        /// <summary>设备能力尚未确认时使用中间预算。</summary>
        Balanced,
        /// <summary>设备具备更大预算且符合当前平台条件。</summary>
        High
    }

    /// <summary>战斗宿主主线程发布的只读计划。表现和独立计算从同一个计划决定执行路径。</summary>
    public readonly struct GameBattleDevicePlan
    {
        /// <summary>此计划相应的平台。</summary>
        public GameBattlePlatform Platform { get; }
        /// <summary>当前使用的适配器类型。Android 与网页保持未知。</summary>
        public GameBattleGpuKind GpuKind { get; }
        /// <summary>当前表现预算。</summary>
        public GameBattleDeviceTier Tier { get; }
        /// <summary>能否使用敌潮 GPU 插值。绘制方还要核对材质和活跃对象数。</summary>
        public bool AllowGpuInterpolation { get; }
        /// <summary>使用 GPU 插值的最少活跃敌人数，单位为个。</summary>
        public int GpuMinimumSprites { get; }
        /// <summary>Android 帧率目标，单位帧每秒。零代表保留宿主设置。</summary>
        public int AndroidFrameRate { get; }
        /// <summary>网页绘制缓冲的像素总预算。零代表由宿主决定。</summary>
        public int WebPixelBudget { get; }

        /// <summary>由预算策略统一构造。调用者以一份完整的值读取路径和阈值。</summary>
        internal GameBattleDevicePlan(GameBattlePlatform platform, GameBattleGpuKind gpuKind, GameBattleDeviceTier tier,
            bool gpu, int gpuMinimum, int androidFps, int webPixels)
        {
            Platform = platform;
            GpuKind = gpuKind;
            Tier = tier;
            AllowGpuInterpolation = gpu;
            GpuMinimumSprites = gpuMinimum;
            AndroidFrameRate = androidFps;
            WebPixelBudget = webPixels;
        }
    }

    /// <summary>只负责硬件提示和预算选型，数值可在引擎外验证。运行中仅由项目启动宿主在主线程发布档位。</summary>
    public static class GameBattleDevicePolicy
    {
        /// <summary>当前战斗计划。宿主未装配时为零值，调用方按 CPU 串行与兼容绘制处理。</summary>
        public static GameBattleDevicePlan Current { get; private set; }

        /// <summary>主线程在启动、档位变化和退出时发布一次。游戏模型不读取此计划。</summary>
        public static void Publish(GameBattleDevicePlan plan)
        {
            Current = plan;
        }

        /// <summary>用实际适配器、系统内存和逻辑处理器估计基础预算。报告缺失时使用可回退的档位。</summary>
        public static GameBattleDeviceTier InitialTier(GameBattlePlatform platform, GameBattleGpuKind gpuKind,
            int memoryMb, int logicalProcessors, int graphicsMemoryMb)
        {
            if (platform == GameBattlePlatform.Windows)
            {
                if (gpuKind == GameBattleGpuKind.Discrete && graphicsMemoryMb >= 4096 && memoryMb >= 8192 && logicalProcessors >= 8)
                {
                    return GameBattleDeviceTier.High;
                }
                return gpuKind == GameBattleGpuKind.Integrated || logicalProcessors < 4
                    ? GameBattleDeviceTier.Economy : GameBattleDeviceTier.Balanced;
            }
            if (platform == GameBattlePlatform.Android)
            {
                return memoryMb >= 5500 && logicalProcessors >= 6
                    ? GameBattleDeviceTier.Balanced : GameBattleDeviceTier.Economy;
            }
            if (platform == GameBattlePlatform.Web && ((memoryMb > 0 && memoryMb <= 4096) ||
                (logicalProcessors > 0 && logicalProcessors <= 4)))
            {
                return GameBattleDeviceTier.Economy;
            }
            return GameBattleDeviceTier.Balanced;
        }

        /// <summary>把档位转为明确预算。能力不足或测试需要固定显示条件时，沿兼容路径执行。</summary>
        public static GameBattleDevicePlan Create(GameBattlePlatform platform, GameBattleGpuKind gpuKind,
            GameBattleDeviceTier tier, int shaderLevel, int refreshRate, bool fixedTest)
        {
            bool gpu = shaderLevel >= 35 && tier == GameBattleDeviceTier.High &&
                ((platform == GameBattlePlatform.Windows && gpuKind == GameBattleGpuKind.Discrete) ||
                 (platform == GameBattlePlatform.Android && shaderLevel >= 45));
            int gpuMinimum = platform == GameBattlePlatform.Android ? 384 : 192;
            int webPixels = tier == GameBattleDeviceTier.Economy ? 1280 * 720 :
                tier == GameBattleDeviceTier.High ? 1920 * 1080 : 1600 * 900;
            int mobileCap = tier == GameBattleDeviceTier.Economy ? 30 : tier == GameBattleDeviceTier.High ? 120 : 60;
            return new GameBattleDevicePlan(platform, gpuKind, tier, !fixedTest && gpu, gpuMinimum,
                !fixedTest && platform == GameBattlePlatform.Android ? DisplayDivisor(refreshRate, mobileCap) : 0,
                !fixedTest && platform == GameBattlePlatform.Web ? webPixels : 0);
        }

        /// <summary>选择最接近预算上限的屏幕整数分频，避免在 90 Hz 屏幕上要求不均匀的 60 FPS。</summary>
        private static int DisplayDivisor(int refreshRate, int cap)
        {
            int refresh = refreshRate >= 24 && refreshRate <= 360 ? refreshRate : 60;
            int divisor = Math.Max(1, (refresh + cap - 1) / cap);
            return Math.Max(1, refresh / divisor);
        }
    }
}
