using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RevGame.Bootstrap
{
    /// <summary>由战斗设备设置持有的安卓 Vulkan 相机试验。只借用当前交火场景，离开后归还全局缩放。</summary>
    internal sealed class GameAndroidBattlePixelTrial
    {
        /// <summary>两个完整窗口用于比较。请求尺寸后的第一帧由外层丢弃。</summary>
        private const int TrialWindows = 2;
        /// <summary>常规低档的最省像素比例，自动试验和隔离对照使用同一比例。</summary>
        private const float EconomyScale = .60f;
        /// <summary>常规低档允许的最短世界画面边长，单位像素。</summary>
        internal const int EconomyShortSide = 640;
        /// <summary>第一、四章已经验证常规低档后，还可试用的更低比例。</summary>
        private const float DeepEconomyScale = .55f;
        /// <summary>仅重绘制章节的额外试档可使用的世界画面短边下限，单位像素。</summary>
        internal const int DeepEconomyShortSide = 590;
        /// <summary>Unity 2022.3 的 Vulkan 等比缩放每档相差 0.05，原生尺寸是第 20 档。</summary>
        private const int VulkanScaleSteps = 20;
        /// <summary>由清晰到省像素的四个比例。常规低档和重绘制章节的额外低档各有验证门槛。</summary>
        private static readonly float[] Candidates = { .75f, 2f / 3f, EconomyScale, DeepEconomyScale };
        /// <summary>当前借用的世界相机；销毁相机后仍须还原 Unity 全局缩放。</summary>
        private Camera _camera;
        /// <summary>持有全局缩放时为真。调用前要求两轴都为 1，确保不接管其他控制方。</summary>
        private bool _ownsScale;
        /// <summary>本交火阶段下一个候选序号，换阶段由设备宿主清零。</summary>
        private int _nextCandidate;
        /// <summary>最近一次实测保留的等比缩放。试验失败或中断退回这里。</summary>
        private float _retainedScale = 1f;
        /// <summary>剩余对照窗口数。零表示已下结论、已取消，或由隔离测试手动设置。</summary>
        private int _remainingWindows;
        /// <summary>缩放前一整窗的平均帧间隔，单位秒。</summary>
        private float _beforeSecondsPerFrame;
        /// <summary>试验前一整窗达到或超过首选帧尾预算的帧占比。</summary>
        private float _beforeLateShare;
        /// <summary>缩放后两窗累计的有效墙钟秒数。</summary>
        private float _trialSeconds;
        /// <summary>缩放后两窗累计的可见帧数。</summary>
        private int _trialFrames;
        /// <summary>缩放后达到或超过首选帧尾预算的可见帧数。</summary>
        private int _trialLate;
        /// <summary>向 Unity 请求的等比缩放值。实际尺寸以相机回读为准。</summary>
        private float _requestedScale = 1f;
        /// <summary>请求缩放前的原生宽度，单位像素。窗口尺寸改变时不能继续沿用这轮对照。</summary>
        private int _nativeWidth;
        /// <summary>请求缩放前的原生高度，单位像素。用于核对试验阶段的输入是否一致。</summary>
        private int _nativeHeight;
        /// <summary>试验前有效的相机宽度，单位像素。第二档要相对于已保留的第一档继续缩小。</summary>
        private int _beforeWidth;
        /// <summary>试验前有效的相机高度，单位像素。两轴都要确实缩小才比较收益。</summary>
        private int _beforeHeight;

        /// <summary>只有自动试验仍在采证时为真；已保留的档位无需继续收集试验窗口。</summary>
        public bool Measuring => _remainingWindows > 0;

        /// <summary>完整窗口平均低于普通局 45 FPS 下限，且至少四分之一的帧达到首选帧尾预算。小样本仍等两窗。</summary>
        internal static bool IsSevereWindow(float seconds, int frames, int late)
        {
            return frames >= 24 && frames < seconds * 45f && late >= (frames + 3) / 4;
        }

        /// <summary>把高像素画布的候选对齐 Vulkan 可用档位。画质短边下限向上取档，返回 1 表示原生。</summary>
        internal static float CandidateScale(int width, int height, float scale = .75f, int minimumShortSide = 800)
        {
            int shortSide = Math.Min(width, height);
            if (shortSide <= minimumShortSide || (long)width * height < 2_000_000L)
            {
                return 1f;
            }

            // 1080 短边要求至少 720 时，直接请求 2/3 会被 Vulkan 取到 .65，实际只画 702 像素。
            // 先对画质下限向上取档，再与候选取较清晰的一档，验证、保留和下一回合复用才能使用同一规则。
            int minimumStep = (int)Math.Ceiling(minimumShortSide * (double)VulkanScaleSteps / shortSide);
            int preferredStep = (int)Math.Round(scale * VulkanScaleSteps);
            return (float)Math.Max(minimumStep, preferredStep) / VulkanScaleSteps;
        }

        /// <summary>候选档要改善平均值或尾部，并维持已经达到的首选帧尾预算。</summary>
        internal static bool Helps(float before, float beforeLate, float after, float afterLate)
        {
            if ((beforeLate <= .05f && afterLate > .05f) || afterLate > beforeLate + .05f)
            {
                return false;
            }
            bool tailImproved = afterLate <= beforeLate - .10f || (beforeLate > .05f && afterLate <= .05f);
            return after <= before * .92f || (after <= before * 1.02f && tailImproved);
        }

        /// <summary>同一交火阶段按顺序试档。重绘制章节已保留 0.60 档且仍有尾部压力时，才允许试 0.55。</summary>
        public bool TryBegin(string scene, int minimumShortSide, bool economy, float secondsPerFrame, float lateShare)
        {
            // 未满足资格的候选先保留。以后升温或战况变化仍能在已验证比例上继续试。
            int count = 2;
            if (economy && _retainedScale < 1f)
            {
                count++;
                if (minimumShortSide == DeepEconomyShortSide && _retainedScale <= EconomyScale && lateShare > .05f)
                {
                    count++;
                }
            }
            while (_nextCandidate < count)
            {
                float preferred = Candidates[_nextCandidate++];
                if (TryScale(scene, preferred, ShortSideFor(preferred, minimumShortSide)))
                {
                    _beforeSecondsPerFrame = secondsPerFrame;
                    _beforeLateShare = lateShare;
                    _trialSeconds = 0;
                    _trialFrames = 0;
                    _trialLate = 0;
                    _remainingWindows = TrialWindows;
                    return true;
                }
            }
            return false;
        }

        /// <summary>汇总同条件窗口。实际短边低于下限、尺寸无效或收益不足时，回到最近一次已验证的档位。</summary>
        public string RecordWindow(float seconds, int frames, int late, int minimumShortSide)
        {
            if (_camera == null || _camera.pixelWidth != _nativeWidth || _camera.pixelHeight != _nativeHeight)
            {
                Reset();
                return "android-pixels-viewport-changed";
            }

            int width = _camera.scaledPixelWidth;
            int height = _camera.scaledPixelHeight;
            if (Math.Min(width, height) < ShortSideFor(_requestedScale, minimumShortSide))
            {
                // 用实际画出的尺寸把关。驱动回读异常的比例不能进入同章下一回合的已验证档位。
                RestoreRetainedScale();
                return "android-pixels-below-minimum";
            }

            _trialSeconds += seconds;
            _trialFrames += frames;
            _trialLate += late;
            if (--_remainingWindows > 0)
            {
                return null;
            }

            bool effective = width < _beforeWidth && height < _beforeHeight;
            if (effective && Helps(_beforeSecondsPerFrame, _beforeLateShare, _trialSeconds / _trialFrames,
                (float)_trialLate / _trialFrames))
            {
                _retainedScale = _requestedScale;
                return "android-pixels-kept";
            }

            RestoreRetainedScale();
            return effective ? "android-pixels-no-gain" : "android-pixels-no-effective-scale";
        }

        /// <summary>中断在途试验并恢复已验证尺寸。未结案的候选可以在完整的新基线后重试。</summary>
        public void CancelTrial()
        {
            if (Measuring)
            {
                _nextCandidate--;
                RestoreRetainedScale();
            }
        }

        /// <summary>同一章节下一回合复用已获益的比例。若画面或短边预算改变，则回到原生重新观察。</summary>
        public bool BeginCampaignRound(string scene, int minimumShortSide)
        {
            CancelTrial();
            // 只继承已验证过的实际尺寸，未结案的候选、旧相机或窗口变化都不能成为新基线。
            if (_retainedScale < 1f && _camera != null && _camera == FindCamera(scene)
                && _camera.pixelWidth == _nativeWidth && _camera.pixelHeight == _nativeHeight
                && Math.Min(_camera.scaledPixelWidth, _camera.scaledPixelHeight) >= ShortSideFor(_retainedScale, minimumShortSide))
            {
                _nextCandidate = 0;
                return true;
            }

            Reset();
            return false;
        }

        /// <summary>更换场景或退场时归还设置，清除所有比例证据。</summary>
        public void Reset()
        {
            Restore();
            _nextCandidate = 0;
            _retainedScale = 1f;
        }

        /// <summary>隔离测试切换单一像素变量。显式 deepEconomy 选择 0.55，失败表示相机不支持本次请求。</summary>
        public bool SetForComparison(string scene, bool reduced, bool economy = false, bool deepEconomy = false)
        {
            Reset();
            if (reduced && deepEconomy)
            {
                return TryScale(scene, DeepEconomyScale, DeepEconomyShortSide);
            }
            // 既有脚本省略 economy 时继续用 0.75 倍对照，从而保持已发布数据的测试口径。
            return !reduced || TryScale(scene, economy ? EconomyScale : .75f, economy ? EconomyShortSide : 800);
        }

        /// <summary>只有低于 0.60 的额外试档能使用更小的短边下限。常规档仍按已有画质下限验证。</summary>
        private static int ShortSideFor(float scale, int minimumShortSide)
        {
            return scale < EconomyScale ? minimumShortSide : Math.Max(EconomyShortSide, minimumShortSide);
        }

        /// <summary>从 Unity 实际相机读取两套尺寸，供隔离试验核对缩放生效以及退场恢复。</summary>
        public object Snapshot(string scene)
        {
            Camera camera = _camera != null ? _camera : FindCamera(scene);
            return new Dictionary<string, object>
            {
                ["requestedScale"] = _requestedScale,
                ["retainedScale"] = _retainedScale,
                ["measuring"] = Measuring,
                ["nativeWidth"] = camera == null ? 0 : camera.pixelWidth,
                ["nativeHeight"] = camera == null ? 0 : camera.pixelHeight,
                ["worldWidth"] = camera == null ? 0 : camera.scaledPixelWidth,
                ["worldHeight"] = camera == null ? 0 : camera.scaledPixelHeight,
                ["bufferWidthScale"] = ScalableBufferManager.widthScaleFactor,
                ["bufferHeightScale"] = ScalableBufferManager.heightScaleFactor
            };
        }

        /// <summary>接管指定场景的直接显示正交相机，或者在已持有的相机上继续试较低档。</summary>
        private bool TryScale(string scene, float preferredScale, int minimumShortSide)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null)
            {
                return false;
            }

            Camera camera = FindCamera(scene);
            if (camera == null || camera.targetTexture != null || !camera.orthographic)
            {
                return false;
            }

            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            if (_ownsScale)
            {
                if (camera != _camera || width != _nativeWidth || height != _nativeHeight)
                {
                    // 原生尺寸已经变化，本窗不能继续当作新画布的基线。
                    Restore();
                    _retainedScale = 1f;
                    _nextCandidate = Candidates.Length;
                    return false;
                }
            }
            else if (camera.allowDynamicResolution || ScalableBufferManager.widthScaleFactor != 1f
                || ScalableBufferManager.heightScaleFactor != 1f)
            {
                return false;
            }

            float scale = CandidateScale(width, height, preferredScale, minimumShortSide);
            if (scale >= _requestedScale - .01f)
            {
                return false;
            }

            // 先登记归属，再仅在世界相机打开缩放。Screen 尺寸、触屏坐标和 Overlay Canvas 继续沿正常入口。
            _camera = camera;
            _ownsScale = true;
            _requestedScale = scale;
            _nativeWidth = width;
            _nativeHeight = height;
            _beforeWidth = camera.scaledPixelWidth;
            _beforeHeight = camera.scaledPixelHeight;
            camera.allowDynamicResolution = true;
            ScalableBufferManager.ResizeBuffers(scale, scale);
            return true;
        }

        /// <summary>回退到最近有效尺寸。原生档可直接归还全局缓冲和相机所有权。</summary>
        private void RestoreRetainedScale()
        {
            _remainingWindows = 0;
            if (_retainedScale >= 1f)
            {
                Restore();
                return;
            }
            _requestedScale = _retainedScale;
            ScalableBufferManager.ResizeBuffers(_retainedScale, _retainedScale);
        }

        /// <summary>按正式战斗场景核对主相机，切场期间大厅或临时相机不会被接管。</summary>
        private static Camera FindCamera(string scene)
        {
            if (string.IsNullOrEmpty(scene))
            {
                return null;
            }

            Camera camera = Camera.main;
            return camera != null && camera.gameObject.scene.name == scene ? camera : null;
        }

        /// <summary>只有取得所有权后才恢复原值。相机已销毁时，仍归还全局缓冲比例。</summary>
        private void Restore()
        {
            if (!_ownsScale)
            {
                return;
            }

            if (_camera != null)
            {
                _camera.allowDynamicResolution = false;
            }
            ScalableBufferManager.ResizeBuffers(1f, 1f);
            _camera = null;
            _ownsScale = false;
            _requestedScale = 1f;
            _remainingWindows = 0;
        }
    }
}
