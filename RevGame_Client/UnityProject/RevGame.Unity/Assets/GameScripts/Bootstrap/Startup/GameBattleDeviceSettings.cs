using System;
using System.Collections.Generic;
using Revolution;
using RevGame.Core;
using RevGame.Endless;
using RevGame.Flow;
using RevGame.Services;
using UnityEngine;

namespace RevGame.Bootstrap
{
    /// <summary>在战斗生存期内应用设备预算。只接触显示与执行路径，游戏规则仍由原有服务推进。</summary>
    internal sealed class GameBattleDeviceSettings : IDisposable
    {
        /// <summary>每个有效窗口的时长，单位秒。慢帧也计入；只有后台、遮挡与非交火阶段不采样。</summary>
        private const float WindowSeconds = 4f;
        /// <summary>安卓 30 FPS 的整帧预算，单位秒。固定阈值也用于原生档和缩放档的比较。</summary>
        private const float AndroidFrameBudget = 1f / 30f;
        /// <summary>Unity 所在平台。Windows Editor 也能检测当前适配器。</summary>
        private readonly GameBattlePlatform _platform;
        /// <summary>Windows 当前渲染适配器的硬件拓扑。</summary>
        private readonly GameBattleGpuKind _gpuKind;
        /// <summary>启动时估计的表现预算，重进玩法从这一档开始。</summary>
        private readonly GameBattleDeviceTier _initial;
        /// <summary>连续稳定时允许尝试的最高档，来源是机器提示与平台能力。</summary>
        private readonly GameBattleDeviceTier _ceiling;
        /// <summary>当前游戏屏幕与真实输入门禁由现有流程负责。</summary>
        private readonly GameFlow _flow;
        /// <summary>进程可见的逻辑处理器数量，仅启动读取。</summary>
        private readonly int _processors;
        /// <summary>Shader 级别，例如 45 表示 Shader Model 4.5。</summary>
        private readonly int _shaderLevel;
        /// <summary>显式采样模式需要固定目标与像素预算，避免在 A/B 中途改变口径。</summary>
        private readonly bool _fixedTest;
        /// <summary>安卓 Vulkan 独有的世界相机试验。其他图形路径不收集像素试验计数。</summary>
        private readonly GameAndroidBattlePixelTrial _androidPixels;
        /// <summary>当前在远征或无尽时为真，退出负责恢复宿主原配置。</summary>
        private bool _active;
        /// <summary>本次进入前记录的移动端帧率设置，含 Unity 默认值 -1。</summary>
        private int _originalFrameRate;
        /// <summary>本次有效采样窗口的累计秒数。</summary>
        private float _elapsed;
        /// <summary>本次有效采样窗口的可见帧数。</summary>
        private int _frames;
        /// <summary>超过当前预算 125% 的可见帧数。</summary>
        private int _slowFrames;
        /// <summary>本窗口超过 30 FPS 帧预算的实际帧数。</summary>
        private int _androidLateFrames;
        /// <summary>平均低于 29 FPS 的连续交火窗口数，使用少量余量避免在接近目标时缩画面。</summary>
        private int _androidUnderTargetWindows;
        /// <summary>连续有超预算迹象的完整窗口数。</summary>
        private int _slowWindows;
        /// <summary>连续稳定的完整窗口数。</summary>
        private int _stableWindows;
        /// <summary>最近一次更新计划的实时时刻，单位秒。升档需要经过冷却期。</summary>
        private float _changedAt;
        /// <summary>前一个 Update 是否也在同一段有效交火，用来过滤属于遮挡或恢复过程的时间。</summary>
        private bool _observePrevious;
        /// <summary>采样对应的远征章回或无尽波次。零表示尚无有效阶段。</summary>
        private int _observationStage;
        /// <summary>网页试降未获益的阶段，本阶段不重复尝试减少像素。</summary>
        private int _webRejectedStage;
        /// <summary>确认较低像素有实际收益的阶段，避免仅因降档后稳定就在同一战况反复升回去。</summary>
        private int _webAcceptedStage;
        /// <summary>试降前的档位。存在时需要完成两个同阶段窗口才接受结果。</summary>
        private GameBattleDeviceTier? _webTrialPrevious;
        /// <summary>试降前可比较的平均帧间隔和慢帧占比。</summary>
        private float _webBeforeTime, _webBeforeSlow;
        /// <summary>试降期间的累计采样时间、帧数及超线帧数。</summary>
        private float _webTrialSeconds;
        /// <summary>试降期间完整的窗口数、实际帧数和慢帧数。</summary>
        private int _webTrialWindows, _webTrialFrames, _webTrialSlowFrames;
        /// <summary>最近的策略决定，用于诊断实际档位与试验状态。</summary>
        private string _lastDecision = "startup";
        /// <summary>Android 热等级，-1 表示接口不可用。其他平台也保持 -1。</summary>
        private int _thermal = -1;
        /// <summary>下次查询系统热状态的时刻，单位秒；失焦后归零，回到前台立即复核。</summary>
        private float _nextThermalAt;
#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>Android 29 起可查热等级，进入战斗才持有一次 Java PowerManager。</summary>
        private AndroidJavaObject _power;
        /// <summary>当前进程的系统热接口不可用时，后续窗口直接使用未知等级。</summary>
        private bool _thermalUnavailable;
#endif

