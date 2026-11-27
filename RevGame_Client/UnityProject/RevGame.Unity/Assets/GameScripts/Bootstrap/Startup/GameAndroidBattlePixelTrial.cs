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
        /// <summary>当前借用的世界相机；销毁相机后仍须还原 Unity 全局缩放。</summary>
        private Camera _camera;
        /// <summary>持有全局缩放时为真。调用前要求两轴都为 1，确保不接管其他控制方。</summary>
        private bool _ownsScale;
        /// <summary>本交火阶段已试过一次时记录阶段编号，零表示尚未申请。</summary>
        private int _attemptedStage;
        /// <summary>剩余对照窗口数。零表示已下结论、已取消，或由隔离测试手动设置。</summary>
        private int _remainingWindows;
        /// <summary>缩放前一整窗的平均帧间隔，单位秒。</summary>
        private float _beforeSecondsPerFrame;
        /// <summary>缩放前一整窗超出 33.33 ms 的帧占比。</summary>
        private float _beforeLateShare;
        /// <summary>缩放后两窗累计的有效墙钟秒数。</summary>
        private float _trialSeconds;
        /// <summary>缩放后两窗累计的可见帧数。</summary>
        private int _trialFrames;
        /// <summary>缩放后超过 33.33 ms 的可见帧数。</summary>
        private int _trialLate;
        /// <summary>向 Unity 请求的等比缩放值。实际尺寸以相机回读为准。</summary>
        private float _requestedScale = 1f;
        /// <summary>请求缩放前的原生宽度，单位像素。窗口尺寸改变时不能继续沿用这轮对照。</summary>
        private int _nativeWidth;
        /// <summary>请求缩放前的原生高度，单位像素。用于核对试验阶段的输入是否一致。</summary>
        private int _nativeHeight;

        /// <summary>只有自动试验仍在采证时为真；已保留的档位无需继续收集试验窗口。</summary>
        public bool Measuring => _remainingWindows > 0;

        /// <summary>设备预算只减少高像素画面的工作，短边目标至少 800 像素。返回 1 表示沿用原始画布。</summary>
        internal static float CandidateScale(int width, int height)
        {
            int shortSide = Math.Min(width, height);
            return shortSide > 800 && (long)width * height >= 2_000_000L
                ? Math.Max(.75f, 800f / shortSide)
                : 1f;
        }

        /// <summary>要求平均间隔明显下降，或有效减少超线帧且平均间隔基本相当。</summary>
        internal static bool Helps(float before, float beforeLate, float after, float afterLate)
        {
            return after <= before * .92f || (after <= before * 1.02f && afterLate <= beforeLate - .10f);
        }

        /// <summary>同一交火阶段尝试一次候选尺寸。调用者要在收集原生档窗口后调用并重开帧计时。</summary>
        public bool TryBegin(int stage, string scene, float secondsPerFrame, float lateShare)
        {
            if (_attemptedStage == stage)
            {
                return false;
            }

            _attemptedStage = stage;
            if (!TryScale(scene))
            {
                return false;
            }

            _beforeSecondsPerFrame = secondsPerFrame;
            _beforeLateShare = lateShare;
            _trialSeconds = 0;
            _trialFrames = 0;
            _trialLate = 0;
            _remainingWindows = TrialWindows;
            return true;
        }

        /// <summary>汇总两段同条件窗口。尺寸若未实际缩小或收益不足，立即恢复原生档。</summary>
        public string RecordWindow(float seconds, int frames, int late)
        {
            if (_camera == null || _camera.pixelWidth != _nativeWidth || _camera.pixelHeight != _nativeHeight)
            {
                Restore();
                return "android-pixels-viewport-changed";
            }

            _trialSeconds += seconds;
            _trialFrames += frames;
            _trialLate += late;
            if (--_remainingWindows > 0)
            {
                return null;
            }

            bool effective = _camera.scaledPixelWidth < _nativeWidth && _camera.scaledPixelHeight < _nativeHeight;
            if (effective && Helps(_beforeSecondsPerFrame, _beforeLateShare, _trialSeconds / _trialFrames,
                (float)_trialLate / _trialFrames))
            {
                return "android-pixels-kept";
            }

            Restore();
            return effective ? "android-pixels-no-gain" : "android-pixels-no-effective-scale";
        }

        /// <summary>中断在途试验并恢复原尺寸。保留阶段编号，避免暂停或切回来频繁闪动。</summary>
        public void CancelTrial()
        {
            if (Measuring)
            {
                Restore();
            }
        }

        /// <summary>新阶段或退出时归还设置并清除上一个阶段的尝试记录。</summary>
        public void Reset()
        {
            Restore();
            _attemptedStage = 0;
        }

        /// <summary>隔离测试可显式切换单一像素变量。返回 false 代表当前配置无法获取世界相机缩放。</summary>
        public bool SetForComparison(string scene, bool reduced)
        {
            Reset();
            return !reduced || TryScale(scene);
        }

        /// <summary>从 Unity 实际相机读取两套尺寸，供隔离试验核对缩放生效以及退场恢复。</summary>
        public object Snapshot(string scene)
        {
            Camera camera = _camera != null ? _camera : FindCamera(scene);
            return new Dictionary<string, object>
            {
                ["requestedScale"] = _requestedScale,
                ["measuring"] = Measuring,
                ["nativeWidth"] = camera == null ? 0 : camera.pixelWidth,
                ["nativeHeight"] = camera == null ? 0 : camera.pixelHeight,
                ["worldWidth"] = camera == null ? 0 : camera.scaledPixelWidth,
                ["worldHeight"] = camera == null ? 0 : camera.scaledPixelHeight,
                ["bufferWidthScale"] = ScalableBufferManager.widthScaleFactor,
                ["bufferHeightScale"] = ScalableBufferManager.heightScaleFactor
            };
        }

        /// <summary>仅接管指定场景内直接显示的正交主相机。专题试验只启用安卓 Vulkan 的内置管线路径。</summary>
        private bool TryScale(string scene)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan || GraphicsSettings.currentRenderPipeline != null
                || ScalableBufferManager.widthScaleFactor != 1f || ScalableBufferManager.heightScaleFactor != 1f)
            {
                return false;
            }

            Camera camera = FindCamera(scene);
            if (camera == null || camera.allowDynamicResolution || camera.targetTexture != null || !camera.orthographic)
            {
                return false;
            }

            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            float scale = CandidateScale(width, height);
            if (scale >= 1f)
            {
                return false;
            }

            // 先登记归属，再仅在世界相机打开缩放。Screen 尺寸、触屏坐标和 Overlay Canvas 继续沿正常入口。
            _camera = camera;
            _ownsScale = true;
            _requestedScale = scale;
            _nativeWidth = width;
            _nativeHeight = height;
            camera.allowDynamicResolution = true;
            ScalableBufferManager.ResizeBuffers(scale, scale);
            return true;
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
