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
    /// <summary>
    /// 组合技的效果类型，Excel EndlessCombo.Effect 写这些名字：
    /// Pierce 贯穿一条线并在末端爆炸；Spread 扇形多发；Rain 从天而降的箭雨；Burst 目标处范围爆裂；
    /// Pillar 目标处光柱；Chain 依次瞬斩附近敌人；Ring 以自身为中心的冲击环。
    /// 枚举只决定“怎么打”，伤害倍率、半径、数量全在表里。
    /// </summary>
    public enum EndlessComboEffect
    {
        Pierce, Spread, Rain, Burst, Pillar, Chain, Ring
    }
    /// <summary>
    /// 领主奖励（滚雪球）的效果类型，Excel EndlessBoon.Effect 写这些名字：
    /// Attack 全体伤害、AttackSpeed 全体攻速、Health 生命上限（并回满）、AuraRange 剑气/盾击范围、ComboPower 组合技伤害、Heart 棋心回复。
    /// 枚举只决定“加到哪”，加多少全在表里。
    /// </summary>
    public enum EndlessBoonEffect
    {
        Attack, AttackSpeed, Health, AuraRange, ComboPower, Heart
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
        /// <summary>近战剑气下次切割的战斗时间（秒）；只有近战棋使用。存档保存，读档后不会立刻多切一次。</summary>
        public float AuraAt;
        /// <summary>组合技计数：本棋子满足搭档条件后已打出的普攻次数，到 Every 次就放一次组合技。存档保存。</summary>
        public int ComboCount;
        /// <summary>玩家手动摆放/移动后，自动索敌暂停到这个战斗时间（秒）。存档保存。</summary>
        public float HoldUntil;
        /// <summary>智能索敌这一轮分配到的驻守区（0~5，-1 未分配）。每次规划重算，不进存档。</summary>
        public int Zone = -1;
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
        /// <summary>涌入后贴住己方棋子时下次出手的战斗时间（秒）。旧存档为 0，表示可以立刻出手。</summary>
        public float attackAt;
    }
    /// <summary>表现只读伤害结果；环形缓冲覆盖旧画面不会删除任何业务伤害。</summary>
    public struct EndlessImpact
    {
        public int serial, route;
        public Vector2 from, to;
        public float time, radius;
        /// <summary>
        /// 出手棋子的编号与棋种，0 表示环境/连锁这类没有具体出手者的效果。
        /// 表现层据此挑六章同款弹道或斩击、播出手动作和攻击口号；只是快照，棋子死后仍能正确播放。
        /// </summary>
        public int sourceId;
        public PieceKind sourceKind;
        /// <summary>sourceKind 是否有效。组合技的弹道/范围没有具体出手者（sourceId=0），但要指定六章棋种外观。</summary>
        public bool hasKind;
        /// <summary>组合技喊招回执：组合技编号 + 1（0 表示普通回执）。表现层据此弹出组合技名字飘字并播组合技音效。</summary>
        public int combo;
    }
    /// <summary>
    /// 一次真实扣血的表现回执，用来显示六章同款伤害数字。
    /// target 是被打者编号（外围杂兵和盘内棋子共用 Run.nextId 发号，不会重复），同目标同帧的数字由表现层合并。
    /// 写入时伤害已经结算完毕；环形缓冲被覆盖只会少显示一个数字，不影响任何血量。
    /// </summary>
    public struct EndlessDamageReceipt
    {
        public int serial, target, damage;
        public Vector2 position;
        public GameSide side;
        public BattleDamageKind kind;
    }
    /// <summary>
    /// 一次真实死亡的表现回执。盘内棋子靠它区分“死亡”（播溶解）和“三合一被吃掉”（直接收走）；
    /// 外围杂兵没有 PieceView，表现层按 position 临时借一枚棋子视图播放同款溶解。
    /// </summary>
    /// <summary>一次盾战防御区格挡的表现回执：哪枚己方棋子的护盾挡了伤害，表现层让它的护盾泡泡波动。</summary>
    public struct EndlessBlockReceipt
    {
        public int serial, id;
        public Vector2 position;
    }
    public struct EndlessDeathReceipt
    {
        public int serial, id;
        public Vector2 position;
        public bool horde;
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
        /// <summary>
        /// 出生槽游标：每刷一只外围敌人加 1，由它推出“左/右哪一侧、第几行”。
        /// 存进档里，读档后接着轮转，不会重新从第 0 行开始把敌人挤在一起。
        /// </summary>
        public int laneCursor;
        /// <summary>下次自动补手牌的战斗时间（秒）。无尽全程不停顿，手牌由它定时补到上限，存档保存。</summary>
        public float nextCardAt;
        /// <summary>
        /// 领主奖励累计（滚雪球）：每击杀一只领主从 EndlessBoon 表抽一条累加到这里，整局永久生效、跟存档走。
        /// boonAttack 伤害加成比例、boonSpeed 攻速加成比例、boonHealth 生命上限加成比例、
        /// boonAura 剑气/盾击范围加成比例、boonCombo 组合技伤害加成比例；boonCount 已领取次数。
        /// </summary>
        public float boonAttack, boonSpeed, boonHealth, boonAura, boonCombo;
        public int boonCount;
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
        /// <summary>剑气与组合技状态（新增字段，旧存档读出来是 0，等于“马上可以切/从头计数”，不会出错）。</summary>
        public float auraAt, holdUntil;
        public int comboCount;
    }
}
