// Boss音频目录属于场景表现层：技能枚举对应声音资源，数值/技能轮换由BattleService负责。
// 六十个主动技能及狂暴复用已授权的元素音色；显式别名避免拼出不存在的Boss_StarStorm等文件。
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class GameAudio
    {
        /// <summary>与BossSkillKind一一对应的真实资源名；内容为美术绑定，不是影响胜负的策划数值。</summary>
        private static readonly string[] BossClipNames =
        {
            "Boss_Cleave", "Boss_Snipe", "Boss_Bombard", "Boss_BulletRing", "Boss_BlinkBlast",
            "Boss_LaserSpin", "Boss_SoulDrain", "Boss_Summon", "Boss_Enrage", "Boss_MeteorShower",
            "Boss_CrossLaser", "Boss_FrostNova", "Boss_ChainLightning", "Boss_PoisonMarsh",
            "Boss_FlameWall", "Boss_IceLance", "Boss_GravityWell", "Boss_DoomSigil",
            "Boss_MeteorShower", "Boss_GravityWell", "Boss_FlameWall", "Boss_Snipe",
            "Boss_BulletRing", "Boss_SoulDrain", "Boss_PoisonMarsh", "Boss_ChainLightning",
            "Boss_FrostNova", "Boss_IceLance", "Boss_CrossLaser", "Boss_Summon",
            "Boss_MeteorShower", "Boss_MeteorShower", "Boss_FlameWall", "Boss_FlameWall", "Boss_FlameWall", "Boss_BulletRing", "Boss_BulletRing", "Boss_BulletRing", "Boss_BulletRing", "Boss_PoisonMarsh", "Boss_PoisonMarsh", "Boss_PoisonMarsh", "Boss_PoisonMarsh", "Boss_IceLance", "Boss_IceLance", "Boss_IceLance", "Boss_CrossLaser", "Boss_CrossLaser", "Boss_CrossLaser", "Boss_MeteorShower", "Boss_MeteorShower", "Boss_FlameWall", "Boss_FlameWall", "Boss_BulletRing", "Boss_BulletRing", "Boss_PoisonMarsh", "Boss_PoisonMarsh", "Boss_IceLance", "Boss_IceLance", "Boss_CrossLaser", "Boss_CrossLaser"
        };

        /// <summary>固定每种技能一个句柄与节流时间；重复段替换自己上一段，不无限叠音，也不停止其它音效。</summary>
        private readonly RevSoundHandle[] _bossAudioHandles = new RevSoundHandle[BossClipNames.Length];
        private readonly float[] _bossAudioReadyAt = new float[BossClipNames.Length];
        private readonly string[] _bossAudioAliases = new string[BossClipNames.Length];
        public int BossAudioRequestCount { get; private set; }
        public string LastBossAudioResource { get; private set; }

        /// <summary>返回实际资源名供Editor逐项核对；无效枚举给出空值，不能默默映射成任意技能。</summary>
        public static string GetBossSkillSoundName(BossSkillKind kind)
        {
            int index = (int)kind;
            return index >= 0 && index < BossClipNames.Length ? BossClipNames[index] : null;
        }

        /// <summary>场景独占这些别名，销毁时成对注销。Frame去重仍由RevSound处理，业务层不创建AudioSource。</summary>
        private void OpenBossAudio()
        {
            for (int index = 0; index < BossClipNames.Length; index++)
            {
                string alias = "Boss_" + (BossSkillKind)index;
                _bossAudioAliases[index] = alias;
                if (!RevSound.Register(alias, BossClipNames[index]))
                {
                    RevLog.Warn("Boss声音映射注册失败：" + alias, "RevGame");
                }
            }
            RevEvent.AddEventListener<int>(Events.PhaseEnterPrepare, _ => StopBossAudio(), owner: this);
            RevEvent.AddEventListener<GameSide>(Events.PhaseEnterSettle, _ => StopBossAudio(), owner: this);
        }

        /// <summary>每段技能均有声音反馈；同技能最短0.18真实秒，并用较轻的续段音量保留层次，避免高频Boss遮住所有游戏声音。</summary>
        private void PlayBossSkillAudio(BossSkillInfo info)
        {
            int index = (int)info.Kind;
            if (index < 0 || index >= BossClipNames.Length)
            {
                RevLog.Warn("Boss声音未映射：" + info.Kind, "RevGame");
                return;
            }
            if (Time.unscaledTime < _bossAudioReadyAt[index]) { return; }
            _bossAudioReadyAt[index] = Time.unscaledTime + 0.18f;
            _bossAudioHandles[index].Stop();
            _bossAudioHandles[index] = RevSound.Play(_bossAudioAliases[index], volume: info.Index == 0 ? 0.38f : 0.23f);
            if (!_bossAudioHandles[index].IsEmpty)
            {
                BossAudioRequestCount++;
                LastBossAudioResource = BossClipNames[index];
            }
        }

        private void StopBossAudio()
        {
            for (int index = 0; index < _bossAudioHandles.Length; index++)
            {
                _bossAudioHandles[index].Stop();
                _bossAudioHandles[index] = default;
                _bossAudioReadyAt[index] = 0f;
            }
        }

        private void CloseBossAudio()
        {
            StopBossAudio();
            foreach (string alias in _bossAudioAliases)
            {
                if (!string.IsNullOrEmpty(alias)) { RevSound.Unregister(alias); }
            }
        }
    }
}
