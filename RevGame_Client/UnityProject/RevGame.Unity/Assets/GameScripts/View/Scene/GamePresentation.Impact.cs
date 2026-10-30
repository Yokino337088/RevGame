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
        /// <summary>表现节流使用真实秒；一次范围伤害可能同帧打中几十颗棋，合并为一次震动，避免马达与镜头连续叠爆。</summary>
        private float _nextBossImpactAt;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        private float _nextDeviceVibrationAt;
#endif
        /// <summary>开发验收读取真实命中触发次数；不把“施放成功”误算为命中反馈。</summary>
        public int BossHitFeedbackCount
        {
            get; private set;
        }
        public float ImpactAmplitude => _impactAmplitude;

        private void OnEnergyImpact(EnergyBombInfo info)
        {
            BeginImpact(0.19f, 0.48f);
        }
        /// <summary>只消费唯一伤害出口提交的正伤害快照；躲开、无敌、护盾挡住以及Boss自我强化都不会震屏。</summary>
        private void OnBossSkillHit(BattleHitInfo info)
        {
            if (!IsBossSkillPlayerHit(info) || Time.unscaledTime < _nextBossImpactAt)
            {
                return;
            }
            // 0.16秒是镜头反馈合并窗口，不参与攻速、冷却或伤害，故属于表现配置而不是战斗数值。
            _nextBossImpactAt = Time.unscaledTime + 0.16f;
            BossHitFeedbackCount++;
            BeginImpact(0.13f, 0.3f);
        }
        /// <summary>供业务事件与只读规则检查共用的筛选契约。显式枚举范围排除帮手激光、普通伤害和能量炸弹。</summary>
        public static bool IsBossSkillPlayerHit(BattleHitInfo info)
        {
            return info.TargetSide == GameSide.Player && info.Damage > 0 && info.FromSkill
                && info.DamageKind >= BattleDamageKind.BossCleave && info.DamageKind <= BattleDamageKind.BossFinalEdict;
        }
        /// <summary>相机只有一条衰减Tween，新的轻击保留已有重击强度；偏移由FitCamera统一叠加。</summary>
        private void BeginImpact(float amplitude, float seconds)
        {
            _impactTween?.Kill(false);
            _impactStarted = Time.unscaledTime;
            _impactAmplitude = Mathf.Max(_impactAmplitude, amplitude);
            _impactTween = DOTween.To(() => _impactAmplitude, x => _impactAmplitude = x, 0f, seconds)
                .SetEase(Ease.OutQuad).SetUpdate(true).OnComplete(() => _impactTween = null);

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            // Unity公开设备接口只在真机编译。手机马达每0.65秒最多触发一次；Editor只可验证这条调用链，不能证明真机手感。
            if (Time.unscaledTime >= _nextDeviceVibrationAt)
            {
                _nextDeviceVibrationAt = Time.unscaledTime + 0.65f;
                Handheld.Vibrate();
            }
#endif
        }
        private Vector3 EnergyShakeOffset()
        {
            float t = (Time.unscaledTime - _impactStarted) * 100f;
            return new Vector3(Mathf.Sin(t), Mathf.Sin(t * 1.37f)) * _impactAmplitude;
        }
        private void ClearEnergyShake()
        {
            _impactTween?.Kill(false);
            _impactTween = null;
            _impactAmplitude = 0f;
            _nextBossImpactAt = 0f;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            _nextDeviceVibrationAt = 0f;
#endif
        }
    }
}