        /// <summary>由启动器装配，读取本地能力并订阅游戏屏幕。读取失败使用未知或兼容档。</summary>
        public GameBattleDeviceSettings()
        {
            _flow = RevServices.GetRequired<GameFlow>();
            _platform = Platform();
            if (_platform == GameBattlePlatform.Android && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
            {
                _androidPixels = new GameAndroidBattlePixelTrial();
            }
            _gpuKind = IdentifyGpu(_platform);
            _processors = SystemInfo.processorCount;
            _shaderLevel = SystemInfo.graphicsShaderLevel;
            int ram = SystemInfo.systemMemorySize;
            _initial = GameBattleDevicePolicy.InitialTier(_platform, _gpuKind, ram, _processors, SystemInfo.graphicsMemorySize);
            _ceiling = _platform == GameBattlePlatform.Android && (ram < 7000 || _processors < 8 || _shaderLevel < 45)
                ? GameBattleDeviceTier.Balanced :
                _platform == GameBattlePlatform.Web && _initial == GameBattleDeviceTier.Economy
                    ? GameBattleDeviceTier.Balanced : GameBattleDeviceTier.High;
            _fixedTest = Array.IndexOf(Environment.GetCommandLineArgs(), "-revBenchmark") >= 0 ||
                RevServices.GetRequired<StoryProgress>().IsIsolated;
            GameBattleDevicePolicy.Publish(Create(_initial));
            RevEvent.AddEventListener<GameScreen>(Events.ScreenChanged, OnScreen, owner: this);
            if (_platform == GameBattlePlatform.Android || _platform == GameBattlePlatform.Web)
            {
                RevEvent.AddEventListener(RevAppLifecycle.Paused, OnSuspending, owner: this);
            }
        }

        /// <summary>诊断记录真实的预算、引擎绘制线程和当前排队策略，不上传设备数据。</summary>
        public object Snapshot()
        {
            var plan = GameBattleDevicePolicy.Current;
            return new Dictionary<string, object>
            {
                ["platform"] = plan.Platform.ToString(),
                ["gpuKind"] = plan.GpuKind.ToString(),
                ["gpuName"] = SystemInfo.graphicsDeviceName,
                ["graphicsApi"] = SystemInfo.graphicsDeviceType.ToString(),
                ["renderThreaded"] = SystemInfo.graphicsMultiThreaded,
                ["tier"] = plan.Tier.ToString(),
                ["active"] = _active,
                ["fixedTest"] = _fixedTest,
                ["cpuLogical"] = _processors,
                ["thermal"] = _thermal,
                ["androidTarget"] = plan.AndroidFrameRate,
                ["actualTargetFrameRate"] = Application.targetFrameRate,
                ["androidWorld"] = _androidPixels?.Snapshot(AndroidBattleScene()),
                ["webPixels"] = plan.WebPixelBudget,
                ["webPixelTrial"] = _webTrialPrevious.HasValue,
                ["ordinaryProjectileLimit"] = plan.LimitOrdinaryProjectiles,
                ["decision"] = _lastDecision,
                ["gpuInterpolation"] = plan.AllowGpuInterpolation,
                ["gpuMinimumSprites"] = plan.GpuMinimumSprites
            };
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>隔离测试固定帧率口径后，独立切换安卓相机像素。请求成功后再回读实际相机尺寸。</summary>
        internal object SetAndroidPixelsForComparison(bool reduced)
        {
            if (_androidPixels == null || !_fixedTest || !RevServices.GetRequired<StoryProgress>().IsIsolated
                || !_active || _flow.IsGameplayBlocked || FightingStage() == 0)
            {
                throw new InvalidOperationException("需要正在交火的安卓隔离测试局");
            }

            if (!_androidPixels.SetForComparison(AndroidBattleScene(), reduced))
            {
                throw new InvalidOperationException("当前相机无法启用安卓 Vulkan 画面缩放");
            }

            return Snapshot();
        }
#endif

        /// <summary>只把当前远征或无尽的正式场景交给相机所有权检查。</summary>
        private string AndroidBattleScene()
        {
            return _flow.Screen == GameScreen.Battle ? GameFlow.BattleSceneName :
                _flow.Screen == GameScreen.Endless ? GameFlow.EndlessSceneName : null;
        }

        /// <summary>识别实际 Player 宿主。编辑器不会冒充 Android 或浏览器来切全局显示设置。</summary>
        private static GameBattlePlatform Platform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
            return GameBattlePlatform.Web;
#elif UNITY_ANDROID && !UNITY_EDITOR
            return GameBattlePlatform.Android;
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return GameBattlePlatform.Windows;
#else
            return GameBattlePlatform.Other;
#endif
        }

        /// <summary>只在实际 Windows 驱动上查询当前 Unity 适配器，其它图形环境保留未知。</summary>
        private static GameBattleGpuKind IdentifyGpu(GameBattlePlatform platform)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (platform == GameBattlePlatform.Windows)
            {
                return GameWindowsGpu.Identify(SystemInfo.graphicsDeviceVendorID, SystemInfo.graphicsDeviceID);
            }
#endif
            return GameBattleGpuKind.Unknown;
        }

