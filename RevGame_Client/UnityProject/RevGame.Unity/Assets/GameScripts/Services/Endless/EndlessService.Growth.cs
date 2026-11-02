using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        public int ExperienceNeeded()
        {
            return N("ExperienceBase") + N("ExperienceLinear") * Run.level + N("ExperienceQuadratic") * Run.level * Run.level;
        }
        private void GainExperience(int amount)
        {
            Run.xp += amount;
            while (Run.level < N("LevelMax") && Run.xp >= ExperienceNeeded())
            {
                Run.xp -= ExperienceNeeded();
                Run.level++;
                Run.pendingLevels++;
            }
            if (Run.level == N("LevelMax") && Run.xp >= N("OverflowExperience"))
            {
                Run.xp -= N("OverflowExperience");
                int minute = (int)(Run.seconds / V("MinuteSeconds"));
                if (minute != Run.supplyMinute)
                {
                    Run.supplyMinute = minute;
                    Run.supplyHealing = 0;
                }
                int heal = Math.Min(N("SupplyHeal"), N("SupplyHealPerMinute") - Run.supplyHealing);
                if (Run.heart < V("HeartHp") && heal > 0)
                {
                    Run.heart = Mathf.Min(V("HeartHp"), Run.heart + heal);
                    Run.supplyHealing += heal;
                }
                else
                {
                    AddScore(N("SupplyScore"));
                }
            }
        }
        private readonly int[] _candidates = new int[16];
        private int SlotCount(int[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > 0)
                {
                    count++;
                }
            }
            return count;
        }
        private bool LegalChoice(int id)
        {
            if (id < 0)
            {
                return true;
            }
            if (Run.banned[id])
            {
                return false;
            }
            if (id >= 12)
            {
                return !Run.rare[id - 12] && SlotCount(Run.mains) + RareSlots() < N("MainSlots");
            }
            bool seal = id >= 6;
            int route = id % 6;
            int[] values = seal ? Run.seals : Run.mains;
            return values[route] < N(seal ? "SealMax" : "MainMax") && (values[route] > 0 || SlotCount(values) + (seal ? 0 : RareSlots()) < N(seal ? "SealSlots" : "MainSlots"));
        }
        /// <summary>
        /// 升级不再弹三选一（会打断割草）：照常抽出三个候选，直接拿第一项——抽签规则本来就让第一项优先强化已有路线，
        /// 等于替玩家做了最顺手的选择；然后推一条横幅告诉玩家拿到了什么。随机仍走存档随机源，读档结果一致。
        /// </summary>
        private void OfferUpgrade()
        {
            if (Run.phase != EndlessPhase.Fighting)
            {
                return;
            }
            Run.phase = EndlessPhase.Upgrade;
            RollChoices();
            string text = ChoiceText(Run.choices[0]).Replace("\n", " ");
            if (Choose(0))
            {
                PushBanner("升级 Lv" + Run.level + "！" + text);
            }
        }
        /// <summary>风险事件同样不弹窗：自动选“稳一手”的收益，推横幅说明。</summary>
        private void AutoEvent()
        {
            int kind = Run.eventKind;
            if (SelectEvent(false))
            {
                PushBanner("事件 · " + EventNames[kind] + "：" + EventSafeText(kind));
            }
        }
        /// <summary>巡回突破与敌方词条的名字（按巡回数轮换），横幅与 HUD 共用。</summary>
        public static readonly string[] BreakthroughNames =
        {
            "多重弩阵", "灼尽再燃", "棋心回响", "临阵换锋"
        };
        public static readonly string[] AffixNames =
        {
            "双门奔袭", "碎盾潮", "潮汐冲锋", "断线精英"
        };
        /// <summary>四种风险事件的名字，HUD 横幅和旧版弹窗共用。</summary>
        public static readonly string[] EventNames =
        {
            "遗迹守阵", "暴走潮", "刻印交换", "救援棋手"
        };
        /// <summary>“稳一手”实际给了什么，数字从表里读，和真实效果一致。</summary>
        private static string EventSafeText(int kind)
        {
            return kind == 0 ? "获得一张同名牌" : kind == 1 ? "回复棋心 " + N("EventHeal") : kind == 2 ? "刻印保持不变" : "免费补一张牌";
        }
        /// <summary>最新一条横幅文字与序号；HUD 记住读到的序号，序号变了就弹一次。</summary>
        public string Banner
        {
            get;
            private set;
        }
        = "";
        public int BannerSerial
        {
            get;
            private set;
        }
        /// <summary>
        /// 大横幅（领主来袭、领主奖励）单独一条通道：击杀领主的同一步里往往还会升级、推小横幅，
        /// 如果和小横幅共用“只显示最新一条”，增益提示会被立刻盖掉。HUD 按 MajorBannerSerial 单独弹出奖励卡。
        /// </summary>
        public string MajorBanner
        {
            get;
            private set;
        }
        = "";
        public int MajorBannerSerial
        {
            get;
            private set;
        }
        /// <summary>大横幅正文颜色（#RRGGBB，来自 Excel；空串表示 HUD 默认金色）。</summary>
        public string MajorBannerColor
        {
            get;
            private set;
        }
        = "";
        /// <summary>领主来袭横幅的文字颜色（纯表现常量：警示红，不是战斗数值）。</summary>
        private const string BossBannerColor = "#FF5A5A";
        /// <summary>推一条小横幅（右上角，2.8 秒）。</summary>
        private void PushBanner(string text)
        {
            Banner = text;
            BannerSerial++;
        }
        /// <summary>推一条大横幅（右侧奖励卡，放大回弹弹入、停留更久）；color 为空用 HUD 默认金色。</summary>
        private void PushMajorBanner(string text, string color)
        {
            MajorBanner = text;
            MajorBannerColor = color ?? "";
            MajorBannerSerial++;
        }
        private void RollChoices()
        {
            int count = 0;
            for (int i = 0; i < _candidates.Length; i++)
            {
                if (LegalChoice(i))
                {
                    _candidates[count++] = i;
                }
            }
            for (int i = 0; i < Run.choices.Length; i++)
            {
                if (count == 0)
                {
                    Run.choices[i] = -1;
                    continue;
                }
                int at = Next(count);
                // 第一项优先已有路线升级/缺失辅印，另外两项保留转型自由。
                if (i == 0)
                {
                    for (int n = 0; n < count; n++)
                    {
                        int candidate = _candidates[n];
                        if (candidate < 12 && (Run.mains[candidate % 6] > 0 || candidate == Run.formation))
                        {
                            at = n;
                            break;
                        }
                    }
                }
                Run.choices[i] = _candidates[at];
                _candidates[at] = _candidates[--count];
            }
        }
        public string ChoiceText(int id)
        {
            if (id < 0)
            {
                return Run.heart < V("HeartHp") ? "棋心补给\n回复 " + N("SupplyHeal") : "战绩补给\n积分 +" + N("SupplyScore");
            }
            int route = id % 6;
            if (id >= 12)
            {
                return RareText(id - 12);
            }
            var config = EndlessConfig.Route(route);
            if (id >= 6)
            {
                return config.Seal + " · Lv" + (Run.seals[route] + 1) + "\n" + config.Evolution + "的配方辅印";
            }
            return config.Blessing + " · Lv" + (Run.mains[route] + 1) + "\n" + config.Description;
        }
        public bool Choose(int index)
        {
            if (Run.phase != EndlessPhase.Upgrade || index < 0 || index >= Run.choices.Length)
            {
                return Reject("当前没有这项升级");
            }
            int choice = Run.choices[index];
            if (!LegalChoice(choice))
            {
                return Reject("此项已满或被放逐");
            }
            if (choice < 0)
            {
                if (Run.heart < V("HeartHp"))
                {
                    Run.heart = Mathf.Min(V("HeartHp"), Run.heart + V("SupplyHeal"));
                }
                else
                {
                    AddScore(N("SupplyScore"));
                }
            }
            else if (choice >= 12)
            {
                Run.rare[choice - 12] = true;
                if (choice == 14)
                {
                    for (int i = 0; i < Units.Count; i++)
                    {
                        if (Units[i].Piece.Side == GameSide.Player)
                        {
                            Units[i].Piece.SetRoundHealthScale(PlayerHealthScale());
                        }
                    }
                }
            }
            else if (choice >= 6)
            {
                Run.seals[choice % 6]++;
            }
            else
            {
                Run.mains[choice]++;
            }
            Run.pendingLevels--;
            Run.phase = EndlessPhase.Fighting;
            Run.upgradeStreak++;
            if (Run.upgradeStreak >= 2)
            {
                Run.nextChoiceAt = Run.seconds + V("ChoiceBreather");
                Run.upgradeStreak = 0;
            }
            Save();
            return true;
        }
        public bool Reroll()
        {
            if (Run.phase != EndlessPhase.Upgrade || Run.rerolls <= 0)
            {
                return Reject("本局免费重抽已用完");
            }
            Run.rerolls--;
            RollChoices();
            return true;
        }
        public bool Banish(int index)
        {
            if (Run.phase != EndlessPhase.Upgrade || Run.banishes <= 0 || index < 0 || index >= 3 || Run.choices[index] < 0)
            {
                return Reject("当前不能放逐此项");
            }
            Run.banned[Run.choices[index]] = true;
            Run.banishes--;
            RollChoices();
            return true;
        }
        public bool Evolve(int route)
        {
            if (route < 0 || route >= 6 || Run.phase == EndlessPhase.Result || Run.mains[route] < N("MainMax") || Run.seals[route] == 0 || Run.cores == 0 || Run.evolved[route])
            {
                return Reject("需要满级主祝福、对应辅印和核心，且未进化");
            }
            Run.cores--;
            Run.evolved[route] = true;
            Impact(new Vector2(4, 4), new Vector2(4, 4), route, V("AreaLimit"));
            Save();
            return true;
        }
        private void GiveCards(int count)
        {
            for (int i = 0; i < count && Run.hand.Count < N("HandLimit"); i++)
            {
                Run.hand.Add(Next(Enum.GetValues(typeof(PieceKind)).Length));
            }
        }
        /// <summary>旧按钮契约明确拒绝三合一；无尽升级只能消费一张同名手牌，不自动吃掉场上同名棋子。</summary>
        public bool MergeSelected()
        {
            return Reject("选择同名手牌打出即可升级一枚棋子，场上棋子不会自动融合");
        }
        public bool Disassemble()
        {
            if (!CanArrange() || Run.selectedCard < 0 || Run.selectedCard >= Run.hand.Count)
            {
                return Reject("先选择一张待分解手牌");
            }
            Run.hand.RemoveAt(Run.selectedCard);
            Run.selectedCard = -1;
            Run.disassemblyPoints++;
            return true;
        }
        public bool SelectEvent(bool risk)
        {
            if (Run.phase != EndlessPhase.Event)
            {
                return Reject("当前没有事件选择");
            }
            if (risk)
            {
                if (Run.eventKind == 2)
                {
                    for (int i = 0; i < Run.seals.Length; i++)
                    {
                        if (Run.seals[i] > 0 && Run.seals[i] < N("SealMax") && Run.seals[(i + 1) % 6] == 0)
                        {
                            Run.seals[(i + 1) % 6] = Math.Max(1, Mathf.FloorToInt(Run.seals[i] * V("ReplacementRefund")));
                            Run.seals[i] = 0;
                            break;
                        }
                    }
                }
                else
                {
                    Run.riskKind = Run.eventKind;
                    Run.riskEnds = Run.seconds + V("RiskSeconds");
                    Run.riskKillsAt = Run.kills + Run.invaderKills;
                    Run.riskDamageAt = Run.heartDamageEvents;
                    // 敌人只从左右两侧来（见 NextLane），遗迹也只能放在这两个门点上，否则永远没人去打它。
                    Run.relicGate = Next(2);
                    Run.relicHp = V("RelicHp");
                    Run.riskObjectiveDone = false;
                    if (Run.eventKind == 1)
                    {
                        Run.riskUntil = Run.riskEnds;
                    }
                }
            }
            else
            {
                if (Run.eventKind == 0)
                {
                    GiveNamedCard();
                }
                else if (Run.eventKind == 1)
                {
                    Run.heart = Mathf.Min(V("HeartHp"), Run.heart + V("EventHeal"));
                }
                else if (Run.eventKind == 3)
                {
                    GiveCards(1);
                }
            }
            Run.phase = EndlessPhase.Fighting;
            return true;
        }
        public bool ContinueCircuit(int breakthrough, int affix)
        {
            if (Run.phase != EndlessPhase.Circuit || breakthrough < 0 || breakthrough >= 4 || affix < 0 || affix >= 4)
            {
                return Reject("当前不能继续巡回，或词条选择非法");
            }
            if (Run.breakthroughs.Count < N("SealSlots"))
            {
                Run.breakthroughs.Add(breakthrough);
            }
            else
            {
                GiveCards(1);
            }
            if (Run.affixes.Count >= N("SealSlots"))
            {
                Run.affixes.RemoveAt(0);
            }
            Run.affixes.Add(affix);
            Run.circuit++;
            Run.heart = Mathf.Min(V("HeartHp"), Run.heart + V("HeartHp") * V("CircuitHeal"));
            // 直接回到战斗：无尽全程不停顿。
            Run.phase = EndlessPhase.Fighting;
            Save();
            return true;
        }
        public void Finish()
        {
            if (Run == null || Run.settled)
            {
                return;
            }
            Run.phase = EndlessPhase.Result;
            Run.settled = true;
            Run.bankedScore = Run.score;
            RecordProgress();
            Save();
        }
    }
}
