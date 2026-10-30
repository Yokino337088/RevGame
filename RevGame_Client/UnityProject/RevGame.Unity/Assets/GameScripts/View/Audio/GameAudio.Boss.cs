// Boss音频目录属于场景表现层：技能枚举对应声音资源，数值/技能轮换由BattleService负责。
// 六十个主动技能及狂暴各有不同的已授权源音色；显式别名避免拼出不存在的Boss_StarStorm等文件。
using RevGame.Core;
using RevGame.Data;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class GameAudio
    {
        /// <summary>与BossSkillKind一一对应的真实资源名；内容为美术绑定，不是影响胜负的策划数值。</summary>
        private static readonly string[] BossClipNames =
        {
            "Boss_Cleave", // 横扫震地，独立源音色。
            "Boss_Snipe", // 致命狙击，独立源音色。
            "Boss_Bombard", // 三连轰炸，独立源音色。
            "Boss_BulletRing", // 弹幕风暴，独立源音色。
            "Boss_BlinkBlast", // 瞬影爆破，独立源音色。
            "Boss_LaserSpin", // 天光回旋，独立源音色。
            "Boss_SoulDrain", // 灵魂汲取，独立源音色。
            "Boss_Summon", // 召唤影卫，独立源音色。
            "Boss_Enrage", // 狂暴怒吼，独立源音色。
            "Boss_MeteorShower", // 流星陨落，独立源音色。
            "Boss_CrossLaser", // 十字天罚，独立源音色。
            "Boss_FrostNova", // 极寒新星，独立源音色。
            "Boss_ChainLightning", // 连锁雷暴，独立源音色。
            "Boss_PoisonMarsh", // 腐蚀毒沼，独立源音色。
            "Boss_FlameWall", // 烈焰火墙，独立源音色。
            "Boss_IceLance", // 冰晶贯穿，独立源音色。
            "Boss_GravityWell", // 引力坍缩，独立源音色。
            "Boss_DoomSigil", // 末日烙印，独立源音色。
            "Boss_StarStorm", // 星辰风暴，独立源音色。
            "Boss_BlackHole", // 黑洞陷落，独立源音色。
            "Boss_SolarFlare", // 日冕灼潮，独立源音色。
            "Boss_SunBrand", // 烈阳烙印，独立源音色。
            "Boss_TidalSurge", // 怒涛横卷，独立源音色。
            "Boss_Maelstrom", // 回流涡旋，独立源音色。
            "Boss_SwampMaw", // 沼泽巨口，独立源音色。
            "Boss_MireChains", // 泥沼缚射，独立源音色。
            "Boss_FrozenPrison", // 极寒囚笼，独立源音色。
            "Boss_GlacialAegis", // 冰封终焉，独立源音色。
            "Boss_DivineCross", // 十字神罚，独立源音色。
            "Boss_Sanctuary", // 圣域，独立源音色。
            "Boss_CometRails", // 彗星轨道，独立源音色。
            "Boss_AstralShards", // 星屑追爆，独立源音色。
            "Boss_SolarSpears", // 烈阳贯枪，独立源音色。
            "Boss_EmberFan", // 炎羽扇扫，独立源音色。
            "Boss_MagmaRings", // 熔岩环爆，独立源音色。
            "Boss_TidalLances", // 潮汐水枪，独立源音色。
            "Boss_FoamBurst", // 泡沫连爆，独立源音色。
            "Boss_Undertow", // 暗流绞阵，独立源音色。
            "Boss_ReefSpikes", // 礁岩穿刺，独立源音色。
            "Boss_BogSpines", // 腐沼骨刺，独立源音色。
            "Boss_VenomFan", // 毒雾扇潮，独立源音色。
            "Boss_MireRipples", // 泥潭连震，独立源音色。
            "Boss_BlightSeeds", // 疫种爆裂，独立源音色。
            "Boss_SnowCross", // 寒霜十字，独立源音色。
            "Boss_HailBarrage", // 冰雹连坠，独立源音色。
            "Boss_IceFault", // 冰川断层，独立源音色。
            "Boss_JudgementLances", // 裁决光矛，独立源音色。
            "Boss_HaloPulse", // 圣环脉冲，独立源音色。
            "Boss_RunicGrid", // 律令棋阵，独立源音色。
            "Boss_EventHorizon", // 星界崩塌，独立源音色。
            "Boss_ConstellationSeal", // 星座封阵，独立源音色。
            "Boss_PhoenixDescent", // 焚天凤陨，独立源音色。
            "Boss_SolarEclipse", // 日蚀审判，独立源音色。
            "Boss_LeviathanWake", // 巨鲸破浪，独立源音色。
            "Boss_AbyssalCage", // 深渊潮牢，独立源音色。
            "Boss_PlagueBloom", // 瘟疫绽放，独立源音色。
            "Boss_HydraFangs", // 九首獠牙，独立源音色。
            "Boss_AbsoluteZero", // 绝对零域，独立源音色。
            "Boss_Avalanche", // 万仞雪崩，独立源音色。
            "Boss_Heavenfall", // 天穹裁决，独立源音色。
            "Boss_FinalEdict", // 终极律令，独立源音色。
        };

        /// <summary>固定每种技能一个句柄与节流时间；重复段替换自己上一段，不无限叠音，也不停止其它音效。</summary>
        private readonly RevSoundHandle[] _bossAudioHandles = new RevSoundHandle[BossClipNames.Length];
        private readonly float[] _bossAudioReadyAt = new float[BossClipNames.Length];
        private readonly string[] _bossAudioAliases = new string[BossClipNames.Length];
        public int BossAudioRequestCount
        {
            get; private set;
        }
        public string LastBossAudioResource
        {
            get; private set;
        }
        /// <summary>返回实际资源名供Editor逐项核对；无效枚举给出空值，不能默默映射成任意技能。</summary>
        public static string GetBossSkillSoundName(BossSkillKind kind)
        {
            int index = (int)kind;
            return index >= 0 && index < BossClipNames.Length ? BossClipNames[index] : null;
        }
        /// <summary>场景独占这些别名，销毁时成对注销。Frame去重仍由RevSound处理，业务层不创建AudioSource。</summary>
        /// <summary>真实逻辑子路径；常用狂暴单独共包，六章技能各十段录音分别独立打包。</summary>
        public static string GetBossSkillSoundPath(BossSkillKind kind)
        {
            string name = GetBossSkillSoundName(kind);

            if (name == null)
            {
                return null;
            }
            return kind == BossSkillKind.Enrage ? "Boss/Common/" + name
                : "Boss/Chapter" + BossSkillCatalog.ThemeChapter(kind).ToString("00") + "/" + name;
        }
        /// <summary>本场景登记的当前章节；换章先停声音、归还加载引用再注销别名，不加载其它章。</summary>
        private int _bossAudioChapter;
        public int BossAudioChapter => _bossAudioChapter;
        public int RegisteredBossAudioCount
        {
            get; private set;
        }
        private void RefreshBossAudio()
        {
            int chapter = _battleScreen ? RevServices.GetRequired<IBattleService>().Chapter : 0;

            if (chapter == _bossAudioChapter)
            {
                return;
            }
            CloseBossAudio();
            _bossAudioChapter = chapter;

            if (chapter <= 0)
            {
                return;
            }
            for (int index = 0; index < BossClipNames.Length; index++)
            {
                var kind = (BossSkillKind)index;

                if (!BossSkillCatalog.IsUnlocked(kind, chapter))
                {
                    continue;
                }
                string alias = "Boss_" + kind;
                _bossAudioAliases[index] = alias;

                if (!RevSound.Register(alias, GetBossSkillSoundPath(kind)))
                {
                    throw new System.InvalidOperationException("Boss音效登记失败：" + alias);
                }
                RegisteredBossAudioCount++;
            }
            // 按需首次播放再加载本章Clip；不调用PreloadAll，目录登记不占其它章音频引用。
        }
        private void OpenBossAudio()
        {
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
            RefreshBossAudio();

            if (_bossAudioAliases[index] == null)
            {
                RevLog.Warn("当前章未登记Boss技能：" + info.Kind, "RevGame");
                return;
            }
            if (Time.unscaledTime < _bossAudioReadyAt[index])
            {
                return;
            }
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
            for (int index = 0; index < _bossAudioAliases.Length; index++)
            {
                string alias = _bossAudioAliases[index];

                if (string.IsNullOrEmpty(alias))
                {
                    continue;
                }
                // Unload必须在Unregister之前：框架按别名解析真实子路径，先注销会找不到原加载引用。
                RevSound.Unload(alias);
                RevSound.Unregister(alias);
                _bossAudioAliases[index] = null;
            }
            RegisteredBossAudioCount = 0;
            _bossAudioChapter = 0;
        }
    }
}