        /// <summary>使用当前屏幕刷新率选择分频。缺失或不可信时由纯策略回落到 60 Hz。</summary>
        private GameBattleDevicePlan Create(GameBattleDeviceTier tier)
        {
            int refresh = (int)Math.Round(Screen.currentResolution.refreshRateRatio.value);
            return GameBattleDevicePolicy.Create(_platform, _gpuKind, tier, _shaderLevel, refresh, _fixedTest);
        }

        /// <summary>进入实战才设置显示预算，回大厅立即回收。远征与无尽相互切换时沿用本次持有。</summary>
        private void OnScreen(GameScreen screen)
        {
            bool battle = screen == GameScreen.Battle || screen == GameScreen.Endless;
            if (battle == _active)
            {
                return;
            }
            if (!battle)
            {
                Restore();
                return;
            }

            _active = true;
            _originalFrameRate = Application.targetFrameRate;
            Apply(_initial, "battle-enter");
            if (!_fixedTest && (_platform == GameBattlePlatform.Android || _platform == GameBattlePlatform.Web))
            {
                RevMono.AddUpdate(ObserveFrame, owner: this);
            }
        }

        /// <summary>前台战斗查热状态与真实交火帧间隔。安卓先试单一像素变量，再恢复帧率档位决策。</summary>
        private void ObserveFrame()
        {
            if (!_active || !Application.isFocused)
            {
                _nextThermalAt = 0f;
                _androidPixels?.CancelTrial();
                CancelWebTrial("focus-lost");
                DiscardEvidence();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_platform == GameBattlePlatform.Android && now >= _nextThermalAt)
            {
                _nextThermalAt = now + WindowSeconds;
                int previousThermal = _thermal;
                _thermal = ReadThermal();
                GameBattleDeviceTier allowed = AllowedTier();
                if (GameBattleDevicePolicy.Current.Tier > allowed)
                {
                    _androidPixels?.CancelTrial();
                    Apply(allowed, "thermal-ceiling");
                    return;
                }
                if (previousThermal != _thermal && _androidPixels?.Measuring == true)
                {
                    _androidPixels.CancelTrial();
                    _lastDecision = "android-pixels-thermal-changed";
                    DiscardEvidence();
                    return;
                }
            }

            int stage = _flow.IsGameplayBlocked ? 0 : FightingStage();
            if (stage == 0)
            {
                _androidPixels?.CancelTrial();
                CancelWebTrial("battle-interrupted");
                DiscardEvidence();
                return;
            }
            if (stage != _observationStage)
            {
                _androidPixels?.Reset();
                CancelWebTrial("stage-changed");
                _observationStage = stage;
                DiscardEvidence();
            }
            if (!_observePrevious)
            {
                // 这个 delta 覆盖的是上一次 Update 到现在，中间可能仍在加载、切波、恢复或重建画布。
                _observePrevious = true;
                return;
            }

            var plan = GameBattleDevicePolicy.Current;
            int target = plan.AndroidFrameRate > 0 ? plan.AndroidFrameRate : 60;
            float elapsed = Time.unscaledDeltaTime;
            if (elapsed >= WindowSeconds)
            {
                // 浏览器挂起时可能没有失焦帧。单个间隔不能冒充整段连续交火，长停顿由独立诊断记录。
                _androidPixels?.CancelTrial();
                CancelWebTrial("observation-gap");
                DiscardEvidence();
                return;
            }
            _elapsed += elapsed;
            _frames++;
            if (elapsed > 1.25f / target)
            {
                _slowFrames++;
            }
            if (_androidPixels != null && elapsed > AndroidFrameBudget)
            {
                _androidLateFrames++;
            }
            if (_elapsed < WindowSeconds)
            {
                return;
            }

            if (_webTrialPrevious.HasValue)
            {
                ObserveWebTrialWindow();
                ResetWindow();
                return;
            }

            int frames = _frames, late = _slowFrames, androidLate = _androidLateFrames;
            float seconds = _elapsed;
            bool slow = late * 10 > frames;
            bool stable = late * 50 <= frames && frames >= seconds * target * .96f;
            ResetWindow();

            GameBattleDeviceTier current = plan.Tier;
            GameBattleDeviceTier ceiling = AllowedTier();

            if (_androidPixels != null && ObserveAndroidPixels(stage, plan, seconds, frames, androidLate))
            {
                return;
            }

            _slowWindows = slow ? _slowWindows + 1 : 0;
            _stableWindows = stable ? _stableWindows + 1 : 0;
            if (_slowWindows >= 2 && current > GameBattleDeviceTier.Economy)
            {
                if (_platform == GameBattlePlatform.Web)
                {
                    if (_webRejectedStage != stage)
                    {
                        BeginWebTrial(current, seconds / frames, (float)late / frames);
                    }
                }
                else
                {
                    Apply(current - 1, "slow-windows");
                }
            }
            else if (_stableWindows >= 5 && current < ceiling && now - _changedAt >= 20f &&
                ((_platform == GameBattlePlatform.Web && _webAcceptedStage != stage) ||
                 (_platform == GameBattlePlatform.Android && _thermal >= 0 && _thermal < 2)))
            {
                Apply(current + 1, "stable-windows");
            }
            else if (_platform == GameBattlePlatform.Android && Create(current).AndroidFrameRate != plan.AndroidFrameRate)
            {
                Apply(current, "display-refresh-changed");
            }
        }

