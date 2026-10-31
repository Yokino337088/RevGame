using RevGame.Core;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>本棋盘独占有界公平队列；保留最多81条、每来源3段水流，过期0.7秒丢弃表现，退出清空历史。</summary>
        private readonly FairFeedbackBuffer<PiecePatternInfo> _patternQueue = new FairFeedbackBuffer<PiecePatternInfo>(81, 3, .7f);
        /// <summary>与既有16份短效预算一致的固定槽位。只登记范围效果，回收立即摘除，不分配每帧快照列表。</summary>
        private readonly BattleEffectView[] _patternSlots = new BattleEffectView[MaxMeleeEffects];
        /// <summary>轮询游标与每帧推进上限；4是表现调度预算，不是玩法数值，满16份时最多隔4帧轮到一次。</summary>
        private int _patternCursor;
        public const int PatternAdvanceBudget = 4;
        /// <summary>冷加载同帧返回时先停放关闭Renderer的池对象，真正显示仍受每帧3份限制；预约计数继续占位。</summary>
        private struct ReadyPattern
        {
            public BattleEffectView Effect;
            /// <summary>世界坐标与棋种/格子半径的值快照，不持有攻击者或目标棋子。</summary>
            public Vector3 Source, Center;
            public PieceKind Kind;
            public int Radius;
        }
        private readonly ReadyPattern[] _readyPatterns = new ReadyPattern[MaxMeleeEffects];
        private int _readyPatternHead, _readyPatternCount;
        /// <summary>真实参数块写入数和累计推进次数，供自动化检查上限；新启动的首帧由启动预算另计。</summary>
        public int PatternWritesLastFrame
        {
            get; private set;
        }
        public int PatternWritesPeak
        {
            get; private set;
        }
        public long PatternAdvanceTotal
        {
            get; private set;
        }
        /// <summary>实际启播数包括冷回调交付后的显示，区别于仅统计发出加载请求的数量。</summary>
        public int PatternStartsLastFrame
        {
            get; private set;
        }
        public int PatternStartsPeak
        {
            get; private set;
        }
        /// <summary>开发验收读取实际启用的范围Renderer数量；仅在查询时遍历有上限的短效集合。</summary>
        public int ActivePatternCount
        {
            get
            {
                int count = 0;
                foreach (BattleEffectView effect in _meleeEffects)
                {
                    if (effect.IsPatternActive)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        /// <summary>
        /// 攻击形状占用既有短效预算与池租约，异步代次检查、退出回池复用ShowEffect。
        /// 超预算只省略本次表现，不能反向阻止模型命中；数字仍由真实伤害事件独立调度。
        /// </summary>
        private void OnPiecePattern(PiecePatternInfo info)
        {
            if (_released)
            {
                return;
            }
            _patternQueue.Offer(info.SourceId, info, Time.time);
        }

        /// <summary>在已有LateUpdate内分帧交付，每帧至多3份；名额不足先保留候选，不能把名单后段永久饿死。</summary>
        private void FlushPiecePatterns()
        {
            float now = Time.time;
            PatternWritesLastFrame = 0;
            PatternStartsLastFrame = 0;
            // 先推进已经播放的对象再处理新启动，避免新效果在同一画面帧重复写第一帧。
            int advanced = 0;
            for (int scanned = 0; scanned < _patternSlots.Length && advanced < PatternAdvanceBudget; scanned++)
            {
                BattleEffectView effect = _patternSlots[_patternCursor];
                _patternCursor = (_patternCursor + 1) % _patternSlots.Length;
                if (effect == null || !effect.IsPatternActive)
                {
                    continue;
                }
                advanced++;
                PatternAdvanceTotal++;
                // AdvancePattern可能同步完成并回池；完成回调会清此槽位，调用后不再读取该对象。
                if (effect.AdvancePattern(now))
                {
                    PatternWritesLastFrame++;
                }
            }
            PatternWritesPeak = Mathf.Max(PatternWritesPeak, PatternWritesLastFrame);
            FlushReadyPatterns();
            for (int count = 0; count < 3 && PatternStartsLastFrame < 3; count++)
            {
                if (_meleeEffects.Count + _pendingMeleeLoads >= MaxMeleeEffects
                    || !_patternQueue.TryPeek(now, 0f, out PiecePatternInfo info, out int sourceId))
                {
                    return;
                }
                Vector3 source = info.Source.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                Vector3 center = info.Center.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
                _pendingMeleeLoads++;
                _patternQueue.MarkDisplayed(sourceId, now);
                ShowEffect(BattleEffectKind.Pattern, source, info.Radius, info.Side, center, info.Kind);
                // 热取会同步交付到停放队列，本帧即可显示；冷取迟到后由下一次LateUpdate按同一预算消费。
                FlushReadyPatterns();
            }
        }

        /// <summary>统一停放热/冷交付对象。_effects已经持有它，退出会归池；此处不提前扣掉预约名额。</summary>
        private void QueueReadyPattern(BattleEffectView effect, Vector3 source, Vector3 center, PieceKind kind, int radius)
        {
            if (_readyPatternCount >= _readyPatterns.Length)
            {
                throw new System.InvalidOperationException("元素特效待启播队列超过短效预约上限");
            }
            int tail = (_readyPatternHead + _readyPatternCount) % _readyPatterns.Length;
            _readyPatterns[tail] = new ReadyPattern { Effect = effect, Source = source, Center = center, Kind = kind, Radius = radius };
            _readyPatternCount++;
        }

        /// <summary>真正启用Renderer前才消费预约并登记活动槽；冷加载16份同时返回也只会每帧显示3份。</summary>
        private void FlushReadyPatterns()
        {
            while (_readyPatternCount > 0 && PatternStartsLastFrame < 3)
            {
                ReadyPattern ready = _readyPatterns[_readyPatternHead];
                _readyPatterns[_readyPatternHead] = default;
                _readyPatternHead = (_readyPatternHead + 1) % _readyPatterns.Length;
                _readyPatternCount--;
                _pendingMeleeLoads--;
                _meleeEffects.Add(ready.Effect);
                RegisterPattern(ready.Effect);
                PatternStartsLastFrame++;
                PatternStartsPeak = Mathf.Max(PatternStartsPeak, PatternStartsLastFrame);
                ready.Effect.ShowPattern(ready.Source, ready.Center, ready.Kind, ready.Radius, _effectFinishedCallback);
            }
        }

        /// <summary>池交付后登记空槽。总短效预算已预检，因此没有空槽代表接线错误，不能静默漏播。</summary>
        private void RegisterPattern(BattleEffectView effect)
        {
            for (int i = 0; i < _patternSlots.Length; i++)
            {
                if (_patternSlots[i] == null)
                {
                    _patternSlots[i] = effect;
                    return;
                }
            }
            throw new System.InvalidOperationException("范围特效槽位已满，活动数量与预约预算不一致");
        }

        /// <summary>先断开棋盘持有的租约引用，后续对象即使马上被池复用，也不会由旧槽位再次推进。</summary>
        private void UnregisterPattern(BattleEffectView effect)
        {
            for (int i = 0; i < _patternSlots.Length; i++)
            {
                if (_patternSlots[i] == effect)
                {
                    _patternSlots[i] = null;
                    return;
                }
            }
        }

        private void ClearPatternSchedule()
        {
            System.Array.Clear(_patternSlots, 0, _patternSlots.Length);
            System.Array.Clear(_readyPatterns, 0, _readyPatterns.Length);
            _readyPatternHead = _readyPatternCount = 0;
            _patternCursor = 0;
            PatternWritesLastFrame = PatternWritesPeak = 0;
            PatternStartsLastFrame = PatternStartsPeak = 0;
            PatternAdvanceTotal = 0;
        }

        /// <summary>只读图形验收：读取当前棋种已提交的真实Shader关键帧，不创建数组，也不改变调度游标。</summary>
        public bool ReadPatternFrame(PieceKind kind, out int frame, out bool elemental)
        {
            frame = -1;
            elemental = false;
            foreach (BattleEffectView effect in _patternSlots)
            {
                if (effect != null && effect.IsPatternActive && effect.PatternKind == kind)
                {
                    frame = Mathf.Max(frame, effect.PatternKeyframe);
                    elemental |= effect.UsesElementalShader;
                }
            }
            return frame >= 0;
        }
    }
}
