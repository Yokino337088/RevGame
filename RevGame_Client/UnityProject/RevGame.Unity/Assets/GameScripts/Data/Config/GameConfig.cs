// ============================================================
// GameConfig.cs —— 全部导表数据的“装配 + 校验”入口，也是游戏数值的唯一来源。
//
// 为什么要有这一层：
// Excel 里的行是“字符串 + 数字”，代码里要的是枚举、不可变定义和已校验的参数。
// 如果让各处自己 Enum.Parse / FindByKey，填错一个枚举名会在战斗中途才炸，而且每处报错口径不同。
// 这里在游戏启动时一次性把所有表转成不可变快照，任何问题（缺行、枚举名拼错、数值越界、占位符写错）
// 都攒成一份“中文问题清单”一起抛出，策划改一轮就能全部修完，而不是玩到哪儿炸到哪儿。
//
// 约定（别绕开）：
// 1) 数值只来自 Excel（配置表/*.xlsx）→ 框架导表工具 → GameRes/Data/*.txt + Generated/*.cs。
//    不要在 C# 里再写一份同样的数值；要新增数值，先加 Excel 列/行，再在这里读取并校验。
// 2) 运行时由 GameFlow 用 RevDataTableManager.LoadAsync 加载各表，然后调用 Initialize 一次；
//    退出游戏时调用 Reset。未装配就读取会立刻抛异常（失败必须可见），不会悄悄返回 0。
// 3) 本类只依赖框架的纯 C# 表基类，不碰 UnityEngine，因此能在工程外做断言测试。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Revolution;
using RevGame.Core;

namespace RevGame.Data
{
    /// <summary>已经加载好的全部配置表（由加载方准备，GameConfig.Initialize 消费）。</summary>
    public sealed class GameConfigTables
    {
        public PieceConfigTable Pieces;
        public StarConfigTable Stars;
        public PrepBuffConfigTable PrepBuffs;
        public BattleEventConfigTable BattleEvents;
        public EventRotationConfigTable EventRotation;
        public ChapterConfigTable Chapters;
        public EnemyRosterConfigTable EnemyRoster;
        public EnemyReinforceConfigTable EnemyReinforce;
        public RewardConfigTable Rewards;
        public RewardBoostConfigTable RewardBoosts;
        /// <summary>Boss 技能表（BossSkillConfig）。</summary>
        public BossSkillConfigTable BossSkills;
        public BalanceRuleTable Balance;
    }

    /// <summary>配置装配失败。Message 里是完整的中文问题清单（每条带表名和行/编号）。</summary>
    public sealed class GameConfigException : Exception
    {
        public IReadOnlyList<string> Problems { get; }

        public GameConfigException(IReadOnlyList<string> problems) : base(BuildMessage(problems))
        {
            Problems = problems;
        }

        private static string BuildMessage(IReadOnlyList<string> problems)
        {
            var sb = new StringBuilder("配置表校验未通过，共 ").Append(problems.Count).Append(" 个问题：");
            for (int i = 0; i < problems.Count; i++) { sb.Append("\n  ").Append(i + 1).Append(". ").Append(problems[i]); }
            return sb.ToString();
        }
    }

    /// <summary>不可变的一份完整配置。所有目录类（PieceCatalog 等）都读它。</summary>
    internal sealed class ConfigSnapshot
    {
        public readonly Dictionary<PieceKind, PieceTemplate> Pieces = new Dictionary<PieceKind, PieceTemplate>();
        public readonly List<PieceKind> PieceOrder = new List<PieceKind>();
        public readonly Dictionary<int, StarRule> Stars = new Dictionary<int, StarRule>();
        public readonly List<PreparationBuffDef> Buffs = new List<PreparationBuffDef>();
        public readonly Dictionary<BattleEventKind, BattleEventDef> Events = new Dictionary<BattleEventKind, BattleEventDef>();
        public readonly List<BattleEventKind> EventRotation = new List<BattleEventKind>();
        public readonly List<BattleEventKind> CommonEvents = new List<BattleEventKind>();
        public readonly List<BattleEventKind> RareEvents = new List<BattleEventKind>();
        public readonly List<ChapterRule> Chapters = new List<ChapterRule>();
        public readonly List<PieceKind> CoreRoster = new List<PieceKind>();
        public readonly List<PieceKind> FillerRoster = new List<PieceKind>();
        public readonly List<ReinforceRule> Reinforce = new List<ReinforceRule>();
        public readonly Dictionary<RewardKind, RewardRule> Rewards = new Dictionary<RewardKind, RewardRule>();
        public readonly Dictionary<BoostKind, BoostRule> Boosts = new Dictionary<BoostKind, BoostRule>();
        /// <summary>Boss 技能定义，每个 BossSkillKind 恰好一项。</summary>
        public readonly Dictionary<BossSkillKind, BossSkillDef> BossSkills = new Dictionary<BossSkillKind, BossSkillDef>();
        public BalanceValues Balance;
    }

