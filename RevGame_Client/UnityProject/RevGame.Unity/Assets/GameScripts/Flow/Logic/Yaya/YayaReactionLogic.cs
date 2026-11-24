// 场景互动只观察战斗，不改变数值或随机源。两个域复用评价算法，时间只累计实际战斗秒数。
using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Endless;
using RevGame.Services;
using RevGame.View;
using Revolution;
namespace RevGame.Flow
{
    /// <summary>六章域内的鸦鸦评价观察者，随Battle域进出自动停止，不能留监听到下一局。</summary>
    [RevLogic(GameDomain.Battle)]
    public sealed class CampaignYayaReactionLogic : YayaReactionLogic
    {
        /// <summary>六章用于观察鼠标操作的动作名，只在远征域存活。</summary>
        private const string ActivityAction = "RevGame.CampaignYaya.Activity";

        protected override int Mode => 1;

        protected override void OnStart()
        {
            base.OnStart();
            // 正式包也通过绑定启动输入采集宿主。单纯读取快照不能保证从编辑态或无域重载进入时已经启动。
            if (!RevInput.Bind(ActivityAction, RevMouseButton.Left))
            {
                throw new InvalidOperationException("无法绑定六章战斗操作检测");
            }
        }

        /// <summary>每帧读框架输入快照，避免四分之一秒的评价采样漏掉短触摸。触摸、拖动和鼠标按住都表示玩家正在操作。</summary>
        protected override void OnTick(float deltaTime)
        {
            if (RevInput.PointerCount > 0 || RevInput.Held(ActivityAction))
            {
                TryRecordCampaignActivity();
            }
        }

        protected override void OnStop()
        {
            RevInput.RemoveAction(ActivityAction);
            base.OnStop();
        }
    }
    /// <summary>无尽域内的互动观察者。读档进入场景后，以存档战斗时刻为新一轮观察起点。</summary>
    [RevLogic(GameDomain.Endless)]
    public sealed class EndlessYayaReactionLogic : YayaReactionLogic
    {
        protected override int Mode => 2;
    }
    /// <summary>事实累计、评价与台词抽选所有者。只在固定采样读取玩家总生命，不逐帧扫棋盘。</summary>
    public abstract class YayaReactionLogic : RevLogicBase
    {
        /// <summary>由域具体类确定：1六章、2无尽，不在共享静态字段保存当前模式。</summary>
        protected abstract int Mode
        {
            get;
        }
        /// <summary>本局唯一场景呈现器，只通知播放/隐藏；资源和Tween由呈现器拥有。</summary>
        private YayaReactionPresenter _presenter;
        /// <summary>表现观察时间/窗口与冷却，单位实际战斗秒；暂停和阅读剧情不消耗。</summary>
        private float _clock, _window, _nextGlobal, _initialHealth;
        /// <summary>各档下次允许的观察时间，长度与台词表的档位约定一致。</summary>
        private readonly float[] _nextBand = new float[YayaReactionConfig.IdleTauntBand];
        /// <summary>最近一次玩家操作的战斗时刻（秒）。远征换轮归零，无尽读档从本次进场时刻开始。</summary>
        private float _lastActivity;
        /// <summary>两种玩法共用的无操作阈值（战斗秒），每次进入互动域从原表读取。</summary>
        private float _idleTauntAfter;
        /// <summary>当前窗口事实，long避免密集伤害累加溢出；敌伤只用来判断窗口有实际交战。</summary>
        private long _taken, _dealt;
        /// <summary>窗口击杀/真实死亡、采样累计与六章回合标识，换回合必须清窗口但保留本局控频。</summary>
        private int _kills, _losses, _samples, _lastRound = -1;
        /// <summary>尚未开始观察时，第一条战斗事实先记录起始生命，不能使用空窗口的零生命作分母。</summary>
        private bool _collecting;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>仅隔离夹具暂停本观察者的自然采样，不暂停实际战斗或任何其它逻辑。</summary>
        private bool _preview;
        /// <summary>本场进入域后接受的无操作台词次数，和通用战绩互动分开，便于验证确实重复触发。</summary>
        private int _idleTaunts;
#endif
        /// <summary>互动随机源独立于战斗，不消费服务随机数，不会因开关台词影响掉落或AI。</summary>
        private uint _random = 3187781;
        /// <summary>每档上次台词编号，抽取时排除连续重复。</summary>
        private readonly int[] _lastLine = new int[YayaReactionConfig.IdleTauntBand];
        /// <summary>最近一次通过正式控频的评价，1 好、2 一般、3 不佳、4 无操作，0 尚未评价。</summary>
        public int LastBand
        {
            get; private set;
        }
        /// <summary>本局接受的互动次数，实际显示次数由Presenter另行报告。</summary>
        public int TriggerCount
        {
            get; private set;
        }
        /// <summary>真实战斗结算事件累计数，与测试窗口输入分开，验证自然观察接线是否生效。</summary>
        public int FactsObserved
        {
            get; private set;
        }
        /// <summary>在本域装配表现、操作监听与固定采样。框架在离场时按所有者移除监听和计时器。</summary>
        protected override void OnStart()
        {
            YayaReactionConfig.Validate();
            _presenter = UnityEngine.Object.FindObjectOfType<YayaReactionPresenter>();
            if (_presenter == null)
            {
                throw new InvalidOperationException("当前战斗场景未装配鸦鸦互动节点");
            }
            _presenter.BeginSession(Mode);
            _lastRound = Mode == 1 ? RevServices.GetRequired<IBattleService>().Round : -1;
            _idleTauntAfter = YayaReactionConfig.Value(YayaReactionConfig.IdleTauntSecondsKey);
            if (Mode == 1)
            {
                Listen<int>(Events.PhaseEnterBattle, _ => _lastActivity = 0f);
            }
            else
            {
                // 无尽的战斗时钟可能从存档中途开始。只接收当前棋局已经提交的玩家操作。
                var endless = RevServices.GetRequired<EndlessService>();
                _lastActivity = endless.Run.seconds;
                Listen<EndlessService>(Events.EndlessPlayerBoardAction, source =>
                {
                    if (ReferenceEquals(source, endless))
                    {
                        RecordActivity(endless.Run.seconds);
                    }
                });
            }
            Listen<YayaCombatFact>(Events.YayaCombatObserved, OnFact);
            Every(YayaReactionConfig.Value("SampleSeconds"), Sample);
        }

