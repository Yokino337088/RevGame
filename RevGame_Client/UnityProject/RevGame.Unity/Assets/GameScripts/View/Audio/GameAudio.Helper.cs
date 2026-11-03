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
        /// <summary>帮手的固定资源别名，随场景注册/卸载；不是玩法数值，不维护额外音频加载系统。</summary>
        private static readonly string[] HelperClipNames = { "HelperBarrage", "HelperBeam", "HelperExplosion", "HelperHeal", "HelperJellyfish", "HelperJellyImpact", "HelperMissile" };
        /// <summary>帮手监听与延迟声音独占所有者，重入/退场成对取消，避免误清其它声部。</summary>
        private readonly object _helperAudioOwner = new object();
        /// <summary>按武器保存现实秒到期时刻，长度对应四种HelperWeapon；只限制听感。</summary>
        private readonly float[] _helperSoundDue = new float[4];
        /// <summary>回合代次与最近治疗目标；旧延迟回调不得在新回合发声。</summary>
        private int _helperAudioGeneration, _helperHealingTarget;
        /// <summary>治疗提示的下一次允许现实秒时刻；不随目标频繁切换重置。</summary>
        private float _helperHealingDue;
        /// <summary>所有帮手弹体共享的命中声期限，跨弹体也只能按固定间隔播放。</summary>
        private static float _helperImpactDue;
        /// <summary>跨武器的全局开火间隔，现实秒；切换武器也不能绕过，延迟回调还要遵守播放时的控频。</summary>
        private readonly GameAudioRateLimit _helperShotRate = new GameAudioRateLimit();
        /// <summary>延迟回调的声音播放门禁，与申请门禁分开，防止延迟弹幕和即时光束在同帧叠播。</summary>
        private readonly GameAudioRateLimit _helperPlaybackRate = new GameAudioRateLimit();

        /// <summary>登记固定资源与事件，治疗观察计时器归独立owner；退出时对称解除。</summary>
        private void OpenHelperAudio()
        {
            _helperImpactDue = 0;
            foreach (string name in HelperClipNames)
            {
                RevSound.Register(name, "Helper/" + name);
            }
            RevEvent.AddEventListener<HelperShotInfo>(Events.HelperShot, OnHelperShotAudio, owner: _helperAudioOwner);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnHelperAudioPrepare, owner: _helperAudioOwner);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnHelperAudioSettle, owner: _helperAudioOwner);
            RevTimer.Every(.12f, RefreshHelperHealingAudio, domain: RevTimeDomain.Unscaled, owner: _helperAudioOwner);
        }
        /// <summary>先武器/全场预检，再预约延迟；回调核对代次与实际播放门禁，拒绝不影响已结算的发射。</summary>
        private void OnHelperShotAudio(HelperShotInfo info)
        {
            int index = (int)info.Weapon;

            if (index < 0 || index >= _helperSoundDue.Length || Time.unscaledTime < _helperSoundDue[index] || !_helperShotRate.Try(Time.unscaledTime, GameAudioRateLimit.HelperSeconds))
            {
                return;
            }
            float delay = info.Beam || info.Storm ? 0f : 1.4f;
            // 间隔只控制听感：普通帮手声音最多约每秒两次，光束最多每1.5秒一次；不减少真实发射。
            float spacing = info.Beam ? 1.5f : info.Weapon == HelperWeapon.Missile ? .65f : .5f;
            _helperSoundDue[index] = Time.unscaledTime + spacing;
            int generation = _helperAudioGeneration;
            string sound = info.Weapon == HelperWeapon.Beam ? "HelperBeam" : info.Weapon == HelperWeapon.Missile ? "HelperMissile"
                : info.Weapon == HelperWeapon.Jellyfish ? "HelperJellyfish" : "HelperBarrage";
            float volume = info.Beam ? .24f : info.Weapon == HelperWeapon.Missile ? .3f : .17f;
            // 普通积分弹在飞星从HUD到帮手之后才出现，声音同步这个表现延迟；换回合时旧代次拒绝播放。
            RevTimer.After(delay, () =>
            {
                if (generation == _helperAudioGeneration && _battleScreen && _helperPlaybackRate.Try(Time.unscaledTime, GameAudioRateLimit.HelperSeconds))
                {
                    RevSound.Play(sound, volume: volume);
                }
            }, domain: RevTimeDomain.Unscaled, owner: _helperAudioOwner);
        }
        /// <summary>只在实际存在保护目标时低频提示，目标换人不绕过间隔，停止治疗后不继续提示。</summary>
        private void RefreshHelperHealingAudio()
        {
            var battle = RevServices.Get<IBattleService>();
            int target = _battleScreen && battle != null ? battle.HelperProtectionTargetId : 0;

            if (target == 0)
            {
                _helperHealingTarget = 0;
                return;
            }
            if (Time.unscaledTime >= _helperHealingDue)
            {
                _helperHealingTarget = target;
                _helperHealingDue = Time.unscaledTime + 3f;
                RevSound.Play("HelperHeal", volume: .2f);
            }
        }

        /// <summary>弹体首次碰到目标时调用一次；跨弹体再用短间隔合并，视觉不为声音再次发送伤害事件。</summary>
        public static void PlayHelperImpact(HelperWeapon weapon)
        {
            // 编辑器资源检查可逐步采样爆炸帧；非Play采样不能启动音频宿主或污染场景。
            if (!Application.isPlaying)
            {
                return;
            }
            if (Time.unscaledTime < _helperImpactDue)
            {
                return;
            }
            _helperImpactDue = Time.unscaledTime + .4f;
            RevSound.Play(weapon == HelperWeapon.Missile ? "HelperExplosion" : "HelperJellyImpact", volume: weapon == HelperWeapon.Missile ? .32f : .2f);
        }
        private void OnHelperAudioPrepare(int round)
        {
            ResetHelperAudio();
        }
        private void OnHelperAudioSettle(GameSide side)
        {
            ResetHelperAudio();
        }
        /// <summary>阶段结束令旧延迟回调失效并复位全部期限；保留注册供当前场景下一回合复用。</summary>
        private void ResetHelperAudio()
        {
            _helperAudioGeneration++;
            _helperHealingTarget = 0;
            _helperHealingDue = 0;
            _helperShotRate.Reset();
            _helperPlaybackRate.Reset();
            _helperImpactDue = 0;
            for (int i = 0; i < _helperSoundDue.Length; i++)
            {
                _helperSoundDue[i] = 0;
            }
        }
        /// <summary>场景销毁的最终收尾：先失效/取消监听计时器，再卸资源和别名。</summary>
        private void CloseHelperAudio()
        {
            ResetHelperAudio();
            RevTimer.CancelAllOf(_helperAudioOwner);
            RevEvent.RemoveAllByOwner(_helperAudioOwner);
            foreach (string name in HelperClipNames)
            {
                RevSound.Unload(name);
                RevSound.Unregister(name);
            }
        }
    }
}
