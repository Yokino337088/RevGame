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
        /// <summary>省电设备只收紧无尽普通弹道的表现容量，独立于全局画质、伤害和预警。</summary>
        public bool LimitOrdinaryProjectiles { get; }

        /// <summary>由预算策略统一构造。调用者以一份完整的值读取路径和阈值。</summary>
        internal GameBattleDevicePlan(GameBattlePlatform platform, GameBattleGpuKind gpuKind, GameBattleDeviceTier tier,
            bool gpu, int gpuMinimum, int androidFps, int webPixels, bool limitOrdinaryProjectiles)
        {
            Platform = platform;
            GpuKind = gpuKind;
            Tier = tier;
            AllowGpuInterpolation = gpu;
            GpuMinimumSprites = gpuMinimum;
            AndroidFrameRate = androidFps;
            WebPixelBudget = webPixels;
            LimitOrdinaryProjectiles = limitOrdinaryProjectiles;
        }
    }

    /// <summary>只负责硬件提示和预算选型，数值可在引擎外验证。运行中仅由项目启动宿主在主线程发布档位。</summary>
    public static class GameBattleDevicePolicy
    {
        /// <summary>安卓自动试档优先争取的帧尾边界，单位秒。等于 35 ms 计为超线。</summary>
        public const float AndroidTailBudgetSeconds = .035f;
        /// <summary>安卓普通局验收的严格帧尾边界，单位秒。等于 40 ms 计为超线。</summary>
        public const float AndroidAcceptanceTailSeconds = .040f;
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

        /// <summary>隔离存档中的普通安卓远征也走正式显示预算。显式基准与固定棋数夹具仍走固定档。</summary>
        public static bool UseProductionBudgetInIsolatedCampaign(GameBattlePlatform platform, bool isolatedOrBenchmark,
            bool explicitBenchmark, bool fixedChapterLoad)
        {
            return platform == GameBattlePlatform.Android && isolatedOrBenchmark && !explicitBenchmark && !fixedChapterLoad;
        }

        /// <summary>把档位转为明确预算。安卓绘制档与帧率独立，正常对局请求系统当前显示刷新率。</summary>
        public static GameBattleDevicePlan Create(GameBattlePlatform platform, GameBattleGpuKind gpuKind,
            GameBattleDeviceTier tier, int shaderLevel, double refreshRate, bool fixedTest)
        {
            bool gpu = shaderLevel >= 35 && tier == GameBattleDeviceTier.High &&
                ((platform == GameBattlePlatform.Windows && gpuKind == GameBattleGpuKind.Discrete) ||
                 (platform == GameBattlePlatform.Android && shaderLevel >= 45));
            int gpuMinimum = platform == GameBattlePlatform.Android ? 384 : 192;
            int webPixels = tier == GameBattleDeviceTier.Economy ? 1280 * 720 :
                tier == GameBattleDeviceTier.High ? 1920 * 1080 : 1600 * 900;
            // 网页的试降只改像素。保持那里的弹道容量固定，便于比较同一个调整项的收益。
            bool limitProjectiles = !fixedTest && tier == GameBattleDeviceTier.Economy &&
                (platform == GameBattlePlatform.Windows || platform == GameBattlePlatform.Android);
            return new GameBattleDevicePlan(platform, gpuKind, tier, !fixedTest && gpu, gpuMinimum,
                !fixedTest && platform == GameBattlePlatform.Android ? AndroidNativeFrameRate(refreshRate) : 0,
                !fixedTest && platform == GameBattlePlatform.Web ? webPixels : 0, limitProjectiles);
        }

        /// <summary>判定降低网页像素是否有实质结果。参数依次是原帧间隔、原慢帧比例、试验帧间隔和试验慢帧比例。</summary>
        public static bool WebPixelReductionHelps(float beforeSeconds, float beforeSlow, float afterSeconds, float afterSlow)
        {
            // 平均间隔明显缩短即可保留。若只是偶发慢帧变少，还须保证平均间隔没有明显变差。
            return afterSeconds <= beforeSeconds * .92f ||
                (afterSeconds <= beforeSeconds * 1.02f && afterSlow <= beforeSlow - .05f);
        }

        /// <summary>请求系统实际刷新率。分数刷新率向上取整，读不到有效值时用 60 避开 Android 默认 30。</summary>
        public static int AndroidNativeFrameRate(double refreshRate)
        {
            return refreshRate >= 1d && refreshRate <= int.MaxValue ? (int)Math.Ceiling(refreshRate) : 60;
        }

        /// <summary>画质观察使用的最低均值目标，按刷新能力留小量计时偏差，不参与设置帧率上限。</summary>
        public static float AndroidAverageGoal(GameBattleDeviceTier tier, int targetFrameRate)
        {
            float requested = tier == GameBattleDeviceTier.High ? 100f : 50f;
            return Math.Min(requested, targetFrameRate * .97f);
        }

        /// <summary>按最近秩 P95 判断调用方选定的边界。完整窗口中至多 5% 的帧可达到该边界。</summary>
        public static bool AndroidTailMeetsGoal(int frames, int framesAtOrOverBudget)
        {
            return frames > 0 && framesAtOrOverBudget >= 0 && framesAtOrOverBudget <= frames / 20;
        }

        /// <summary>已验证的 Web 像素档可沿用到同一远征章的下一回合。无尽按实际波次各自判断。</summary>
        public static bool WebPixelReductionMatchesStage(int acceptedStage, int currentStage)
        {
            return acceptedStage != 0 && (acceptedStage == currentStage ||
                (acceptedStage > 0 && currentStage > 0 && acceptedStage / 100 == currentStage / 100));
        }
    }
}