        /// <summary>远征触屏或鼠标操作通过战斗阶段门禁后刷新计时，返回本次输入是否被记录。</summary>
        internal bool TryRecordCampaignActivity()
        {
            if (Mode != 1 || !CanObserve())
            {
                return false;
            }
            RecordActivity(RevServices.GetRequired<IBattleService>().BattleElapsed);
            return true;
        }

        /// <summary>记录最近一次操作的战斗时刻。收回当前闲置嘲讽及其在途加载，保留已消耗的控频时间。</summary>
        private void RecordActivity(float elapsed)
        {
            _lastActivity = elapsed;
            if (LastBand == YayaReactionConfig.IdleTauntBand && _presenter.Busy)
            {
                _presenter.Hide();
            }
        }

        /// <summary>仅在可观察战斗中调用，按当前玩法的战斗时钟计算闲置秒数。暂停和过场由服务冻结时钟。</summary>
        private float IdleSeconds()
        {
            float elapsed = Mode == 1
                ? RevServices.GetRequired<IBattleService>().BattleElapsed
                : RevServices.GetRequired<EndlessService>().Run.seconds;
            return Math.Max(0f, elapsed - _lastActivity);
        }
        /// <summary>事件已在伤害/死亡提交后产生；只有真实交战且无剧情/暂停时才进入窗口。</summary>
        private void OnFact(YayaCombatFact fact)
        {
            if (fact.Mode != Mode || !CanObserve())
            {
                return;
            }
            if (!_collecting)
            {
                ResetWindow();
                _collecting = true;
            }
            FactsObserved++;
            if (fact.TargetSide == GameSide.Player)
            {
                _taken += fact.Damage;
            }
            else
            {
                _dealt += fact.Damage;
            }
            _kills += fact.Kills;
            _losses += fact.Losses;
        }
        /// <summary>只在玩法已就绪且实际交战时返回允许观察。准备、暂停、教学和剧情期间隐藏互动。</summary>
        public bool CanObserve()
        {
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            if (flow.IsGameplayBlocked || flow.IsTutorialActive)
            {
                return false;
            }
            return Mode == 1
                ? RevServices.GetRequired<IBattleService>().Phase == GamePhase.Battle
                : RevServices.GetRequired<EndlessService>().Run?.phase == EndlessPhase.Fighting;
        }
        /// <summary>固定采样只累计可观察的战斗时间。达到闲置阈值时优先尝试嘲讽，远征换轮清理上一轮窗口。</summary>
        private void Sample()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (_preview)
            {
                return;
            }
#endif
            if (Mode == 1)
            {
                int round = RevServices.GetRequired<IBattleService>().Round;
                if (round != _lastRound)
                {
                    _lastRound = round;
                    ResetWindow();
                    _collecting = false;
                }
            }
            if (!CanObserve())
            {
                _presenter.Hide();
                return;
            }
            if (!_collecting)
            {
                ResetWindow();
                _collecting = true;
            }
            float step = YayaReactionConfig.Value("SampleSeconds");
            _clock += step;
            _window += step;
            _samples++;
            if (IdleSeconds() >= _idleTauntAfter)
            {
                // 无操作优先使用专用台词，沿用全局和同档冷却。继续滚动事实窗口供下一次表现评价。
                TryPublish(YayaReactionConfig.IdleTauntBand);
                if (_window >= YayaReactionConfig.Value("WindowSeconds"))
                {
                    ResetWindow();
                }
                return;
            }
            if (_window < YayaReactionConfig.Value("WindowSeconds"))
            {
                return;
            }
            int band = Classify(Mode, _kills, _losses, _taken, _dealt, _initialHealth);
            // 已确认处于战斗阶段。空窗口只使用普通鼓励，不凭空捏造击杀/损失，也不延用上一窗的夸奖或嘲讽。
            // 这样短暂无结算事件时仍能按Excel节奏互动；准备、剧情和暂停已经在上面的门禁提前返回。
            if (band == 0)
            {
                band = 2;
            }
            TryPublish(band);
            ResetWindow();
        }
        /// <summary>损失优先于击杀，避免“死了很多队友却因击杀多被夸”；无交战窗口返回0不弹出。</summary>
        public static int Classify(int mode, int kills, int losses, long taken, long dealt, float health)
        {
            float threshold = YayaReactionConfig.Value(mode == 1 ? "CampaignGoodKills" : "EndlessGoodKills");
            return ClassifyWithRules(kills, losses, taken, dealt, health, threshold, YayaReactionConfig.Value("GoodTakenRatio"), YayaReactionConfig.Value("PoorTakenRatio"), YayaReactionConfig.Value("PoorLosses"));
        }
        /// <summary>纯分类算法，明确参数单位和阈值，编辑器用同一Excel输入测试，无需初始化运行时表管理器。</summary>
        public static int ClassifyWithRules(int kills, int losses, long taken, long dealt, float health, float goodKills, float goodTakenRatio, float poorTakenRatio, float poorLosses)
        {
            if (kills == 0 && losses == 0 && taken == 0 && dealt == 0)
            {
                return 0;
            }
            double ratio = taken / Math.Max(1d, health);
            if (losses >= poorLosses || ratio >= poorTakenRatio)
            {
                return 3;
            }
            if (kills >= goodKills && ratio <= goodTakenRatio)
            {
                return 1;
            }
            return 2;
        }
        /// <summary>全局冷却、同档冷却、已在展示共同控频；台词抽选在独立随机源中按权重避开连续重复。</summary>
        private bool TryPublish(int band)
        {
            if (band == 0 || _clock < YayaReactionConfig.Value("InitialDelay") || _clock < _nextGlobal || _clock < _nextBand[band - 1] || _presenter.Busy)
            {
                return false;
            }
            YayaReactionLine line = ChooseLine(band);
            _presenter.Show(line);
            _nextGlobal = _clock + YayaReactionConfig.Value("GlobalCooldown");
            _nextBand[band - 1] = _clock + YayaReactionConfig.Value("SameBandCooldown");
            _lastLine[band - 1] = line.Id;
            LastBand = band;
            TriggerCount++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (band == YayaReactionConfig.IdleTauntBand)
            {
                _idleTaunts++;
            }
#endif
            return true;
        }
        /// <summary>在适用当前玩法的台词中按权重抽选。排除上一条，有且只有一条可用时允许复用。</summary>
        private YayaReactionLine ChooseLine(int band)
        {
            int total = 0;
            foreach (YayaReactionLine line in RevDataTableManager.Get<YayaReactionLineTable>().All)
            {
                if (line.Band == band && YayaReactionConfig.MatchesMode(line, Mode) && line.Id != _lastLine[band - 1])
                {
                    total += line.Weight;
                }
            }
            if (total == 0)
            {
                foreach (YayaReactionLine line in RevDataTableManager.Get<YayaReactionLineTable>().All)
                {
                    if (line.Band == band && YayaReactionConfig.MatchesMode(line, Mode))
                    {
                        return line;
                    }
                }
            }
            _random ^= _random << 13;
            _random ^= _random >> 17;
            _random ^= _random << 5;
            int pick = (int)(_random % (uint)total);
            foreach (YayaReactionLine line in RevDataTableManager.Get<YayaReactionLineTable>().All)
            {
                if (line.Band != band || !YayaReactionConfig.MatchesMode(line, Mode) || line.Id == _lastLine[band - 1])
                {
                    continue;
                }
                pick -= line.Weight;
                if (pick < 0)
                {
                    return line;
                }
            }
            throw new InvalidOperationException("鸦鸦互动台词权重抽选失败");
        }
        /// <summary>窗口开始记录真实己方总生命作为伤害比例分母，无尽额外包含棋心；复活/治疗不伪造负伤害。</summary>
        private void ResetWindow()
        {
            _window = 0;
            _taken = _dealt = 0;
            _kills = _losses = 0;
            _initialHealth = 0;
            if (Mode == 1)
            {
                foreach (var piece in RevServices.GetRequired<IBattleService>().PlayerPieces)
                {
                    _initialHealth += piece.Hp;
                }
            }
            else
            {
                EndlessService service = RevServices.GetRequired<EndlessService>();
                _initialHealth = service.Run.heart;
                foreach (var actor in service.Units)
                {
                    if (actor.Piece.Side == GameSide.Player)
                    {
                        _initialHealth += actor.Piece.Hp;
                    }
                }
            }
        }
        /// <summary>框架先取消事件/计时器，再关闭本域呈现器；不清其它角色Tween或全局资源组。</summary>
        protected override void OnStop()
        {
            _presenter?.EndSession();
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>查询本域两类互动的计时与次数。保留旧六章查询键供现有回归计划读取。</summary>
        public object Snapshot()
        {
            bool campaign = Mode == 1;
            float idleSeconds = CanObserve() ? IdleSeconds() : 0f;
            bool idle = idleSeconds >= _idleTauntAfter;
            return new Dictionary<string, object>
            {
                ["mode"] = Mode, ["clock"] = _clock, ["window"] = _window, ["kills"] = _kills, ["losses"] = _losses, ["taken"] = _taken,
                ["lastBand"] = LastBand, ["triggers"] = TriggerCount, ["samples"] = _samples, ["factsObserved"] = FactsObserved,
                ["idleSeconds"] = idleSeconds, ["idle"] = idle, ["idleTauntAfter"] = _idleTauntAfter, ["idleTaunts"] = _idleTaunts,
                ["campaignIdleSeconds"] = campaign ? idleSeconds : 0f,
                ["campaignIdle"] = campaign && idle,
                ["campaignIdleTauntAfter"] = campaign ? _idleTauntAfter : 0f,
                ["campaignIdleTaunts"] = campaign ? _idleTaunts : 0
            };
        }
        /// <summary>隔离测试给出固定已结算事实窗口，再经相同分类/控频入口；不修改战斗服务或随机源。</summary>
        public bool EvaluateForTest(int kills, int losses, long taken, long dealt, float health, float advance)
        {
            bool isolated = Mode == 1 ? RevServices.GetRequired<StoryProgress>().IsIsolated : RevServices.GetRequired<EndlessService>().Isolated;
            if (!isolated)
            {
                throw new InvalidOperationException("互动夹具需要隔离测试且无过场剧情");
            }
            if (!CanObserve())
            {
                return false;
            }
            _clock += advance;
            _preview = true;
            return TryPublish(Classify(Mode, kills, losses, taken, dealt, health));
        }
        /// <summary>恢复自然观察与临时显示，自动化finally使用，不能把预览结果写回战斗状态。</summary>
        public void CleanupPreview()
        {
            _preview = false;
            _presenter.Hide();
            ResetWindow();
        }
#endif
    }
}
