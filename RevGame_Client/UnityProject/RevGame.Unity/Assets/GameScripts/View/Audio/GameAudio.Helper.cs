using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>
    /// 帮手声音只响应已发生的攻击/保护状态。普通弹幕、蓝束、导弹、水母、爆炸和治疗各有声音，
    /// 高频攻击合并成有限声部，不让同屏几十发弹幕把声音叠成爆音；这些间隔只影响听感，不影响战斗。
    /// </summary>
    public sealed partial class GameAudio
    {
        private static readonly string[] HelperClipNames = { "HelperBarrage", "HelperBeam", "HelperExplosion", "HelperHeal", "HelperJellyfish", "HelperJellyImpact", "HelperMissile" };
        private readonly object _helperAudioOwner = new object();
        private readonly float[] _helperSoundDue = new float[4];
        private int _helperAudioGeneration, _helperHealingTarget;
        private float _helperHealingDue;
        private static float _helperImpactDue;

        private void OpenHelperAudio()
        {
            _helperImpactDue = 0;
            foreach (string name in HelperClipNames) { RevSound.Register(name,"Helper/" + name); }
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, OnHelperShotAudio, owner: _helperAudioOwner);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnHelperAudioPrepare, owner: _helperAudioOwner);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnHelperAudioSettle, owner: _helperAudioOwner);
            RevTimer.Every(.12f, RefreshHelperHealingAudio, domain: RevTimeDomain.Unscaled, owner: _helperAudioOwner);
        }

        private void OnHelperShotAudio(HelperShotInfo info)
        {
            int index = (int)info.Weapon;
            if (index < 0 || index >= _helperSoundDue.Length || Time.unscaledTime < _helperSoundDue[index]) { return; }
            float delay = info.Beam || info.Storm ? 0f : 1.4f;
            float spacing = info.Beam ? .8f : info.Weapon == HelperWeapon.Missile ? .28f : .16f;
            _helperSoundDue[index] = Time.unscaledTime + spacing;
            int generation = _helperAudioGeneration;
            string sound = info.Weapon == HelperWeapon.Beam ? "HelperBeam" : info.Weapon == HelperWeapon.Missile ? "HelperMissile"
                : info.Weapon == HelperWeapon.Jellyfish ? "HelperJellyfish" : "HelperBarrage";
            float volume = info.Beam ? .24f : info.Weapon == HelperWeapon.Missile ? .3f : .17f;
            // 普通积分弹在飞星从HUD到帮手之后才出现，声音同步这个表现延迟；换回合时旧代次拒绝播放。
            RevTimer.After(delay, () =>
            {
                if (generation == _helperAudioGeneration && _battleScreen) { RevSound.Play(sound, volume: volume); }
            }, domain: RevTimeDomain.Unscaled, owner: _helperAudioOwner);
        }

        private void RefreshHelperHealingAudio()
        {
            var battle = RevServices.Get<IBattleService>();
            int target = _battleScreen && battle != null ? battle.HelperProtectionTargetId : 0;
            if (target == 0) { _helperHealingTarget = 0; return; }
            if (target != _helperHealingTarget || Time.unscaledTime >= _helperHealingDue)
            {
                _helperHealingTarget = target; _helperHealingDue = Time.unscaledTime + 1.9f;
                RevSound.Play("HelperHeal", volume: .2f);
            }
        }

        /// <summary>弹体首次碰到目标时调用一次；跨弹体再用短间隔合并，视觉不为声音再次发送伤害事件。</summary>
        public static void PlayHelperImpact(HelperWeapon weapon)
        {
            // 编辑器资源检查可逐步采样爆炸帧；非Play采样不能启动音频宿主或污染场景。
            if (!Application.isPlaying) { return; }
            if (Time.unscaledTime < _helperImpactDue) { return; }
            _helperImpactDue = Time.unscaledTime + .12f;
            RevSound.Play(weapon == HelperWeapon.Missile ? "HelperExplosion" : "HelperJellyImpact", volume: weapon == HelperWeapon.Missile ? .32f : .2f);
        }

        private void OnHelperAudioPrepare(int round) { ResetHelperAudio(); }
        private void OnHelperAudioSettle(GameSide side) { ResetHelperAudio(); }
        private void ResetHelperAudio()
        {
            _helperAudioGeneration++; _helperHealingTarget = 0; _helperHealingDue = 0;
            for (int i = 0; i < _helperSoundDue.Length; i++) { _helperSoundDue[i] = 0; }
        }
        private void CloseHelperAudio()
        {
            ResetHelperAudio();
            RevTimer.CancelAllOf(_helperAudioOwner);
            RevEvent.RemoveAllByOwner(_helperAudioOwner);
            foreach (string name in HelperClipNames) { RevSound.Unload(name); RevSound.Unregister(name); }
        }
    }
}
