// ============================================================
// GameAudio.cs —— 业务事件到声音的映射。
// 不直接创建 AudioSource，播放、同帧去重和加载交给 RevSound；退出场景停止并卸载本游戏声音。
// ============================================================
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed class GameAudio : MonoBehaviour
    {
        private void Start()
        {
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPlaced, owner: this);
            RevEvent.AddEventListener<int>(Events.DamageResolved, OnDamage, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnMerged, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, owner: this);
            RevEvent.AddEventListener<GameScreen>(Events.ScreenChanged, OnScreen, owner: this);
            RevEvent.AddEventListener<EnergyBombInfo>(Events.EnergyBombImpact, OnEnergyBomb, owner: this);
            RevEvent.AddEventListener<RegionPulseInfo>(Events.RegionPulseWarning, OnRegion, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, OnBossSkill, owner: this);
            RevSound.Failed += OnFailed;
        }

        private void OnEnergyBomb(EnergyBombInfo info) { RevSound.Play("EnergyHuge", volume: 0.48f); }
        private void OnRegion(RegionPulseInfo info) { RevSound.Play("RegionWave", volume: 0.22f); }
        private void OnBossSkill(BossSkillInfo info)
        {
            // 多段技能只在首段播完整音效，避免密集叠音；各技能声音名与外部素材登记保持一致。
            if (info.Index == 0) { RevSound.Play("Boss_" + info.Kind, volume: 0.28f); }
        }

        private void OnPlaced(int id)
        {
            RevSound.Play("Place", volume: 0.35f);
        }

        private void OnDamage(int id)
        {
            RevSound.Play("Hit", volume: 0.18f);
        }

        private void OnMerged(int id)
        {
            RevSound.Play("Upgrade", volume: 0.4f);
        }

        private void OnSettle(GameSide winner)
        {
            RevSound.Play(winner == GameSide.Player ? "Victory" : "Defeat", volume: 0.4f);
        }

        private void OnScreen(GameScreen screen)
        {
            // 从按钮触发流程后才播音乐，也适配浏览器必须先有用户手势才能出声的限制。
            if (screen == GameScreen.Battle)
            {
                RevSound.PlayBgm("MainTheme", fadeSeconds: 0.6f, volume: 0.06f);
            }
            else
            {
                RevSound.StopBgm(0.3f);
            }
        }

        private void OnFailed(string name, RevSoundErrorReason reason)
        {
            // 多颗棋同帧命中时，框架只播放一次同名声音以免音量叠爆。
            // Duplicated代表正常去重；其它加载/播放错误仍必须显示出来供排错。
            if (reason == RevSoundErrorReason.Duplicated) { return; }
            RevLog.Warn("声音播放失败：" + name + "，原因：" + reason, "RevGame");
        }

        private void OnDestroy()
        {
            RevEvent.RemoveAllByOwner(this);
            RevSound.Failed -= OnFailed;
            RevSound.StopAll();
            RevSound.UnloadAll();
        }
    }
}
