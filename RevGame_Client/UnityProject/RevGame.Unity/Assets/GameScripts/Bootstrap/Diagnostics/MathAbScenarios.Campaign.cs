#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using RevGame.Automation;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Bootstrap
{
    internal static partial class MathAbScenarios
    {
        /// <summary>使用第三章已有的固定阵容，种子由 LoadPerformanceScenario 按章节重置。</summary>
        private const int CampaignChapter = 3;
        /// <summary>满盘重弩对射，三十六枚我方活棋达到待测火力公式的阈值。</summary>
        private const int CampaignPieces = 81;
        /// <summary>开战后先等八个规则步，取首条伤害数字，同时让画面进入稳定交火。</summary>
        private const int CampaignWarmSteps = 8;
        /// <summary>稳态整帧窗口包含连续三十二个规则步，当前配置为十个游戏秒。</summary>
        private const int CampaignMeasureSteps = 32;
        /// <summary>现成普通交火的观察窗口，单位为墙钟秒。</summary>
        private const int CampaignCurrentSeconds = 6;

        /// <summary>进入真实远征场景，从开战按钮采集引擎反馈，并在固定逻辑终点记录真实战斗结果。</summary>
        internal static IEnumerator Campaign(MathAbRun run)
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || !battle.IsScorePreviewActive)
            {
                throw new InvalidOperationException("远征对照必须同时隔离剧情及积分");
            }

            if (flow.Screen == GameScreen.Home)
            {
                IEnumerator home = CampaignWait(flow, () => !flow.IsGameplayBlocked && !flow.IsTransitioning, "首页未就绪");
                while (home.MoveNext())
                {
                    yield return home.Current;
                }
                battle.UnlockAllChaptersForDevelopment();
                if (!flow.OpenLevels() || !flow.EnterBattle(CampaignChapter))
                {
                    throw new InvalidOperationException("无法从正式入口进入第三章：" + flow.LastError);
                }
            }

            IEnumerator scene = CampaignWait(flow, () => flow.Screen == GameScreen.Battle && !flow.IsGameplayBlocked &&
                !flow.IsTransitioning && battle.Chapter == CampaignChapter && battle.Phase == GamePhase.Prepare, "第三章准备场景未就绪");
            while (scene.MoveNext())
            {
                yield return scene.Current;
            }
            if (!battle.LoadPerformanceScenario(CampaignChapter, CampaignPieces))
            {
                throw new InvalidOperationException("满盘夹具未装配：" + battle.LastError);
            }

            var board = UnityEngine.Object.FindObjectOfType<BoardView>();
            if (board == null)
            {
                throw new InvalidOperationException("远征棋盘视图不存在");
            }
            IEnumerator ready = CampaignWait(flow, () => !flow.IsGameplayBlocked && battle.PreparationGranted &&
                board.IsReady && board.PieceCount == CampaignPieces && RevUI.IsOpen<PreparePanel>(), "固定阵容、准备增益或开战按钮未就绪");
            while (ready.MoveNext())
            {
                yield return ready.Current;
            }

            var pointer = new GamePointerInput();
            var button = pointer.FindButton("PreparePanel/btnReady");
            var prepare = RevUI.Get<PreparePanel>();
            // 先取生产视图持有的活动文字集合。测量期间不遍历场景，也不添加第二套反馈。
            var textField = typeof(BoardView).GetField("_textEffects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var texts = textField?.GetValue(board) as HashSet<BattleEffectView>;
            if (button == null || prepare == null || texts == null || battle.PlayerPieces.Count != 36 || battle.EnemyPieces.Count != 45)
            {
                throw new InvalidOperationException("固定阵容或原表现控件未就绪");
            }

            // 每场棋子的编号起点不同，保留局内相对编号来比较同一个棋子。
            int firstId = battle.PlayerPieces[0].Id - 1;
            int attacks = 0, hits = 0, uniqueCount = 0, ranged = 0, deaths = 0;
            long damage = 0, started = 0, firstHitStarted = 0;
            int firstHitModelStep = 0, firstHitSource = 0, firstHitTarget = 0;
            float firstHitModelSeconds = 0f;
            ulong trace = 14695981039346656037UL;
            var attackers = new HashSet<int>(CampaignPieces);
            object owner = new object();
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, attack =>
            {
                attacks++;
                if (attackers.Add(attack.PieceId))
                {
                    uniqueCount++;
                }
                CampaignMix(ref trace, 1, attack.PieceId - firstId, (int)attack.Kind);
                CampaignMix(ref trace, attack.Position.X, attack.Position.Y, (int)attack.Side);
            }, owner: owner);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit =>
            {
                if (hits == 0)
                {
                    firstHitStarted = Stopwatch.GetTimestamp();
                    // 规则回调在本次固定步计数累加之前发出。模型时间已经进入这一拍。
                    firstHitModelStep = BattleLogic.MeasuredTickCount + 1;
                    firstHitModelSeconds = battle.BattleElapsed;
                    firstHitSource = hit.SourceId == 0 ? 0 : hit.SourceId - firstId;
                    firstHitTarget = hit.TargetId - firstId;
                    run.RecordFeedback("开战松手到首笔规则扣血", started, firstHitStarted);
                }
                hits++;
                damage += hit.Damage;
                if (hit.SourceIsRanged && !hit.FromSkill)
                {
                    ranged++;
                }
                CampaignMix(ref trace, 2, hit.TargetId - firstId, hit.SourceId == 0 ? 0 : hit.SourceId - firstId);
                CampaignMix(ref trace, hit.Damage, (int)hit.DamageKind, (int)hit.TargetSide);
                CampaignMix(ref trace, hit.Position.X, hit.Position.Y, hit.FromSkill ? 1 : 0);
            }, owner: owner);
            RevEvent.AddEventListener<int>(Events.PieceDied, id =>
            {
                deaths++;
                CampaignMix(ref trace, 3, id - firstId, 0);
            }, owner: owner);

            bool framesStarted = false;
            try
            {
                // 对齐开战到下一次定时回调的相位，避免把准备倒计时的剩余时间算成差异。
                int preparedTick = BattleLogic.MeasuredTickCount;
                IEnumerator nextTick = CampaignWait(flow, () => BattleLogic.MeasuredTickCount > preparedTick, "准备阶段无规则回调");
                while (nextTick.MoveNext())
                {
                    yield return nextTick.Current;
                }
                if (!pointer.PressButton(button))
                {
                    throw new InvalidOperationException("开战按钮按下失败：" + pointer.LastError);
                }
                yield return null;
                BattleLogic.ResetTickMeasurements();
                started = Stopwatch.GetTimestamp();
                if (!pointer.Release(click: true) || battle.Phase != GamePhase.Battle)
                {
                    throw new InvalidOperationException("开战按钮未进入正式战斗：" + battle.LastError);
                }

                bool prepareClosed = false, firstDamageVisible = false;
                var endOfFrame = new WaitForEndOfFrame();
                long deadline = started + Stopwatch.Frequency * 45;
                int lastStep = CampaignWarmSteps + CampaignMeasureSteps;
                while (true)
                {
                    yield return endOfFrame;
                    int step = BattleLogic.MeasuredTickCount;
                    if (Stopwatch.GetTimestamp() >= deadline || battle.Phase != GamePhase.Battle || step > lastStep)
                    {
                        throw new InvalidOperationException("固定步远征未到达正确终点，当前步 " + step + " 阶段 " + battle.Phase);
                    }

                    if (!prepareClosed && (prepare == null || !prepare.gameObject.activeInHierarchy))
                    {
                        prepareClosed = true;
                        run.RecordFeedback("开战松手到准备面板撤下", started);
                    }
                    if (!firstDamageVisible && firstHitStarted != 0 && CampaignHasDamageText(texts))
                    {
                        firstDamageVisible = true;
                        run.RecordFeedback("开战松手到首次实际伤害字进入渲染帧", started);
                        run.RecordFeedback("首笔规则扣血到首次实际伤害字进入渲染帧", firstHitStarted);
                    }

                    if (!framesStarted && step >= CampaignWarmSteps)
                    {
                        if (step != CampaignWarmSteps || !prepareClosed || !firstDamageVisible)
                        {
                            throw new InvalidOperationException("稳态窗口开始前未观测到完整的开战反馈");
                        }
                        run.BeginFrames();
                        framesStarted = true;
                    }
                    if (step == lastStep)
                    {
                        run.EndFrames();
                        framesStarted = false;
                        break;
                    }
                }

                // 到达精确逻辑步后才汇总。排序和构造摘要排除在计时窗外。
                if (attacks == 0 || hits == 0 || ranged == 0 || damage <= 0 || deaths != 0 ||
                    battle.PlayerPieces.Count + battle.EnemyPieces.Count != CampaignPieces)
                {
                    throw new InvalidOperationException("固定满盘采样负载不成立");
                }
                var pieces = new List<Piece>(CampaignPieces);
                pieces.AddRange(battle.PlayerPieces);
                pieces.AddRange(battle.EnemyPieces);
                pieces.Sort((left, right) => left.Id.CompareTo(right.Id));
                run.RecordRule(FormattableString.Invariant($"远征|seed={1025 + CampaignChapter}|步={lastStep}|模拟秒={battle.BattleElapsed:F6}|双方={battle.PlayerPieces.Count}/{battle.EnemyPieces.Count}|出手={attacks}|出手棋={uniqueCount}|命中={hits}|远程命中={ranged}|扣血={damage}|击杀={deaths}|回合分={battle.RoundScore}|战斗能量={battle.CombatScore}|阶段={battle.Phase}|胜方={battle.LastWinner}|事件指纹={trace:x16}"));
                foreach (Piece piece in pieces)
                {
                    run.RecordRule($"远征棋|{piece.Id - firstId}|{piece.Side}|{piece.Def.Kind}|{piece.Pos.X},{piece.Pos.Y}|{piece.Hp}/{piece.MaxHp}|盾={piece.ShieldCharges}");
                }
                run.RecordRule(FormattableString.Invariant($"远征首击|步={firstHitModelStep}|模拟秒={firstHitModelSeconds:F6}|来源={firstHitSource}|目标={firstHitTarget}"));
            }
            finally
            {
                if (pointer.IsPressed)
                {
                    pointer.Release(click: false);
                }
                if (framesStarted)
                {
                    run.EndFrames();
                }
                RevEvent.RemoveAllByOwner(owner);
            }
        }

        /// <summary>在真实交火上采集分段。保留正式时钟与阵容，若交火中断则报告该窗口无效。</summary>
        internal static IEnumerator CampaignCurrent(MathAbRun run)
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = RevServices.GetRequired<IBattleService>();
            int chapter = battle.Chapter;
            int round = battle.Round;
            if (flow.Screen != GameScreen.Battle || flow.IsGameplayBlocked || flow.IsTransitioning || battle.Phase != GamePhase.Battle)
            {
                throw new InvalidOperationException("当前远征尚未交火，无法采集分段");
            }

            // 先让场景交付后的第一批反馈和探针分配与正式窗口错开。
            yield return new WaitForSecondsRealtime(1f);
            run.RecordRule($"章={chapter} 回合={round} 开始={battle.PlayerPieces.Count}/{battle.EnemyPieces.Count}");
            run.BeginFrames();
            long deadline = Stopwatch.GetTimestamp() + CampaignCurrentSeconds * Stopwatch.Frequency;
            while (true)
            {
                if (flow.Screen != GameScreen.Battle || flow.IsGameplayBlocked || flow.IsTransitioning ||
                    battle.Phase != GamePhase.Battle || chapter != battle.Chapter || round != battle.Round)
                {
                    throw new InvalidOperationException("正式交火在采样结束前已变化");
                }
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    break;
                }

                yield return null;
            }
            run.EndFrames();
            run.RecordRule($"章={chapter} 回合={round} 结束={battle.PlayerPieces.Count}/{battle.EnemyPieces.Count}");
        }

        /// <summary>按真实过场等待业务就绪，普通章节故事使用同一个推进入口，超时保留失败原因。</summary>
        private static IEnumerator CampaignWait(GameFlow flow, Func<bool> ready, string failure)
        {
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 45;
            while (!ready())
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    throw new TimeoutException(failure + "：" + flow.LastError);
                }
                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                yield return null;
            }
        }

        /// <summary>只接受实际激活、带文本与正透明度的伤害字，排除战斗口号和预约池对象。</summary>
        private static bool CampaignHasDamageText(HashSet<BattleEffectView> texts)
        {
            foreach (BattleEffectView effect in texts)
            {
                if (effect != null && effect.isActiveAndEnabled && effect.IsFloatingText && !effect.IsAttackText &&
                    effect.TextColor.a > 0f && !string.IsNullOrEmpty(effect.TextContent))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>把已发生的规则事件按先后压进固定整数指纹，保留顺序和字段差异，不引入逐事件分配。</summary>
        private static void CampaignMix(ref ulong hash, int first, int second, int third)
        {
            unchecked
            {
                hash = (hash ^ (uint)first) * 1099511628211UL;
                hash = (hash ^ (uint)second) * 1099511628211UL;
                hash = (hash ^ (uint)third) * 1099511628211UL;
            }
        }
    }
}
#endif