        /// <summary>在同一交火阶段比较原生与缩放档。试验窗口占用决策时，帧率目标维持在原值。</summary>
        private bool ObserveAndroidPixels(int stage, GameBattleDevicePlan plan, float seconds, int frames, int late)
        {
            if (_androidPixels.Measuring)
            {
                // 显示刷新率换了，原生档与缩放档已失去同一目标；先还原比例，再应用新刷新率预算。
                if (Create(plan.Tier).AndroidFrameRate != plan.AndroidFrameRate)
                {
                    _androidPixels.CancelTrial();
                    Apply(plan.Tier, "display-refresh-changed");
                    return true;
                }

                string decision = _androidPixels.RecordWindow(seconds, frames, late);
                if (decision != null)
                {
                    _lastDecision = decision;
                    DiscardEvidence();
                }
                return true;
            }

            _androidUnderTargetWindows = frames < seconds * 29f ? _androidUnderTargetWindows + 1 : 0;
            // 两窗持续低于目标才尝试，避免一次特效峰值就降低世界清晰度。
            if (_androidUnderTargetWindows >= 2 && _androidPixels.TryBegin(stage, AndroidBattleScene(),
                seconds / frames, (float)late / frames))
            {
                _lastDecision = "android-pixel-trial";
                DiscardEvidence();
                return true;
            }
            return false;
        }