    /// <summary>棋子外形的 1★ 模板（来自 PieceConfig）。</summary>
    internal sealed class PieceTemplate
    {
        public PieceKind Kind;
        public string DisplayName;
        public PieceClass Class;
        public string SpriteKey;
        public int Hp;
        public int Attack;
        public MovePattern Pattern;
        public int MoveRange;
        public bool IsRanged;
        public bool UnlockedAtStart;
    }

    /// <summary>星级成长规则（来自 StarConfig）。</summary>
    internal sealed class StarRule
    {
        public int Star;
        public float HpScale;
        public float AttackScale;
        public int PlayerBodySize;
    }

    /// <summary>战斗事件的效果参数（来自 BattleEventConfig）。</summary>
    public sealed class BattleEventDef
    {
        public BattleEventKind Kind { get; internal set; }
        public string Name { get; internal set; }
        public bool IsRare { get; internal set; }
        public float Amount { get; internal set; }
        public float Cap { get; internal set; }
    }

    /// <summary>一章的敌方与 Boss 配置（来自 ChapterConfig）。</summary>
    internal sealed class ChapterRule
    {
        public int Chapter;
        public int EnemyStar;
        public PieceKind BossKind;
        public int BossStar;
        public int[] BossBodySizes;
    }

    /// <summary>战斗中敌方补兵规则（来自 EnemyReinforceConfig）。</summary>
    internal sealed class ReinforceRule
    {
        public PieceKind Kind;
        public int Every;
        public int Offset;
        public int MinIntelligence;
        public int Priority;
    }

    /// <summary>奖励抽取规则（来自 RewardConfig）。</summary>
    internal sealed class RewardRule
    {
        public RewardKind Kind;
        public string Title;
        public string Detail;
        public int Weight;
        public int OutnumberedBonus;
        public int UnlockAfterCampaigns;
    }

    /// <summary>全队强化规则（来自 RewardBoostConfig）。</summary>
    internal sealed class BoostRule
    {
        public BoostKind Kind;
        public string Detail;
        public float Amount;
    }

    public static class GameConfig
    {
        private static ConfigSnapshot _current;

        /// <summary>配置是否已经装配完成。</summary>
        public static bool IsLoaded => _current != null;

        internal static ConfigSnapshot Current
        {
            get
            {
                ConfigSnapshot snapshot = _current;
                if (snapshot == null)
                {
                    throw new InvalidOperationException("配置表尚未装配：请先加载全部导表数据并调用 GameConfig.Initialize（正式游戏由 GameFlow 在启动加载阶段完成）");
                }
                return snapshot;
            }
        }

        /// <summary>清除当前配置（退出游戏或测试结束时调用）。之后再读取会立刻报错。</summary>
        public static void Reset()
        {
            _current = null;
        }

