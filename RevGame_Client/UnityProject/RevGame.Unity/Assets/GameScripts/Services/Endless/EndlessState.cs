using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    /// <summary>无尽局内阶段。战斗时钟只在Fighting推进；改阵仅改变同一时钟倍率。</summary>
    public enum EndlessPhase
    {
        Formation, Fighting, Upgrade, Preparation, Event, Circuit, Paused, Result
    }
    /// <summary>单个盘内演员的附属状态，归本局所有；离场从有限名单移除，不保留历史对象。</summary>
    public sealed class EndlessUnit
    {
        public Piece Piece;
        public bool Summoned, Stone, Revived, Elite;
        public float AttackAt, MoveAt, BurnUntil, BurnAt, BurnDamage, SlowUntil, CounterAt;
        public float BrokenUntil, NextSkillAt, WarningAt, BornAt;
        public int BossTemplate = -1, WarningLine = -1;
        /// <summary>本次领主借用的六章身份，1~6；出生时确定，巡回推进不会改变在场Boss。</summary>
        public int BossChapter;
        /// <summary>锁定的章节技能、三处目标和当前伤害段；全部写入存档，读档后不重新抽目标。</summary>
        public int ChapterSkill, SkillStage;
        /// <summary>这只领主累计放了几次技能；由它推导“这次是不是专属技”，读档后结果不变。</summary>
        public int SkillCasts;
        public BoardPos SkillA, SkillB, SkillC;
        /// <summary>入侵职责与来袭门点；屏障朝向按门点判断，身份转换后保持不变。</summary>
        public int Role, Gate;
        /// <summary>破眼蓄力结束时间/目标身份；目标移走或死亡时取消，不能隔空破眼。</summary>
        public float BreakEyeAt;
        public int BreakEyeTarget;
        /// <summary>稀有祝福的局内寿命：破眼攻速结束、静守起点和一次抵挡，不使用全局静态计时。</summary>
        public float FuryUntil, StationarySince;
        public bool SteadyShield;
    }
    /// <summary>外围敌人是值类型，不挂MonoBehaviour，不参与围棋连通块。Id在转入棋盘后保持。</summary>
    [Serializable]
    public struct EndlessHorde
    {
        public bool active;
        public int id, gate, kind, chainGeneration;
        /// <summary>献祭潮杂兵：被杀时给领主掉血。</summary>
        public bool surge;
        /// <summary>瘟沼毒囊杂兵：被杀时爆炸波及周围。</summary>
        public bool pustule;
        public float x, y, hp, maxHp, speed, arrivedAt, burnUntil, burnAt, burnDamage, frozenUntil, slowUntil;
    }
    /// <summary>表现只读伤害结果；环形缓冲覆盖旧画面不会删除任何业务伤害。</summary>
    public struct EndlessImpact
    {
        public int serial, route;
        public Vector2 from, to;
        public float time, radius;
    }
    /// <summary>可序列化完整局状态。所有随机抽选、导演期限和奖励状态都属于它，读档不得重新抽选。</summary>
    [Serializable]
    public sealed class EndlessRun
    {
        public int version = 1, seed, difficulty, formation, circuit = 1, level = 1, xp, cores, fragments;
        public int nextId = 1, kills, invaderKills, eliteKills, bossKills, completedCircuits, maximumChain;
        public uint random;
        public long score, bankedScore;
        public string runId, lastDamage = "尚未受伤", error = "";
        public EndlessPhase phase, previousPhase;
        public float seconds, heart, formationLeft, nextSpawn, nextInvader, nextElite, nextBoss, nextMinute, nextEvent, nextSave;
        public float slowLeft, riskUntil, nextSummon, nextPulse, nextBomb, rescueUntil;
        public int slowMoves, slowMinute = -1, rerolls, banishes, pendingLevels, pityMisses, selectedCard = -1, selectedUnit = -1;
        public int supplyMinute = -1, supplyHealing, disassemblyPoints;
        public bool rescueUsed, settled, scoreStopped, lowEffects, shake = true, flashes = true;
        /// <summary>事件索引与风险任务起点，均使用战斗时间；遗迹坐标固定在盘边且每局最多一个。</summary>
        public int eventKind, riskKind = -1, riskKillsAt, riskDamageAt;
        public float riskEnds, heartShieldUntil, heartShieldReady, doubleShotUntil, adaptiveBudget = 1, minuteHeart;
        public int heartDamageEvents, replacementMinute = -1, upgradeStreak;
        public float nextChoiceAt;
        public float healRemainder;
        public float relicHp;
        public int relicGate = -1;
        public bool riskObjectiveDone;
        /// <summary>同格只保留最新燃烧区，最多81格；独立时钟保证读档不多触发一次持续伤害。</summary>
        public float[] burnZoneUntil = new float[81], burnZoneDamage = new float[81];
        public float nextZoneTick;
        // 六种路线是策划的枚举结构，数组长度不是可调数值；槽位上限仍取Excel。
        public int[] mains = new int[6], seals = new int[6];
        public bool[] evolved = new bool[6], banned = new bool[16], rare = new bool[4];
        public float[] brokenEyeReady = new float[81];
        public double[] damage = new double[6];
        public int[] choices = new int[3];
        public float[] gateWarnings = new float[4], gateNext = new float[4];
        public List<int> hand = new List<int>();
        public List<int> breakthroughs = new List<int>(), affixes = new List<int>();
        public List<EndlessSavedUnit> units = new List<EndlessSavedUnit>();
        public EndlessHorde[] horde;
    }
    /// <summary>存档只写业务数据，不序列化Unity对象或Piece内部缓存。恢复时由Board.Place重建索引。</summary>
    [Serializable]
    public sealed class EndlessSavedUnit
    {
        public int id, kind, star, side, x, y, size, hp, maxHp, bossTemplate, warningLine, role, gate, breakEyeTarget;
        public int skillCasts, bossChapter, chapterSkill, skillStage, skillAX, skillAY, skillBX, skillBY, skillCX, skillCY;
        public bool summoned, stone, revived, elite, steadyShield;
        public float attackScale, attackAt, moveAt, burnUntil, burnAt, burnDamage, slowUntil, counterAt;
        public float brokenUntil, nextSkillAt, warningAt, bornAt, breakEyeAt, furyUntil, stationarySince;
    }
}