        /// <summary>热状态中等、未知时最多均衡，严重时只留省电；其余条件继续受设备初始上限约束。</summary>
        private GameBattleDeviceTier AllowedTier()
        {
            if (_platform != GameBattlePlatform.Android || (_thermal >= 0 && _thermal < 2))
            {
                return _ceiling;
            }
            return _thermal >= 3 ? GameBattleDeviceTier.Economy : GameBattleDeviceTier.Balanced;
        }

        /// <summary>真正交火返回可比阶段标记。远征以章回、无尽以波次区分，布阵和结算返回零。</summary>
        private int FightingStage()
        {
            if (_flow.Screen == GameScreen.Battle)
            {
                var battle = RevServices.GetRequired<IBattleService>();
                return battle.Phase == GamePhase.Battle ? battle.Chapter * 100 + battle.Round : 0;
            }
            if (_flow.Screen == GameScreen.Endless)
            {
                var endless = RevServices.GetRequired<EndlessService>();
                return endless.Run?.phase == EndlessPhase.Fighting ? -1 - endless.WaveNumber : 0;
            }
            return 0;
        }

        /// <summary>只改网页像素一项。两段后核对是否得到了实质性改善，未确认前保留原档。</summary>
        private void BeginWebTrial(GameBattleDeviceTier previous, float meanSeconds, float slowShare)
        {
            _webTrialPrevious = previous;
            _webBeforeTime = meanSeconds;
            _webBeforeSlow = slowShare;
            _webTrialWindows = 0;
            _webTrialFrames = 0;
            _webTrialSlowFrames = 0;
            _webTrialSeconds = 0f;
            Apply(previous - 1, "web-pixel-trial");
        }

        /// <summary>累积试降后的两个有效窗口。收益不足恢复原档，同阶段避免反复试降。</summary>
        private void ObserveWebTrialWindow()
        {
            _webTrialFrames += _frames;
            _webTrialSlowFrames += _slowFrames;
            _webTrialSeconds += _elapsed;
            if (++_webTrialWindows < 2)
            {
                return;
            }

            if (GameBattleDevicePolicy.WebPixelReductionHelps(_webBeforeTime, _webBeforeSlow,
                _webTrialSeconds / _webTrialFrames, (float)_webTrialSlowFrames / _webTrialFrames))
            {
                _webTrialPrevious = null;
                _webAcceptedStage = _observationStage;
                _lastDecision = "web-pixel-kept";
                return;
            }

            _webRejectedStage = _observationStage;
            CancelWebTrial("web-pixel-no-gain");
        }

        /// <summary>中断或未见收益时恢复到试验前。没有在途试验就不重新触发画布变更。</summary>
        private void CancelWebTrial(string reason)
        {
            if (!_webTrialPrevious.HasValue)
            {
                return;
            }

            var previous = _webTrialPrevious.Value;
            _webTrialPrevious = null;
            Apply(previous, reason);
        }

