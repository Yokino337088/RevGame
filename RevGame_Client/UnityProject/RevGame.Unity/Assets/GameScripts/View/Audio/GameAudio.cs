// ============================================================
// GameAudio.cs —— 业务事件到声音的映射。
// 不直接创建 AudioSource，播放、同帧去重和加载交给 RevSound；退出场景停止并卸载本游戏声音。
// ============================================================
using RevGame.Core;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class GameAudio : MonoBehaviour
    {
        /// <summary>本场景是否处于战斗屏；准备事件也会在入场装配期间触发，不能因此提前播放。</summary>
        private bool _battleScreen;
        /// <summary>当前音乐的逻辑资源名，由本组件持有；换章和退场时释放，避免六章音乐累计常驻。</summary>
        private string _currentBgm;

        private void Start()
        {
            RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPlaced, owner: this);
            RevEvent.AddEventListener<int>(Events.DamageResolved, OnDamage, owner: this);
            RevEvent.AddEventListener<int>(Events.PieceMerged, OnMerged, owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, OnSettle, owner: this);
            RevEvent.AddEventListener<GameScreen>(Events.ScreenChanged, OnScreen, owner: this);
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, OnPrepare, owner: this);
            RevEvent.AddEventListener<EnergyBombInfo>(Events.EnergyBombImpact, OnEnergyBomb, owner: this);
            RevEvent.AddEventListener<RegionPulseInfo>(Events.RegionPulseWarning, OnRegion, owner: this);
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, OnBossSkill, owner: this);
            OpenBossAudio();
            OpenHelperAudio();
            RevSound.Failed += OnFailed;
        }

        private void OnEnergyBomb(EnergyBombInfo info) { RevSound.Play("EnergyHuge", volume: 0.48f); }
        private void OnRegion(RegionPulseInfo info) { RevSound.Play("RegionWave", volume: 0.22f); }
        private void OnBossSkill(BossSkillInfo info)
        {
            PlayBossSkillAudio(info);
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
            _battleScreen = screen == GameScreen.Battle;
            if (_battleScreen)
            {
                RefreshChapterMusic();
                RefreshBossAudio();
            }
            else
            {
                CloseBossAudio();
                ReleaseChapterMusic();
            }
        }

        /// <summary>同章下一回合保持播放位置；跨章仍在GameScene时由准备事件选择新音乐。</summary>
        private void OnPrepare(int round)
        {
            if (_battleScreen) { RefreshChapterMusic(); RefreshBossAudio(); }
        }

        /// <summary>章节到音乐的资源绑定，不是玩法数值；六章均有独立曲目。</summary>
        public static string GetChapterMusicName(int chapter)
        {
            switch (chapter)
            {
                case 1: return "Chapter01_CafeByTheSea";
                case 2: return "Chapter02_AzureBlue";
                case 3: return "Chapter03_ToTheSouth";
                case 4: return "Chapter04_Adventure";
                case 5: return "Chapter05_IceWalls";
                case 6: return "Chapter06_Swing";
                default: return "MainTheme";
            }
        }

        /// <summary>只在曲目变化时发起框架异步播放；不预加载其它章，不自己创建AudioSource。</summary>
        private void RefreshChapterMusic()
        {
            string name = GetChapterMusicName(RevServices.GetRequired<IBattleService>().Chapter);
            if (_currentBgm == name) { return; }
            ReleaseChapterMusic();
            _currentBgm = name;
            // 先停旧曲再释放其流式资源，新曲淡入，避免正在读旧Clip时卸载其AB。
            if (RevSound.PlayBgm(name, fadeSeconds: 0.6f, volume: GameAudioSettings.MusicBaseVolume).IsEmpty)
            {
                // 声音总开关关闭或请求被拒绝时不能记成已播放，否则下一次刷新会永远跳过此曲。
                _currentBgm = null;
            }
        }

        /// <summary>先停止再归还声音资源；Unload只释放本曲，不误伤战斗音效。</summary>
        private void ReleaseChapterMusic()
        {
            RevSound.StopBgm();
            if (string.IsNullOrEmpty(_currentBgm)) { return; }
            RevSound.Unload(_currentBgm);
            _currentBgm = null;
        }

        private void OnFailed(string name, RevSoundErrorReason reason)
        {
            // 多颗棋同帧命中时，框架只播放一次同名声音以免音量叠爆。
            // Duplicated代表正常去重；其它加载/播放错误仍必须显示出来供排错。
            if (reason == RevSoundErrorReason.Duplicated) { return; }
            // 首次资源加载异步失败后允许下次准备/屏幕刷新重试，不能留下“曲名相同所以不播”的假状态。
            if (name == _currentBgm) { _currentBgm = null; }
            RevLog.Warn("声音播放失败：" + name + "，原因：" + reason, "RevGame");
        }

        private void OnDestroy()
        {
            CloseHelperAudio();
            CloseBossAudio();
            RevEvent.RemoveAllByOwner(this);
            RevSound.Failed -= OnFailed;
            ReleaseChapterMusic();
            RevSound.StopAll();
            RevSound.UnloadAll();
        }
    }
}
