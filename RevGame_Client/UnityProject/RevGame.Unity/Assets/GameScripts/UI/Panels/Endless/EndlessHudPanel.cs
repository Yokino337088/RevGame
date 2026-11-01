using System;
using System.Text;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;
namespace RevGame.Endless
{
    /// <summary>
    /// 无尽专属HUD：上方生存/棋心，左侧有限构筑槽，下方手牌，右侧阶段操作。
    /// OnBind只装配，刷新只落屏，按钮只转发；5Hz文字刷新避免每帧重建整块Canvas。
    /// </summary>
    [RevUIPanel("UI/Panel/Endless", RevUILayer.Normal)]
    public sealed class EndlessHudPanel : RevUIPanel
    {
        [RevBind("top/status")] private Text _status;
        [RevBind("top/heartTrack/heartFill")] private RectTransform _heartFill;
        private Image _heartImage;
        [RevBind("left/build")] private Text _build;
        [RevBind("right/info")] private Text _info;
        [RevBind("bottom/experience")] private Text _experience;
        [RevBind("modal/title")] private Text _modalTitle;
        [RevBind("modal")] private GameObject _modal;
        private readonly Text[] _cards = new Text[6], _choices = new Text[3];
        private readonly StringBuilder _text = new StringBuilder(1024);
        private EndlessArena _arena;
        private bool _opening;
        private int _version;
        // 配方查看只持有路线编号；战斗中展开时使用服务暂停，关闭时恢复原阶段。
        private int _routeDetail = -1;
        private bool _pausedForDetail;
        private int _breakthrough, _affix;
        private static readonly string[] BreakthroughNames =
        {
            "多重弩阵", "灼尽再燃", "棋心回响", "临阵换锋"
        };
        private static readonly string[] AffixNames =
        {
            "双门奔袭", "碎盾潮", "潮汐冲锋", "断线精英"
        };
        public object PerformanceSnapshot()
        {
            return _arena == null ? null : _arena.Metrics();
        }
        private EndlessService Service
        {
            get
            {
                return RevServices.GetRequired<EndlessService>();
            }
        }
        protected override void OnBindView()
        {
            _heartImage = _heartFill.GetComponent<Image>();
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i] = transform.Find("bottom/card" + i + "/label").GetComponent<Text>();
            }
            for (int i = 0; i < _choices.Length; i++)
            {
                _choices[i] = transform.Find("modal/choice" + i + "/label").GetComponent<Text>();
            }
        }
        protected override void OnOpen()
        {
            _opening = true;
            _version++;
            RevTimer.Every(0.2f, RefreshView, owner: this);
        }
        /// <summary>过场屏障等真实池对象与全部棋子可画；退出/失败时释放迟到的异步租约。</summary>
        public async RevTask PrepareAsync()
        {
            int version = _version;
            bool returned = false;
            GamePoolRequests.GetAsync<EndlessArena>(arena =>
            {
                if (!_opening || version != _version)
                {
                    if (arena != null)
                    {
                        RevPool.Return(arena);
                    }
                }
                else
                {
                    _arena = arena;
                }
                returned = true;
            }
            );
            float deadline = Time.realtimeSinceStartup + 30;
            while (_opening && (!returned || (_arena != null && !_arena.Ready && string.IsNullOrEmpty(_arena.Error))))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException("无尽棋盘资源就绪超时");
                }
                await RevTask.Yield();
            }
            if (_arena == null || !_arena.Ready)
            {
                throw new InvalidOperationException(_arena == null ? "无尽棋盘预制体加载失败" : _arena.Error);
            }
            RefreshView();
        }
        private static void Set(Text text, string value)
        {
            if (text.text != value)
            {
                text.text = value;
            }
        }
        protected override void OnRefreshView()
        {
            EndlessRun run = Service.Run;
            if (run == null)
            {
                return;
            }
            string phase = PhaseName(run.phase);
            float heartRatio = run.heart / EndlessConfig.Value("HeartHp");
            _heartFill.anchorMax = new Vector2(heartRatio, 1);
            // 血条图本身是金色：健康时不染色，危险时才染红，提示更醒目。
            _heartImage.color = heartRatio < 0.3f ? new Color(1, .3f, .22f) : Color.white;
            Set(_status, "◆ 棋心 " + Mathf.CeilToInt(run.heart) + " / " + EndlessConfig.Count("HeartHp") + "     "
            + ((int)run.seconds / 60).ToString("00") + ":" + ((int)run.seconds % 60).ToString("00")
            + "     巡回 " + run.circuit + "     " + phase + "     击杀 " + (run.kills + run.invaderKills));
            _text.Clear();
            _text.Append("构筑档案\n主祝福 ").Append(Slots(run.mains) + Service.RareSlots()).Append('/').Append(EndlessConfig.Count("MainSlots"));
            _text.Append(" · 辅印 ").Append(Slots(run.seals)).Append('/').Append(EndlessConfig.Count("SealSlots")).Append('\n');
            for (int i = 0; i < 6; i++)
            {
                var route = EndlessConfig.Route(i);
                if (run.mains[i] > 0 || run.seals[i] > 0)
                {
                    _text.Append('\n').Append(run.evolved[i] ? "★ " + route.Evolution : route.Blessing + " Lv" + run.mains[i]);
                    _text.Append('\n').Append(route.Seal).Append(" Lv").Append(run.seals[i]);
                    _text.Append('\n').Append("配方：主Lv").Append(EndlessConfig.Count("MainMax")).Append(" + 印 + 核心\n");
                }
            }
            _text.Append("\n核心 ").Append(run.cores).Append(" · 碎片 ").Append(run.fragments);
            for (int i = 0; i < run.rare.Length; i++)
            {
                if (run.rare[i])
                {
                    _text.Append("\n").Append(EndlessService.RareText(i).Split('\n')[0]);
                }
            }
            Set(_build, _text.ToString());
            EndlessUnit boss = Service.BossUnit();
            string bossText = boss == null ? "下个领主\n" + Mathf.CeilToInt(Mathf.Max(0, run.nextBoss - run.seconds)) + " 战斗秒" : "第" + boss.BossChapter + "章领主\n" + boss.Piece.Hp + " / " + boss.Piece.MaxHp + (boss.BrokenUntil > run.seconds ? "\n破阵！承伤提升" : "");
            if (boss != null && Service.BossBanner.Length > 0)
            {
                bossText += "\n\n" + Service.BossBanner;
            }
            Set(_info, bossText + "\n\n敌潮 " + Service.HordeCount() + " / " + EndlessConfig.Count("HordeLimit")
            + "\n主力 " + Service.PlayerCount() + " / " + EndlessConfig.Count("PlayerLimit")
            + "\n占地 " + Service.Occupied(GameSide.Player) + " / " + EndlessConfig.Count("PlayerCells")
            + "\n\n" + (run.slowLeft > 0 ? "改阵 " + run.slowLeft.ToString("F1") + " 秒\n剩余移动 " + run.slowMoves : "先选棋，再点空格\n整备可合星、补牌")
            + "\n\n" + Service.LastError + "\n" + run.error);
            Set(_experience, "Lv " + run.level + "     经验 " + run.xp + " / " + Service.ExperienceNeeded() + "     战绩 " + run.score + "     " + (run.selectedCard >= 0 ? "请点击棋盘空格部署" : "点击手牌选择 · 点击己棋改阵"));
            for (int i = 0; i < _cards.Length; i++)
            {
                Set(_cards[i], i < run.hand.Count ? (run.selectedCard == i ? "▶ " : "") + PieceCatalog.Get((PieceKind)run.hand[i], StarTier.One).DisplayName : "空槽");
            }
            bool modal = _routeDetail >= 0 || run.phase == EndlessPhase.Upgrade || run.phase == EndlessPhase.Event || run.phase == EndlessPhase.Circuit || run.phase == EndlessPhase.Paused || run.phase == EndlessPhase.Result;
            _modal.SetActive(modal);
            if (!modal)
            {
                return;
            }
            string title = phase;
            if (_routeDetail >= 0)
            {
                var route = EndlessConfig.Route(_routeDetail);
                int next = NextEmpty(_routeDetail, false), seal = NextEmpty(_routeDetail, true);
                title = route.Evolution + " · 完整配方\n" + route.Blessing + " Lv" + run.mains[_routeDetail] + "/" + EndlessConfig.Count("MainMax") + " + " + route.Seal + " Lv" + run.seals[_routeDetail] + " + 核心 " + run.cores;
                Set(_choices[0], "进化此路线\n" + route.Description);
                Set(_choices[1], "整备换主祝福\n" + route.Blessing + " → " + EndlessConfig.Route(next).Blessing + "\n返还部分投入经验");
                Set(_choices[2], "整备换辅印\n" + route.Seal + " → " + EndlessConfig.Route(seal).Seal + "\n保留部分等级");
            }
            else if (run.phase == EndlessPhase.Upgrade)
            {
                title = "选择一次质变 · 战斗已暂停";
                for (int i = 0; i < 3; i++)
                {
                    Set(_choices[i], Service.ChoiceText(run.choices[i]));
                }
            }
            else if (run.phase == EndlessPhase.Event)
            {
                string[] eventNames =
                {
                    "遗迹守阵", "暴走潮", "刻印交换", "救援棋手"
                };
                title = eventNames[run.eventKind] + " · 选择已明确后果的路线";
                string[] safe =
                {
                    "获得已有棋同名牌", "回复棋心 " + EndlessConfig.Count("EventHeal"), "保留当前辅印", "免费补一张牌"
                };
                string[] risk =
                {
                    "保护棋心 " + EndlessConfig.Count("RiskSeconds") + " 秒\n无突破伤害则获碎片与路线升级", "出怪预算短时增加\n成功清敌获得碎片与同名牌", SealExchangePreview(run), "边缘救援 " + EndlessConfig.Count("RiskSeconds") + " 秒\n完成击杀获得定向同名牌"
                };
                Set(_choices[0], "稳健\n" + safe[run.eventKind]);
                Set(_choices[1], "挑战\n" + risk[run.eventKind]);
                Set(_choices[2], "稳健\n" + safe[run.eventKind]);
            }
            else if (run.phase == EndlessPhase.Circuit)
            {
                title = "巡回完成 · 收益已记入\n突破：" + BreakthroughNames[_breakthrough] + " · 下轮侵袭：" + AffixNames[_affix];
                Set(_choices[0], "继续无尽\n保留阵形和构筑\n棋心部分修复");
                Set(_choices[1], "检查当前配方\n确认后再继续");
                Set(_choices[2], "结算带走");
            }
            else if (run.phase == EndlessPhase.Paused)
            {
                title = "棋阵静止 · 所有战斗时钟暂停";
                Set(_choices[0], "继续战斗");
                Set(_choices[1], "保存并返回大厅");
                Set(_choices[2], "放弃并结算\n保留已获得战绩");
            }
            else
            {
                title = "本局战绩 " + run.score + " · 完成巡回 " + run.completedCircuits + "\n领主 " + run.bossKills + " · 精英 " + run.eliteKills + " · 最大连锁 " + run.maximumChain + "\n棋心最后受伤：" + run.lastDamage;
                Set(_choices[0], "返回大厅");
                Set(_choices[1], "返回大厅\n尝试另一种棋阵");
                Set(_choices[2], "返回大厅");
            }
            Set(_modalTitle, title);
            bool upgrade = _routeDetail < 0 && run.phase == EndlessPhase.Upgrade;
            transform.Find("modal/reroll").gameObject.SetActive(upgrade);
            for (int i = 0; i < 3; i++)
            {
                transform.Find("modal/banish" + i).gameObject.SetActive(upgrade);
            }
            transform.Find("modal/detailClose").gameObject.SetActive(_routeDetail >= 0);
            transform.Find("modal/breakthrough").gameObject.SetActive(_routeDetail < 0 && run.phase == EndlessPhase.Circuit);
            transform.Find("modal/affix").gameObject.SetActive(_routeDetail < 0 && run.phase == EndlessPhase.Circuit);
        }
        private static int Slots(int[] values)
        {
            int n = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] > 0)
                {
                    n++;
                }
            }
            return n;
        }
        private static string PhaseName(EndlessPhase phase)
        {
            string[] names =
            {
                "开局布阵", "持续迎战", "升级选择", "分钟整备", "风险事件", "巡回结算", "暂停", "本局结束"
            };
            return names[(int)phase];
        }
        private void Choice(int index)
        {
            if (_routeDetail >= 0)
            {
                if (index == 0)
                {
                    Service.Evolve(_routeDetail);
                }
                else
                {
                    Service.ReplaceRoute(_routeDetail, NextEmpty(_routeDetail, index == 2), index == 2);
                }
                CloseDetail();
                return;
            }
            EndlessPhase phase = Service.Run.phase;
            if (phase == EndlessPhase.Upgrade)
            {
                Service.Choose(index);
            }
            else if (phase == EndlessPhase.Event)
            {
                Service.SelectEvent(index == 1);
            }
            else if (phase == EndlessPhase.Circuit)
            {
                if (index == 2)
                {
                    Service.Finish();
                }
                else if (index == 1)
                {
                    Detail(Service.Run.formation);
                }
                else
                {
                    Service.ContinueCircuit(_breakthrough, _affix);
                }
            }
            else if (phase == EndlessPhase.Paused)
            {
                if (index == 0)
                {
                    Service.Pause();
                }
                else if (index == 1)
                {
                    SaveExit();
                }
                else
                {
                    Service.Finish();
                }
            }
            else if (phase == EndlessPhase.Result)
            {
                RevServices.GetRequired<GameFlow>().ReturnHome();
            }
            RefreshView();
        }
        private void SaveExit()
        {
            if (Service.Save())
            {
                RevServices.GetRequired<GameFlow>().ReturnHome();
            }
        }
        private int NextEmpty(int route, bool seal)
        {
            int[] values = seal ? Service.Run.seals : Service.Run.mains;
            for (int i = 1; i < 6; i++)
            {
                int next = (route + i) % 6;
                if (values[next] == 0)
                {
                    return next;
                }
            }
            return route;
        }
        private static string SealExchangePreview(EndlessRun run)
        {
            for (int i = 0; i < 6; i++)
            {
                if (run.seals[i] > 0 && run.seals[i] < EndlessConfig.Count("SealMax") && run.seals[(i + 1) % 6] == 0)
                {
                    return EndlessConfig.Route(i).Seal + " Lv" + run.seals[i] + " → " + EndlessConfig.Route((i + 1) % 6).Seal + " Lv" + Math.Max(1, Mathf.FloorToInt(run.seals[i] * EndlessConfig.Value("ReplacementRefund")));
                }
            }
            return "没有可交换的未满级辅印\n本次保持原样";
        }
        private void Detail(int route)
        {
            _routeDetail = route;
            _pausedForDetail = Service.Run.phase == EndlessPhase.Fighting;
            if (_pausedForDetail)
            {
                Service.Pause();
            }
            RefreshView();
        }
        [RevButtonClick("modal/detailClose")] private void CloseDetail()
        {
            _routeDetail = -1;
            if (_pausedForDetail && Service.Run.phase == EndlessPhase.Paused)
            {
                Service.Pause();
            }
            _pausedForDetail = false;
            RefreshView();
        }
        [RevButtonClick("left/route0")] private void Route0()
        {
            Detail(0);
        }
        [RevButtonClick("left/route1")] private void Route1()
        {
            Detail(1);
        }
        [RevButtonClick("left/route2")] private void Route2()
        {
            Detail(2);
        }
        [RevButtonClick("left/route3")] private void Route3()
        {
            Detail(3);
        }
        [RevButtonClick("left/route4")] private void Route4()
        {
            Detail(4);
        }
        [RevButtonClick("left/route5")] private void Route5()
        {
            Detail(5);
        }
        [RevButtonClick("right/disassemble")] private void Disassemble()
        {
            Service.Disassemble();
            RefreshView();
        }
        [RevButtonClick("right/exchange")] private void Exchange()
        {
            var selected = Service.Board.FindPiece(Service.Run.selectedUnit);
            if (selected != null)
            {
                Service.ExchangeCard((int)selected.Def.Kind);
            }
            RefreshView();
        }
        [RevButtonClick("right/flash")] private void Flash()
        {
            Service.Run.flashes = !Service.Run.flashes;
            RefreshView();
        }
        [RevButtonClick("right/shake")] private void Shake()
        {
            Service.Run.shake = !Service.Run.shake;
            RefreshView();
        }
        [RevButtonClick("modal/breakthrough")] private void CycleBreakthrough()
        {
            _breakthrough = (_breakthrough + 1) % 4;
            RefreshView();
        }
        [RevButtonClick("modal/affix")] private void CycleAffix()
        {
            _affix = (_affix + 1) % 4;
            RefreshView();
        }
        [RevButtonClick("right/start")] private void StartWave()
        {
            Service.StartWave();
            RefreshView();
        }
        [RevButtonClick("right/rearrange")] private void Rearrange()
        {
            Service.Rearrange();
            RefreshView();
        }
        [RevButtonClick("right/merge")] private void Merge()
        {
            Service.MergeSelected();
            RefreshView();
        }
        [RevButtonClick("right/evolve")] private void Evolve()
        {
            for (int i = 0; i < 6; i++)
            {
                if (Service.Run.mains[i] == EndlessConfig.Count("MainMax") && Service.Run.seals[i] > 0 && !Service.Run.evolved[i])
                {
                    Service.Evolve(i);
                    break;
                }
            }
            RefreshView();
        }
        [RevButtonClick("top/pause")] private void Pause()
        {
            Service.Pause();
            RefreshView();
        }
        [RevButtonClick("right/effects")] private void Effects()
        {
            Service.Run.lowEffects = !Service.Run.lowEffects;
            RefreshView();
        }
        [RevButtonClick("modal/choice0")] private void Choice0()
        {
            Choice(0);
        }
        [RevButtonClick("modal/choice1")] private void Choice1()
        {
            Choice(1);
        }
        [RevButtonClick("modal/choice2")] private void Choice2()
        {
            Choice(2);
        }
        [RevButtonClick("modal/reroll")] private void Reroll()
        {
            Service.Reroll();
            RefreshView();
        }
        [RevButtonClick("modal/banish0")] private void Banish0()
        {
            Service.Banish(0);
            RefreshView();
        }
        [RevButtonClick("modal/banish1")] private void Banish1()
        {
            Service.Banish(1);
            RefreshView();
        }
        [RevButtonClick("modal/banish2")] private void Banish2()
        {
            Service.Banish(2);
            RefreshView();
        }
        [RevButtonClick("bottom/card0")] private void Card0()
        {
            Service.SelectCard(0);
            RefreshView();
        }
        [RevButtonClick("bottom/card1")] private void Card1()
        {
            Service.SelectCard(1);
            RefreshView();
        }
        [RevButtonClick("bottom/card2")] private void Card2()
        {
            Service.SelectCard(2);
            RefreshView();
        }
        [RevButtonClick("bottom/card3")] private void Card3()
        {
            Service.SelectCard(3);
            RefreshView();
        }
        [RevButtonClick("bottom/card4")] private void Card4()
        {
            Service.SelectCard(4);
            RefreshView();
        }
        [RevButtonClick("bottom/card5")] private void Card5()
        {
            Service.SelectCard(5);
            RefreshView();
        }
        protected override void OnClose()
        {
            _opening = false;
            _routeDetail = -1;
            _pausedForDetail = false;
            _version++;
            RevTimer.CancelAllOf(this);
            if (_arena != null)
            {
                RevPool.Return(_arena);
                _arena = null;
            }
        }
    }
}