        /// <summary>读取 Android 热等级。API 或服务出错后不再重复探查。</summary>
        private int ReadThermal()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_thermalUnavailable)
            {
                return -1;
            }

            try
            {
                if (_power == null)
                {
                    using var api = new AndroidJavaClass("android.os.Build$VERSION");
                    if (api.GetStatic<int>("SDK_INT") < 29)
                    {
                        _thermalUnavailable = true;
                        return -1;
                    }
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _power = activity?.Call<AndroidJavaObject>("getSystemService", "power");
                }
                if (_power == null)
                {
                    _thermalUnavailable = true;
                    return -1;
                }
                return _power.Call<int>("getCurrentThermalStatus");
            }
            catch (AndroidJavaException)
            {
                _thermalUnavailable = true;
                _power?.Dispose();
                _power = null;
                return -1;
            }
#else
            return -1;
#endif
        }

        /// <summary>发布完整计划后应用当前平台的显示预算并记录原因。用新窗口观察生效后的节奏。</summary>
        private void Apply(GameBattleDeviceTier tier, string reason)
        {
            var plan = Create(tier);
            GameBattleDevicePolicy.Publish(plan);
            if (plan.AndroidFrameRate > 0)
            {
                Application.targetFrameRate = plan.AndroidFrameRate;
            }
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
            if (plan.WebPixelBudget > 0)
            {
                RevGame_SetBattlePixelBudget(plan.WebPixelBudget);
            }
#endif
            _changedAt = Time.realtimeSinceStartup;
            _lastDecision = reason;
            DiscardEvidence();
        }

        /// <summary>清空当窗的累计时间、帧数和超出两种预算的计数。</summary>
        private void ResetWindow()
        {
            _elapsed = 0f;
            _frames = 0;
            _slowFrames = 0;
            _androidLateFrames = 0;
        }

        /// <summary>暂停、遮挡或失焦中断对照时，清空窗口、连续升降档与安卓缩放的触发证据。</summary>
        private void DiscardEvidence()
        {
            ResetWindow();
            _slowWindows = 0;
            _stableWindows = 0;
            _androidUnderTargetWindows = 0;
            _observePrevious = false;
        }

        /// <summary>进入后台时取消在途像素试验并清空热采样期限，恢复后的第一帧读取系统现状。</summary>
        private void OnSuspending()
        {
            _nextThermalAt = 0f;
            _androidPixels?.CancelTrial();
            CancelWebTrial("application-paused");
            DiscardEvidence();
        }

        /// <summary>关闭当前战斗预算，在自己的宿主范围内还原帧率、安卓缓冲比例或网页画布。</summary>
        private void Restore()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            RevMono.RemoveAllOf(this);
            _androidPixels?.Reset();
            var previous = GameBattleDevicePolicy.Current;
            if (previous.AndroidFrameRate > 0)
            {
                Application.targetFrameRate = _originalFrameRate;
            }
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
            if (previous.WebPixelBudget > 0)
            {
                RevGame_SetBattlePixelBudget(0);
            }
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            _power?.Dispose();
            _power = null;
#endif
            _thermal = -1;
            _nextThermalAt = 0f;
            _observationStage = 0;
            _webRejectedStage = 0;
            _webAcceptedStage = 0;
            _webTrialPrevious = null;
            _lastDecision = "battle-exit";
            GameBattleDevicePolicy.Publish(Create(_initial));
        }

#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
        /// <summary>向拥有画布的网页发出像素预算，零要求恢复模板默认值。</summary>
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void RevGame_SetBattlePixelBudget(int pixels);
#endif

        /// <summary>销毁启动宿主前解除所有定时、事件与 Java 引用，并回到兼容默认。</summary>
        public void Dispose()
        {
            Restore();
            RevEvent.RemoveAllByOwner(this);
            GameBattleDevicePolicy.Publish(default);
        }
    }
}