        /// <summary>
        /// 校验并装配全部配置。任何一张表有问题都会汇总成一份清单一起抛出；
        /// 失败时旧配置保持不变（不会出现“装了一半”的状态）。
        /// </summary>
        public static void Initialize(GameConfigTables tables)
        {
            var problems = new List<string>();
            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables));
            }
            RequireLoaded(problems, "PieceConfig", tables.Pieces);
            RequireLoaded(problems, "StarConfig", tables.Stars);
            RequireLoaded(problems, "PrepBuffConfig", tables.PrepBuffs);
            RequireLoaded(problems, "BattleEventConfig", tables.BattleEvents);
            RequireLoaded(problems, "EventRotationConfig", tables.EventRotation);
            RequireLoaded(problems, "ChapterConfig", tables.Chapters);
            RequireLoaded(problems, "EnemyRosterConfig", tables.EnemyRoster);
            RequireLoaded(problems, "EnemyReinforceConfig", tables.EnemyReinforce);
            RequireLoaded(problems, "RewardConfig", tables.Rewards);
            RequireLoaded(problems, "RewardBoostConfig", tables.RewardBoosts);
            RequireLoaded(problems, "BossSkillConfig", tables.BossSkills);
            RequireLoaded(problems, "BalanceRule", tables.Balance);
            if (problems.Count > 0)
            {
                throw new GameConfigException(problems);
            }

            var snapshot = new ConfigSnapshot();
            // 先读参数表：增益文案里的 {Interval} 要引用它。
            snapshot.Balance = BalanceValues.Read(tables.Balance, problems);
            BuildPieces(tables, snapshot, problems);
            BuildStars(tables, snapshot, problems);
            BuildEvents(tables, snapshot, problems);
            BuildBuffs(tables, snapshot, problems);
            BuildChapters(tables, snapshot, problems);
            BuildEnemyRoster(tables, snapshot, problems);
            BuildReinforce(tables, snapshot, problems);
            BuildRewards(tables, snapshot, problems);
            BossSkillCatalog.Build(tables, snapshot, problems);
            if (problems.Count > 0)
            {
                throw new GameConfigException(problems);
            }
            _current = snapshot;
        }

        // ------------------------------------------------------------ 通用工具
        private static void RequireLoaded(List<string> problems, string name, RevIDataTable table)
        {
            if (table == null || !table.IsLoaded)
            {
                problems.Add("表 " + name + " 尚未加载（先 RevDataTableManager.LoadAsync，或检查 GameRes/Data/" + name + ".txt 与 ResMap 映射）");
            }
            else if (table.Count == 0)
            {
                problems.Add("表 " + name + " 没有任何数据行");
            }
        }

        internal static bool TryEnum<T>(string text, string where, string field, List<string> problems, out T value) where T : struct
        {
            string trimmed = (text ?? string.Empty).Trim();
            // 只接受枚举名本身：Enum.TryParse 会把数字字符串（如 "99"）当成合法值，必须拦掉。
            if (trimmed.Length == 0 || char.IsDigit(trimmed[0]) || trimmed[0] == '-' || !Enum.TryParse(trimmed, false, out value) || !Enum.IsDefined(typeof(T), value))
            {
                problems.Add(where + " 的 " + field + " = \"" + trimmed + "\" 不是有效的 " + typeof(T).Name + "（可选：" + string.Join(" / ", Enum.GetNames(typeof(T))) + "）");
                value = default;
                return false;
            }
            return true;
        }

        internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>数值必须是有限数且落在区间内；NaN 与任何数比较都为 false，必须显式拦截。</summary>
        internal static bool CheckRange(float v, float min, float max, string where, string field, List<string> problems)
        {
            if (!Finite(v) || v < min || v > max)
            {
                problems.Add(where + " 的 " + field + " = " + v.ToString("R", CultureInfo.InvariantCulture) + " 超出合法范围 [" + Fmt(min) + ", " + Fmt(max) + "]");
                return false;
            }
            return true;
        }

        internal static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>按 Enum 全集检查：表里必须恰好覆盖每个枚举值一次（缺行、多行都报）。</summary>
        internal static void CheckCoverage<T>(IEnumerable<string> keys, string tableName, List<string> problems) where T : struct
        {
            var seen = new HashSet<string>();
            foreach (string key in keys)
            {
                if (!seen.Add(key))
                {
                    problems.Add("表 " + tableName + " 的编号 " + key + " 重复");
                }
            }
            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (!seen.Contains(name))
                {
                    problems.Add("表 " + tableName + " 缺少编号 " + name + "（代码枚举 " + typeof(T).Name + " 要求每一项都有一行）");
                }
            }
            foreach (string key in seen)
            {
                if (Array.IndexOf(Enum.GetNames(typeof(T)), key) < 0)
                {
                    problems.Add("表 " + tableName + " 的编号 " + key + " 不是代码枚举 " + typeof(T).Name + " 的成员（程序不认识这一行，请删除或先让程序新增枚举）");
                }
            }
        }

        // ------------------------------------------------------------ 棋子
        private static void BuildPieces(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            var keys = new List<string>();
            int unlocked = 0;
            foreach (PieceConfig row in t.Pieces.All)
            {
                keys.Add(row.Kind);
                string where = "PieceConfig[" + row.Kind + "]";
                var tpl = new PieceTemplate();
                bool ok = TryEnum(row.Kind, where, "Kind", problems, out tpl.Kind);
                ok &= TryEnum(row.Class, where, "Class", problems, out tpl.Class);
                ok &= TryEnum(row.MovePattern, where, "MovePattern", problems, out tpl.Pattern);
                if (string.IsNullOrWhiteSpace(row.DisplayName))
                {
                    problems.Add(where + " 的 DisplayName 不能为空"); ok = false;
                }
                if (string.IsNullOrWhiteSpace(row.SpriteKey))
                {
                    problems.Add(where + " 的 SpriteKey 不能为空"); ok = false;
                }
                if (row.Hp <= 0)
                {
                    problems.Add(where + " 的 Hp 必须大于 0（当前 " + row.Hp + "）"); ok = false;
                }
                if (row.Attack <= 0)
                {
                    problems.Add(where + " 的 Attack 必须大于 0（当前 " + row.Attack + "）"); ok = false;
                }
                if (row.MoveRange < 0)
                {
                    problems.Add(where + " 的 MoveRange 不能为负数（当前 " + row.MoveRange + "）"); ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                tpl.DisplayName = row.DisplayName;
                tpl.SpriteKey = row.SpriteKey;
                tpl.Hp = row.Hp;
                tpl.Attack = row.Attack;
                tpl.MoveRange = row.MoveRange;
                tpl.IsRanged = row.IsRanged;
                tpl.UnlockedAtStart = row.UnlockedAtStart;
                if (row.UnlockedAtStart)
                {
                    unlocked++;
                }
                s.Pieces[tpl.Kind] = tpl;
                s.PieceOrder.Add(tpl.Kind);
            }
            CheckCoverage<PieceKind>(keys, "PieceConfig", problems);
            if (unlocked == 0)
            {
                problems.Add("PieceConfig 至少要有一个 UnlockedAtStart=TRUE 的棋子，否则开局没有牌可抽");
            }
        }

        private static void BuildStars(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            foreach (StarConfig row in t.Stars.All)
            {
                string where = "StarConfig[" + row.Star + "]";
                if (row.Star < 1 || row.Star > 3)
                {
                    problems.Add(where + " 星级必须是 1~3"); continue;
                }
                bool ok = CheckRange(row.HpScale, 0.01f, 1000f, where, "HpScale", problems);
                ok &= CheckRange(row.AttackScale, 0.01f, 1000f, where, "AttackScale", problems);
                if (row.PlayerBodySize < 1 || row.PlayerBodySize > 3)
                {
                    problems.Add(where + " 的 PlayerBodySize 必须是 1~3（当前 " + row.PlayerBodySize + "）"); ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                s.Stars[row.Star] = new StarRule { Star = row.Star, HpScale = row.HpScale, AttackScale = row.AttackScale, PlayerBodySize = row.PlayerBodySize };
            }
            for (int star = 1; star <= 3; star++)
            {
                // 行存在但数值非法时上面已经报过具体原因，这里只报“根本没有这一行”。
                if (!t.Stars.ContainsKey(star))
                {
                    problems.Add("StarConfig 缺少 " + star + " 星这一行（1~3 星必须齐全）");
                }
            }
        }

        // ------------------------------------------------------------ 战斗事件
        private static void BuildEvents(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            var keys = new List<string>();
            foreach (BattleEventConfig row in t.BattleEvents.All)
            {
                keys.Add(row.Kind);
                string where = "BattleEventConfig[" + row.Kind + "]";
                if (!TryEnum(row.Kind, where, "Kind", problems, out BattleEventKind kind))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(row.Name))
                {
                    problems.Add(where + " 的 Name 不能为空"); continue;
                }
                bool ok = CheckRange(row.Amount, 0f, 1000f, where, "Amount", problems);
                ok &= CheckRange(row.Cap, 0f, 1000f, where, "Cap", problems);
                // 各事件必须填的主数值：缺了就是 0，会让事件“触发了却没有效果”。
                switch (kind)
                {
                    case BattleEventKind.Liberty:
                    case BattleEventKind.Teleport:
                    case BattleEventKind.Heal:
                    case BattleEventKind.Artillery:
                    case BattleEventKind.BreakChain:
                        if (row.Amount <= 0f)
                        {
                            problems.Add(where + " 的 Amount 必须大于 0（该事件的主效果量）"); ok = false;
                        }
                        break;
                    case BattleEventKind.Attack:
                    case BattleEventKind.Defense:
                    case BattleEventKind.Haste:
                    case BattleEventKind.Vitality:
                    case BattleEventKind.Combo:
                        if (row.Amount <= 0f || row.Cap <= 0f)
                        {
                            problems.Add(where + " 的 Amount 与 Cap 都必须大于 0（攻速加成与累计上限）"); ok = false;
                        }
                        break;
                }
                if (!ok)
                {
                    continue;
                }
                var def = new BattleEventDef { Kind = kind, Name = row.Name.Trim(), IsRare = row.IsRare, Amount = row.Amount, Cap = row.Cap };
                s.Events[kind] = def;
                (def.IsRare ? s.RareEvents : s.CommonEvents).Add(kind);
            }
            CheckCoverage<BattleEventKind>(keys, "BattleEventConfig", problems);
            if (s.CommonEvents.Count == 0 || s.RareEvents.Count == 0)
            {
                problems.Add("BattleEventConfig 的普通组与稀有组（IsRare）都不能为空");
            }
            var names = new HashSet<string>();
            foreach (BattleEventDef def in s.Events.Values)
            {
                if (!names.Add(def.Name))
                {
                    problems.Add("BattleEventConfig 的事件名 " + def.Name + " 重复（它是弹幕表 Event_<名称> 的后缀，必须唯一）");
                }
            }
            // 轮换表：Step 必须从 1 连续，且每个 Kind 都存在。
            int step = 1;
            foreach (EventRotationConfig row in t.EventRotation.All)
            {
                string where = "EventRotationConfig[" + row.Step + "]";
                if (row.Step != step)
                {
                    problems.Add(where + " 的 Step 应为 " + step + "（必须从 1 开始连续）");
                }
                step++;
                if (TryEnum(row.Kind, where, "Kind", problems, out BattleEventKind kind))
                {
                    s.EventRotation.Add(kind);
                }
            }
        }

        // ------------------------------------------------------------ 准备增益
        private static void BuildBuffs(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            var keys = new List<string>();
            foreach (PrepBuffConfig row in t.PrepBuffs.All)
            {
                keys.Add(row.Kind);
                string where = "PrepBuffConfig[" + row.Kind + "]";
                if (!TryEnum(row.Kind, where, "Kind", problems, out PreparationBuffKind kind))
                {
                    continue;
                }
                bool ok = true;
                if (string.IsNullOrWhiteSpace(row.Title))
                {
                    problems.Add(where + " 的 Title 不能为空"); ok = false;
                }
                if (string.IsNullOrWhiteSpace(row.Detail))
                {
                    problems.Add(where + " 的 Detail 不能为空"); ok = false;
                }
                ok &= CheckRange(row.Amount, 0f, 1000f, where, "Amount", problems);
                ok &= CheckRange(row.Extra, 0f, 1000f, where, "Extra", problems);
                ok &= CheckRange(row.Threshold, 0f, 1f, where, "Threshold", problems);
                ok &= CheckBuffRequirements(kind, row, where, problems);
                PieceKind piece = default;
                string pieceName = string.Empty;
                if (kind == PreparationBuffKind.Elite)
                {
                    if (TryEnum(row.Piece, where, "Piece", problems, out piece))
                    {
                        if (!s.Pieces.TryGetValue(piece, out PieceTemplate tpl))
                        {
                            ok = false;
                        }
                        else
                        {
                            pieceName = tpl.DisplayName;
                        }
                    }
                    else
                    {
                        ok = false;
                    }
                    if (row.Star < 1 || row.Star > 3)
                    {
                        problems.Add(where + " 的 Star 必须是 1~3（当前 " + row.Star + "）"); ok = false;
                    }
                }
                if (!ok)
                {
                    continue;
                }
                string detail = ConfigText.Render(row.Detail, where + ".Detail", problems, token => ResolveBuffToken(token, row, pieceName, s.Balance));
                s.Buffs.Add(new PreparationBuffDef(kind, row.Title.Trim(), detail, row.Amount, row.Extra, row.Threshold, piece, row.Star));
            }
            CheckCoverage<PreparationBuffKind>(keys, "PrepBuffConfig", problems);
            // 洗牌顺序必须与枚举声明顺序一致无关，但抽取数量不能超过增益总数。
            if (s.Balance != null && s.Balance.PrepBuffCountMax > s.Buffs.Count)
            {
                problems.Add("BalanceRule 的 Prep.BuffCountMax = " + s.Balance.PrepBuffCountMax + " 超过增益总数 " + s.Buffs.Count);
            }
        }

        /// <summary>每种增益“必须有哪些数值”。缺了不是报错而是静默变成 0，所以在装配时就拦下。</summary>
        private static bool CheckBuffRequirements(PreparationBuffKind kind, PrepBuffConfig row, string where, List<string> problems)
        {
            bool ok = true;
            void NeedAmount() {
            if (row.Amount <= 0f)
            {
                problems.Add(where + " 的 Amount 必须大于 0（该增益的主效果量）"); ok = false;
            }
            }
            switch (kind)
            {
                case PreparationBuffKind.Elite:
                case PreparationBuffKind.Attack:
                case PreparationBuffKind.Haste:
                case PreparationBuffKind.Vitality:
                case PreparationBuffKind.LifeSteal:
                case PreparationBuffKind.Regeneration:
                case PreparationBuffKind.Shields:
                case PreparationBuffKind.Ranged:
                case PreparationBuffKind.Liberties:
                case PreparationBuffKind.ArrowVolley:
                    NeedAmount();
                    break;
                case PreparationBuffKind.Armor:
                    NeedAmount();
                    if (row.Amount >= 1f)
                    {
                        problems.Add(where + " 的 Amount 是减伤比例，必须小于 1（否则伤害变为 0 或负数）"); ok = false;
                    }
                    break;
                case PreparationBuffKind.Recovery:
                    NeedAmount();
                    if (row.Extra <= 0f)
                    {
                        problems.Add(where + " 的 Extra 必须大于 0（首次受伤后的回复比例）"); ok = false;
                    }
                    break;
                case PreparationBuffKind.Execution:
                    NeedAmount();
                    if (row.Threshold <= 0f)
                    {
                        problems.Add(where + " 的 Threshold 必须大于 0（生命百分比线）"); ok = false;
                    }
                    break;
                case PreparationBuffKind.EyePower:
                    NeedAmount();
                    if (row.Extra <= 0f)
                    {
                        problems.Add(where + " 的 Extra 必须大于 0（成眼棋周期回复比例）"); ok = false;
                    }
                    break;
            }
            return ok;
        }

        private static string ResolveBuffToken(string token, PrepBuffConfig row, string pieceName, BalanceValues balance)
        {
            switch (token)
            {
                case "Amount": return ConfigText.Number(row.Amount);
                case "Amount%": return ConfigText.Percent(row.Amount);
                case "Extra": return ConfigText.Number(row.Extra);
                case "Extra%": return ConfigText.Percent(row.Extra);
                case "Threshold": return ConfigText.Number(row.Threshold);
                case "Threshold%": return ConfigText.Percent(row.Threshold);
                case "Interval": return balance == null ? null : ConfigText.Number(balance.PrepRegenIntervalSeconds);
                case "PieceName": return pieceName.Length == 0 ? null : pieceName;
                case "StarCN": return row.Star >= 1 && row.Star <= 3 ? ConfigText.StarCn(row.Star) : null;
                default: return null;
            }
        }

        // ------------------------------------------------------------ 章节、敌方名单、补兵
        private static void BuildChapters(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            int expected = 1;
            foreach (ChapterConfig row in t.Chapters.All)
            {
                string where = "ChapterConfig[" + row.Chapter + "]";
                if (row.Chapter != expected)
                {
                    problems.Add(where + " 的章节号应为 " + expected + "（必须从 1 开始连续）");
                }
                expected++;
                bool ok = TryEnum(row.BossKind, where, "BossKind", problems, out PieceKind bossKind);
                if (row.EnemyStar < 1 || row.EnemyStar > 3)
                {
                    problems.Add(where + " 的 EnemyStar 必须是 1~3（当前 " + row.EnemyStar + "）"); ok = false;
                }
                if (row.BossStar < 1 || row.BossStar > 3)
                {
                    problems.Add(where + " 的 BossStar 必须是 1~3（当前 " + row.BossStar + "）"); ok = false;
                }
                int[] sizes = ParseSizes(row.BossBodySizes, where, problems);
                if (sizes == null)
                {
                    ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                s.Chapters.Add(new ChapterRule { Chapter = row.Chapter, EnemyStar = row.EnemyStar, BossKind = bossKind, BossStar = row.BossStar, BossBodySizes = sizes });
            }
            if (t.Chapters.Count != Consts.ChapterCount)
            {
                problems.Add("ChapterConfig 有 " + t.Chapters.Count + " 行，但程序里的章节数是 " + Consts.ChapterCount + "（选关界面按钮数量与此绑定；增减章节需同时改预制体和 Consts.ChapterCount）");
            }
        }

        private static int[] ParseSizes(string text, string where, List<string> problems)
        {
            string[] parts = (text ?? string.Empty).Split(',');
            var sizes = new List<int>();
            foreach (string part in parts)
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) || size < 1 || size > 3)
                {
                    problems.Add(where + " 的 BossBodySizes = \"" + text + "\" 不合法（逗号分隔的 1~3 整数，如 3,2,2）");
                    return null;
                }
                sizes.Add(size);
            }
            return sizes.ToArray();
        }

        private static void BuildEnemyRoster(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            int expected = 1;
            foreach (EnemyRosterConfig row in t.EnemyRoster.All)
            {
                string where = "EnemyRosterConfig[" + row.Id + "]";
                if (row.Id != expected)
                {
                    problems.Add(where + " 的 Id 应为 " + expected + "（必须从 1 开始连续）");
                }
                expected++;
                if (!TryEnum(row.Kind, where, "Kind", problems, out PieceKind kind))
                {
                    continue;
                }
                string group = (row.Group ?? string.Empty).Trim();
                if (group == "Core")
                {
                    s.CoreRoster.Add(kind);
                }
                else if (group == "Filler")
                {
                    s.FillerRoster.Add(kind);
                }
                else
                {
                    problems.Add(where + " 的 Group = \"" + group + "\" 不合法（只能是 Core 或 Filler）");
                }
            }
            if (s.CoreRoster.Count == 0)
            {
                problems.Add("EnemyRosterConfig 至少要有一行 Core");
            }
            if (s.FillerRoster.Count == 0)
            {
                problems.Add("EnemyRosterConfig 至少要有一行 Filler（人数不足时用它循环补足）");
            }
            if (s.Balance != null && s.CoreRoster.Count > s.Balance.EnemyCountBase)
            {
                problems.Add("EnemyRosterConfig 的 Core 行数 " + s.CoreRoster.Count + " 超过基础人数 Enemy.CountBase = " + s.Balance.EnemyCountBase);
            }
        }

        private static void BuildReinforce(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            foreach (EnemyReinforceConfig row in t.EnemyReinforce.All)
            {
                string where = "EnemyReinforceConfig[" + row.Id + "]";
                bool ok = TryEnum(row.Kind, where, "Kind", problems, out PieceKind kind);
                if (row.Every < 1)
                {
                    problems.Add(where + " 的 Every 必须 ≥1（当前 " + row.Every + "）"); ok = false;
                }
                if (row.Offset < 0 || row.Offset >= Math.Max(1, row.Every))
                {
                    problems.Add(where + " 的 Offset 必须满足 0 ≤ Offset < Every（当前 " + row.Offset + "）"); ok = false;
                }
                if (row.MinIntelligence < 1 || row.MinIntelligence > 4)
                {
                    problems.Add(where + " 的 MinIntelligence 必须是 1~4"); ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                s.Reinforce.Add(new ReinforceRule { Kind = kind, Every = row.Every, Offset = row.Offset, MinIntelligence = row.MinIntelligence, Priority = row.Priority });
            }
            // 档位 1 必须无论补兵序号是多少都能命中一条规则，否则会出现“某次补兵静默跳过”。
            if (s.Reinforce.Count == 0)
            {
                problems.Add("EnemyReinforceConfig 至少要有一条规则");
                return;
            }
            // 补兵次数预算是有限的（Deploy.BaseCount + 档位，远小于 60），逐个检查序号即可。
            bool uncovered = false;
            for (int intel = 1; intel <= 4 && !uncovered; intel++)
            {
                for (int count = 0; count < 60; count++)
                {
                    if (LevelCatalogInternals.PickReinforce(s, count, intel) != null)
                    {
                        continue;
                    }
                    problems.Add("EnemyReinforceConfig 在 AI 档位 " + intel + "、第 " + count + " 次补兵时没有任何规则命中（请补一条覆盖所有序号的规则）");
                    uncovered = true;
                    break;
                }
            }
        }

        // ------------------------------------------------------------ 奖励
        private static void BuildRewards(GameConfigTables t, ConfigSnapshot s, List<string> problems)
        {
            var rewardKeys = new List<string>();
            foreach (RewardConfig row in t.Rewards.All)
            {
                rewardKeys.Add(row.Kind);
                string where = "RewardConfig[" + row.Kind + "]";
                if (!TryEnum(row.Kind, where, "Kind", problems, out RewardKind kind))
                {
                    continue;
                }
                bool ok = true;
                if (string.IsNullOrWhiteSpace(row.Title))
                {
                    problems.Add(where + " 的 Title 不能为空"); ok = false;
                }
                if (row.Weight <= 0)
                {
                    problems.Add(where + " 的 Weight 必须大于 0（当前 " + row.Weight + "）"); ok = false;
                }
                if (row.OutnumberedBonus < 0)
                {
                    problems.Add(where + " 的 OutnumberedBonus 不能为负数"); ok = false;
                }
                if (row.UnlockAfterCampaigns < 0)
                {
                    problems.Add(where + " 的 UnlockAfterCampaigns 不能为负数"); ok = false;
                }
                if (kind != RewardKind.TeamBoost && string.IsNullOrWhiteSpace(row.Detail))
                {
                    problems.Add(where + " 的 Detail 不能为空"); ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                // 带 {PieceName} 的模板在每次出奖励时才有具体棋子，这里先用占位名校验语法，真正渲染在 RewardDef。
                if (!string.IsNullOrWhiteSpace(row.Detail))
                {
                    ConfigText.Render(row.Detail, where + ".Detail", problems, token => token == "PieceName" ? "测试" : null);
                }
                s.Rewards[kind] = new RewardRule { Kind = kind, Title = row.Title.Trim(), Detail = row.Detail ?? string.Empty, Weight = row.Weight, OutnumberedBonus = row.OutnumberedBonus, UnlockAfterCampaigns = row.UnlockAfterCampaigns };
            }
            CheckCoverage<RewardKind>(rewardKeys, "RewardConfig", problems);
            var boostKeys = new List<string>();
            foreach (RewardBoostConfig row in t.RewardBoosts.All)
            {
                boostKeys.Add(row.Boost);
                string where = "RewardBoostConfig[" + row.Boost + "]";
                if (!TryEnum(row.Boost, where, "Boost", problems, out BoostKind kind))
                {
                    continue;
                }
                bool ok = CheckRange(row.Amount, 0.0001f, 1000f, where, "Amount", problems);
                if (string.IsNullOrWhiteSpace(row.Detail))
                {
                    problems.Add(where + " 的 Detail 不能为空"); ok = false;
                }
                if (!ok)
                {
                    continue;
                }
                string detail = ConfigText.Render(row.Detail, where + ".Detail", problems, token => token == "Amount" ? ConfigText.Number(row.Amount) : (token == "Amount%" ? ConfigText.Percent(row.Amount) : null));
                s.Boosts[kind] = new BoostRule { Kind = kind, Detail = detail, Amount = row.Amount };
            }
            CheckCoverage<BoostKind>(boostKeys, "RewardBoostConfig", problems);
        }
    }
}
