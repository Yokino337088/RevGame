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
    /// 无尽战斗 HUD（新手先读这段）。
    ///
    /// 【设计原则：手机上一眼看懂、一根手指玩】
    /// 平时屏幕上只有：棋心血条、暂停、手牌、等级；其余按钮只在用得上的时候才出现：
    ///   - “开战”只在布阵和整备时出现；
    ///   - 领主血条只在领主在场时出现；
    ///   - 提示框只在需要告诉玩家下一步时出现。
    /// 三合一、进化由规则自动完成，移动棋子只要点棋子，都不需要按钮。
    ///
    /// 【代码分工】OnBindView 只找节点，OnRefreshView 只把数据写到屏幕，按钮方法只转发给 EndlessService。
    /// 每秒刷新 5 次文字，不是每帧，这样不会频繁重建整块 Canvas。
    /// </summary>
    [RevUIPanel("UI/Panel/Endless", RevUILayer.Normal)]
    public sealed class EndlessHudPanel : RevUIPanel
    {
        [RevBind("top/status")] private Text _status;
        [RevBind("top/heartTrack/heartFill")] private RectTransform _heartFill;
        [RevBind("boss")] private GameObject _boss;
        [RevBind("boss/bossTrack/bossFill")] private RectTransform _bossFill;
        [RevBind("boss/name")] private Text _bossName;
        [RevBind("tip")] private GameObject _tip;
        [RevBind("tip/text")] private Text _tipText;
        [RevBind("start")] private GameObject _start;
        [RevBind("start/label")] private Text _startLabel;
        [RevBind("level/label")] private Text _level;
        [RevBind("bottom/xpTrack/xpFill")] private RectTransform _xpFill;
        [RevBind("modal")] private GameObject _modal;
        [RevBind("modal/title")] private Text _modalTitle;
        [RevBind("modal/reroll")] private GameObject _reroll;
        /// <summary>棋心血条的图片，危险时染红。</summary>
        private Image _heartImage;
        /// <summary>手牌的三部分：按钮本身、棋子头像、名字。下标就是手牌序号。</summary>
        private readonly GameObject[] _cards = new GameObject[6];
        private readonly Image[] _cardImages = new Image[6], _cardIcons = new Image[6];
        private readonly Text[] _cardNames = new Text[6];
        /// <summary>弹窗里的三个大选项。只有两个选项时用左右两张（0 和 2），只有一个时用中间（1）。</summary>
        private readonly GameObject[] _choices = new GameObject[3];
        private readonly Text[] _choiceTexts = new Text[3];
        private readonly StringBuilder _text = new StringBuilder(256);
        private EndlessArena _arena;
        private bool _opening;
        private int _version;
        /// <summary>上一次看到的错误，以及这条错误显示到什么时候。错误只显示 2.5 秒，不长期占屏幕。</summary>
        private string _lastError = "";
        private float _errorUntil;
        private static readonly Color SelectedCard = new Color(1f, .86f, .42f);
        private static readonly Color DimCard = new Color(.72f, .70f, .68f);
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
                Transform card = transform.Find("bottom/card" + i);
                _cards[i] = card.gameObject;
                _cardImages[i] = card.GetComponent<Image>();
                _cardIcons[i] = card.Find("icon").GetComponent<Image>();
                _cardNames[i] = card.Find("label").GetComponent<Text>();
            }
            for (int i = 0; i < _choices.Length; i++)
            {
                Transform choice = transform.Find("modal/choice" + i);
                _choices[i] = choice.gameObject;
                _choiceTexts[i] = choice.Find("label").GetComponent<Text>();
            }
        }
        protected override void OnOpen()
        {
            _opening = true;
            _version++;
            _lastError = "";
            _errorUntil = 0;
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
        /// <summary>只在内容变化时才写文字：给 Text 赋值会让 Canvas 重建，能省则省。</summary>
        private static void Set(Text text, string value)
        {
            if (text.text != value)
            {
                text.text = value;
            }
        }
        /// <summary>只在状态变化时才切换显示，原因同上。</summary>
        private static void Show(GameObject target, bool visible)
        {
            if (target.activeSelf != visible)
            {
                target.SetActive(visible);
            }
        }
        protected override void OnRefreshView()
        {
            EndlessRun run = Service.Run;
            if (run == null)
            {
                return;
            }
            RefreshTop(run);
            RefreshBoss(run);
            RefreshHand(run);
            RefreshTip(run);
            RefreshModal(run);
        }
        /// <summary>顶部：棋心血条 + 时间 + 击杀。</summary>
        private void RefreshTop(EndlessRun run)
        {
            float heartRatio = Mathf.Clamp01(run.heart / EndlessConfig.Value("HeartHp"));
            _heartFill.anchorMax = new Vector2(heartRatio, 1);
            // 血条图本身是金色：健康时不染色，危险时才染红，提示更醒目。
            _heartImage.color = heartRatio < 0.3f ? new Color(1, .3f, .22f) : Color.white;
            Set(_status, "棋心 " + Mathf.CeilToInt(run.heart) + "     " + ((int)run.seconds / 60).ToString("00") + ":" + ((int)run.seconds % 60).ToString("00") + "     击杀 " + (run.kills + run.invaderKills));
        }
        /// <summary>领主血条：只有领主在场时才显示。</summary>
        private void RefreshBoss(EndlessRun run)
        {
            EndlessUnit boss = Service.BossUnit();
            Show(_boss, boss != null);
            if (boss != null)
            {
                _bossFill.anchorMax = new Vector2(boss.Piece.HpRatio, 1);
                Set(_bossName, boss.BrokenUntil > run.seconds ? "领主破阵！快打" : "第" + boss.BossChapter + "章领主");
            }
        }
        /// <summary>底部：手牌（没牌的格子直接隐藏）、等级、经验条、开战按钮。</summary>
        private void RefreshHand(EndlessRun run)
        {
            bool arranging = run.phase == EndlessPhase.Formation || run.phase == EndlessPhase.Preparation;
            for (int i = 0; i < _cards.Length; i++)
            {
                bool has = i < run.hand.Count;
                Show(_cards[i], has);
                if (!has)
                {
                    continue;
                }
                var kind = (PieceKind)run.hand[i];
                Set(_cardNames[i], PieceCatalog.Get(kind, StarTier.One).DisplayName);
                // 被选中的牌染金，不能出牌的时候整体变暗，玩家不用读字就知道状态。
                _cardImages[i].color = run.selectedCard == i ? SelectedCard : arranging ? Color.white : DimCard;
                Sprite icon = _arena == null ? null : _arena.PieceIcon(kind);
                if (icon != null && _cardIcons[i].sprite != icon)
                {
                    _cardIcons[i].sprite = icon;
                }
            }
            Set(_level, "Lv " + run.level);
            _xpFill.anchorMax = new Vector2(Mathf.Clamp01((float)run.xp / Mathf.Max(1, Service.ExperienceNeeded())), 1);
            Show(_start, arranging);
            if (arranging)
            {
                Set(_startLabel, run.phase == EndlessPhase.Formation ? "开战 " + Mathf.CeilToInt(run.formationLeft) : "开战");
            }
        }
        /// <summary>提示框：永远只说“现在该做什么”这一件事。优先级：错误 → 领主招式 → 操作引导。</summary>
        private void RefreshTip(EndlessRun run)
        {
            if (Service.LastError != _lastError)
            {
                _lastError = Service.LastError;
                _errorUntil = string.IsNullOrEmpty(_lastError) ? 0 : Time.unscaledTime + 2.5f;
            }
            string tip;
            if (Time.unscaledTime < _errorUntil)
            {
                tip = _lastError;
            }
            else if (Service.BossBanner.Length > 0)
            {
                tip = Service.BossBanner;
            }
            else if (run.selectedCard >= 0)
            {
                tip = "点棋盘空格摆放\n点同名棋子可升星";
            }
            else if (run.phase == EndlessPhase.Formation || run.phase == EndlessPhase.Preparation)
            {
                tip = run.hand.Count > 0 ? "点手牌，再点棋盘\n摆好后点开战" : "点棋子可移动\n准备好就开战";
            }
            else if (run.phase == EndlessPhase.Fighting && run.slowLeft > 0)
            {
                tip = run.selectedUnit >= 0 ? "点空格移动过去" : "时间变慢了\n点棋子再点空格";
            }
            else
            {
                tip = "";
            }
            Show(_tip, tip.Length > 0);
            Set(_tipText, tip);
        }
        /// <summary>弹窗：升级三选一、事件二选一、巡回二选一、暂停、结算。每种都只有大按钮。</summary>
        private void RefreshModal(EndlessRun run)
        {
            bool modal = run.phase == EndlessPhase.Upgrade || run.phase == EndlessPhase.Event || run.phase == EndlessPhase.Circuit || run.phase == EndlessPhase.Paused || run.phase == EndlessPhase.Result;
            Show(_modal, modal);
            if (!modal)
            {
                return;
            }
            string left = null, middle = null, right = null, title;
            if (run.phase == EndlessPhase.Upgrade)
            {
                title = "升级！选一个";
                left = Service.ChoiceText(run.choices[0]);
                middle = Service.ChoiceText(run.choices[1]);
                right = Service.ChoiceText(run.choices[2]);
            }
            else if (run.phase == EndlessPhase.Event)
            {
                string[] eventNames =
                {
                    "遗迹守阵", "暴走潮", "刻印交换", "救援棋手"
                };
                string[] safe =
                {
                    "获得一张同名牌", "回复棋心 " + EndlessConfig.Count("EventHeal"), "什么都不变", "免费补一张牌"
                };
                string[] risk =
                {
                    "保护棋心 " + EndlessConfig.Count("RiskSeconds") + " 秒\n成功得碎片和升级", "敌人暂时变多\n成功得碎片和同名牌", "交换一个辅印", "救援 " + EndlessConfig.Count("RiskSeconds") + " 秒\n成功得同名牌"
                };
                title = eventNames[run.eventKind];
                left = "稳一手\n" + safe[run.eventKind];
                right = "赌一把\n" + risk[run.eventKind];
            }
            else if (run.phase == EndlessPhase.Circuit)
            {
                int next = run.circuit % BreakthroughNames.Length;
                title = "巡回完成！\n继续将获得：" + BreakthroughNames[next] + "\n敌人新增：" + AffixNames[next];
                left = "继续挑战";
                right = "结算带走";
            }
            else if (run.phase == EndlessPhase.Paused)
            {
                title = "已暂停\n" + BuildSummary(run);
                left = "继续战斗";
                middle = "保存并退出";
                right = "结束本局";
            }
            else
            {
                title = "本局结束\n战绩 " + run.score + "     击杀 " + (run.kills + run.invaderKills) + "     领主 " + run.bossKills;
                middle = "返回大厅";
            }
            Set(_modalTitle, title);
            SetChoice(0, left);
            SetChoice(1, middle);
            SetChoice(2, right);
            Show(_reroll, run.phase == EndlessPhase.Upgrade && run.rerolls > 0);
        }
        private void SetChoice(int index, string text)
        {
            Show(_choices[index], text != null);
            if (text != null)
            {
                Set(_choiceTexts[index], text);
            }
        }
        /// <summary>把已有的祝福拼成一行，放在暂停页。战斗时不占屏幕，想看的时候暂停看。</summary>
        private string BuildSummary(EndlessRun run)
        {
            _text.Clear();
            for (int i = 0; i < 6; i++)
            {
                if (run.mains[i] > 0)
                {
                    _text.Append(_text.Length > 0 ? " · " : "");
                    _text.Append(run.evolved[i] ? EndlessConfig.Route(i).Evolution : EndlessConfig.Route(i).Blessing + " Lv" + run.mains[i]);
                }
            }
            return _text.Length > 0 ? _text.ToString() : "还没有祝福";
        }
        private void Choice(int index)
        {
            EndlessPhase phase = Service.Run.phase;
            if (phase == EndlessPhase.Upgrade)
            {
                Service.Choose(index);
            }
            else if (phase == EndlessPhase.Event)
            {
                Service.SelectEvent(index == 2);
            }
            else if (phase == EndlessPhase.Circuit)
            {
                if (index == 2)
                {
                    Service.Finish();
                }
                else
                {
                    // 突破和侵袭按巡回数自动轮换，玩家只决定“继续还是带走”。
                    int next = Service.Run.circuit % BreakthroughNames.Length;
                    Service.ContinueCircuit(next, next);
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
        private void Card(int index)
        {
            Service.SelectCard(index);
            RefreshView();
        }
        [RevButtonClick("start")] private void StartWave()
        {
            Service.StartWave();
            RefreshView();
        }
        [RevButtonClick("pause")] private void Pause()
        {
            Service.Pause();
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
        [RevButtonClick("bottom/card0")] private void Card0()
        {
            Card(0);
        }
        [RevButtonClick("bottom/card1")] private void Card1()
        {
            Card(1);
        }
        [RevButtonClick("bottom/card2")] private void Card2()
        {
            Card(2);
        }
        [RevButtonClick("bottom/card3")] private void Card3()
        {
            Card(3);
        }
        [RevButtonClick("bottom/card4")] private void Card4()
        {
            Card(4);
        }
        [RevButtonClick("bottom/card5")] private void Card5()
        {
            Card(5);
        }
        protected override void OnClose()
        {
            _opening = false;
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
