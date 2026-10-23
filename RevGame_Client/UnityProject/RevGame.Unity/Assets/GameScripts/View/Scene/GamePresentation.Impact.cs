// 相机由FitCamera每帧适配；震动只叠加零均值偏移，不能让DOShake直接和布局争写同一个Transform。
using DG.Tweening;
using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class GamePresentation
    {
        private Tween _impactTween;
        private float _impactAmplitude;
        private float _impactStarted;
        private void OnEnergyImpact(EnergyBombInfo info)
        {
            _impactTween?.Kill(false);
            _impactStarted = Time.unscaledTime;
            _impactAmplitude = 0.19f;
            _impactTween = DOTween.To(() => _impactAmplitude, x => _impactAmplitude = x, 0f, 0.48f)
                .SetEase(Ease.OutQuad).SetUpdate(true).OnComplete(() => _impactTween = null);
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            // 使用Unity的设备触觉接口；编辑器没有手机马达，真机效果需要设备验收。
            Handheld.Vibrate();
#endif
        }
        private Vector3 EnergyShakeOffset()
        {
            float t = (Time.unscaledTime - _impactStarted) * 100f;
            return new Vector3(Mathf.Sin(t), Mathf.Sin(t * 1.37f)) * _impactAmplitude;
        }
        private void ClearEnergyShake()
        {
            _impactTween?.Kill(false); _impactTween = null; _impactAmplitude = 0f;
        }
    }
}
