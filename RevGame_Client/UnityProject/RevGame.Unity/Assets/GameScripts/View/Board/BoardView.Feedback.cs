// ============================================================
// BoardView.Feedback.cs —— 整场战斗的表现预算与文字避让。
// 这里只筛选“显示哪些反馈”，不拦攻击、不改伤害；忙时合并数字或略过口号，规则仍完整执行。
// partial表示同一个BoardView按职责分文件，不是新增管理器或另一套对象池。
// ============================================================
using System.Collections.Generic;
using Unity.Profiling;
using RevGame.Core;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        /// <summary>包含正在异步取池对象的请求；不是等加载完才计数，避免同帧几十个请求穿透上限。</summary>
        private static readonly ProfilerMarker FeedbackFrameMarker = new ProfilerMarker("RevGame.Feedback.Frame");
        private static readonly ProfilerMarker FeedbackArrangeMarker = new ProfilerMarker("RevGame.Feedback.Arrange");
        public const int MaxFloatingTexts = 20;
        /// <summary>弹道单独计数；数字尚未消失时，也不能无限申请新的子弹特效。</summary>
        public const int MaxProjectiles = 35;
        /// <summary>近战短效独立表现预算，不占远程35发名额；上限是渲染资源约束，不是战斗数值。</summary>
        public const int MaxMeleeEffects = 16;
        private readonly HashSet<BattleEffectView> _meleeEffects = new HashSet<BattleEffectView>();
        private int _pendingMeleeLoads;
        public int ActiveMeleeCount => _meleeEffects.Count;
        private const int MaxAttackTexts = 7;
        private const int MaxPendingAttackTexts = 12;
        private const int MaxPendingDamage = 81;
        private const int MaxPendingProjectiles = 70;
        // 每秒最多200发、每帧最多6发；寿命0.36秒时可达到35条，低帧率仍不无界追赶。
        private const float ProjectileSpawnInterval = 0.005f;
        // 这两个集合只追踪当前正在播放的池对象，动画结束或阶段切换时立即移除。
        private readonly HashSet<BattleEffectView> _textEffects = new HashSet<BattleEffectView>();
        private readonly HashSet<BattleEffectView> _projectileEffects = new HashSet<BattleEffectView>();
        // 目标编号+伤害来源复合键：同类伤害合并，暴击和不同技能各自保留颜色，不无界排队。
        private readonly Dictionary<long, PendingDamageText> _pendingDamage = new Dictionary<long, PendingDamageText>(MaxPendingDamage);
        // 预算不变，改用“最久没获得显示名额者优先”的有界缓冲。
        // 普通FIFO满了只拒绝后来者，会永久偏爱先结算的老棋子；高星老棋子连射时，新落的一星棋子尤其容易完全没有反馈。
        private readonly FairFeedbackBuffer<PendingProjectile> _projectileQueue = new FairFeedbackBuffer<PendingProjectile>(MaxPendingProjectiles, 4, 0.5f);
        // 同一棋子只保留一份最新口号，不累计历史连射；最多十二份候选，仍不增加同屏的七条口号上限。
        private readonly FairFeedbackBuffer<PendingAttackText> _attackQueue = new FairFeedbackBuffer<PendingAttackText>(MaxPendingAttackTexts, 1, 0.5f);
        // 遍历字典时不能直接删除，用复用列表先收集过期编号，遍历结束后再移除。
        private readonly List<long> _expiredDamageIds = new List<long>(MaxPendingDamage);
        // 每帧只扫描一次81条待播记录，稳定选出最老八条；同时间戳仍保持字典原遍历次序。
        private readonly PendingDamageText[] _damageBatch = new PendingDamageText[8];
        // 每个棋盘独占固定长度缓冲。上限20条，因此不需要每帧new List/ToArray，也不共享静态可变数组。
        // Rect是值类型，缓存的是本次动画推进后的快照；下一帧重新采样，缩放/移动不会用到旧矩形。
        private readonly BattleEffectView[] _textSnapshot = new BattleEffectView[MaxFloatingTexts];
        private readonly Rect[] _textRects = new Rect[MaxFloatingTexts];
        private readonly Rect[] _reservationRects = new Rect[MaxFloatingTexts];
        // 已申请但还没加载好的名额也算占用；否则异步加载完成的同一帧会突然挤出大量飘字。
        private int _pendingTextLoads, _pendingProjectileLoads;
        // 下次允许显示的游戏时间；只限制表现生成速度，不影响模型中的攻击冷却或伤害结算。
        private float _nextAttackAt, _nextProjectileAt;
        // 递增显示序号同时决定渲染前后关系；不得用HashSet遍历顺序判断哪条文字在下面。
        private int _textOrder;
        // 以下只读计数供编辑器验收观察，业务层不能通过它们修改战斗结果。
        public int ActiveFloatingTextCount => _textEffects.Count;
        public int PendingDamageTextCount => _pendingDamage.Count;
        public int ActiveProjectileCount => _projectileEffects.Count;
        public int PendingProjectileCount => _projectileQueue.Count;

        /// <summary>只保存一次真实攻击的表现快照；排队与动画都不持有棋子，也不会再次结算伤害。</summary>
        private struct PendingProjectile
        {
            public Vector3 Source, Target;
            public GameSide TargetSide;
            public PieceKind SourceKind;
        }

        private struct PendingAttackText
        {
            public PieceAttackInfo Attack;
        }

        /// <summary>
        /// 只登记真实攻击；同棋最多一条最新快照，最多十二条待播，过半秒舍弃。
        /// 满额时未显示过的棋子可以替换近期频繁显示者，不再把名单后面的新棋子一直挡在队外。
        /// </summary>
        private void QueueAttackText(PieceAttackInfo attack)
        {
            _attackQueue.Offer(attack.PieceId, new PendingAttackText { Attack = attack }, Time.time);
        }

        /// <summary>
        /// 每次选择最久没有显示过、且自身冷却已到的棋子；全局预算不足时留下未过期候选等下一帧。
        /// 不能先出队再试预约，否则满屏时整队会被清空，随后仍只有固定顺序的前几颗棋子抢到反馈。
        /// </summary>
        private void FlushAttackText()
        {
            float now = Time.time;
            if (now < _nextAttackAt)
            {
                return;
            }
            PendingAttackText pending;
            int sourceId;
            if (!_attackQueue.TryPeek(now, 0.65f, out pending, out sourceId))
            {
                return;
            }
            if (!TryReserveAttackText(sourceId))
            {
                return;
            }
            _attackQueue.MarkDisplayed(sourceId, now);
            ShowAttackText(pending.Attack);
        }

        /// <summary>短时间内同一目标受到的真实伤害合计；不持有棋子对象，目标死亡回池后仍可正确播报。</summary>
        private struct PendingDamageText
        {
            // 保存快照值而非Piece引用，避免棋子死亡、视图回池后读到别的棋子的状态。
            public long Key;
            public BattleDamageKind Kind;
            public int TargetId, Damage;
            public GameSide Side;
            public Vector3 Position;
            // CreatedAt控制过期；同帧合并后立即允许显示，不再人为等待0.22秒。
            public float CreatedAt;
        }

        /// <summary>口号最多七条，与数字共用二十条总预算；间隔缩短，但同一棋子不能连续刷屏。</summary>
        private bool TryReserveAttackText(int pieceId)
        {
            float now = Time.time;
            float last;
            if (now < _nextAttackAt || _pendingDamage.Count >= 16)
            {
                return false;
            }
            if (_textEffects.Count + _pendingTextLoads >= MaxFloatingTexts)
            {
                return false;
            }
            int attacks = _pendingTextLoads;
            foreach (BattleEffectView effect in _textEffects)
            {
                if (effect.IsAttackText)
                {
                    attacks++;
                }
            }
            if (attacks >= MaxAttackTexts || (_attackTextAt.TryGetValue(pieceId, out last) && now - last < 0.65f))
            {
                return false;
            }
            _attackTextAt[pieceId] = now;
            _nextAttackAt = now + 0.12f;
            _pendingTextLoads++;
            return true;
        }

        /// <summary>
        /// 保存真实命中的弹道快照；来源编号用于公平轮流显示，同棋最多待播四发，总数不超过七十发。
        /// 来源职业直接取结算快照；环境伤害与近战不会进入此队列。
        /// 队满只影响画面取舍，不回滚真实伤害；手动换位后仍依靠编号识别同一颗棋子，不能拿旧坐标当身份。
        /// </summary>
        private void QueueProjectile(Vector3 source, Vector3 target, GameSide targetSide, int sourceId, PieceKind sourceKind)
        {
            var shot = new PendingProjectile { Source = source, Target = target, TargetSide = targetSide, SourceKind = sourceKind };
            _projectileQueue.Offer(sourceId, shot, Time.time);
        }

        /// <summary>真实齐射分散到相邻画面帧；最多35个活动/预约弹道，半秒前的旧弹道不追着新战况补播。</summary>
        private void FlushProjectiles()
        {
            float now = Time.time;
            // 空闲后的第一帧最多借五个间隔，不能因时钟落后几秒而突然补几十发。
            _nextProjectileAt = Mathf.Max(_nextProjectileAt, now - ProjectileSpawnInterval * 5f);
            int spawned = 0;
            while (_projectileQueue.Count > 0 && spawned < 6 && now >= _nextProjectileAt)
            {
                if (_projectileEffects.Count + _pendingProjectileLoads >= MaxProjectiles)
                {
                    return;
                }
                PendingProjectile shot;
                int sourceId;
                if (!_projectileQueue.TryPeek(now, 0f, out shot, out sourceId))
                {
                    return;
                }
                _pendingProjectileLoads++;
                _projectileQueue.MarkDisplayed(sourceId, now);
                _nextProjectileAt += ProjectileSpawnInterval;
                spawned++;
                ShowEffect(BattleEffectKind.Projectile, shot.Source, 0, shot.TargetSide, shot.Target, shot.SourceKind);
            }
        }

        /// <summary>把已结算伤害放入有界显示队列；队列满时略过显示，绝不能因此撤销或重复扣血。</summary>
        private void QueueDamageText(BattleHitInfo hit, Vector3 position)
        {
            if (hit.Damage <= 0)
            {
                return;
            }
            // 同一目标的普通伤害、暴击、环境和各Boss技能分开合并，避免暴击被黄色普通数覆盖。
            // 用值类型复合键与值类型快照，每次入队不再分配一个PendingDamageText对象。
            long key = ((long)hit.TargetId << 8) | (byte)hit.DamageKind;
            PendingDamageText pending;
            if (!_pendingDamage.TryGetValue(key, out pending))
            {
                // 有界队列：屏幕显示不完时不积压几百条旧数字，也不为每次命中创建一个Timer。
                if (_pendingDamage.Count >= MaxPendingDamage)
                {
                    return;
                }
                pending = new PendingDamageText { Key = key, Kind = hit.DamageKind, TargetId = hit.TargetId, Side = hit.TargetSide, CreatedAt = Time.time };
            }
            pending.Damage = (int)System.Math.Min(int.MaxValue, (long)pending.Damage + hit.Damage);
            pending.Position = position; // 玩家换位后，以最近一次命中位置播报。
            _pendingDamage[key] = pending;
        }

        /// <summary>在本帧动画推进后检查文字位置，让移动和放大的字也能获得遮挡淡化。</summary>
        private void LateUpdate()
        {
            if (_released)
            {
                return;
            }
            using (FeedbackFrameMarker.Auto())
            {
                FlushProjectiles();
                // 每帧最多8条，覆盖常见齐射；满额时优先回收口号，其次替换最旧数字，避免等上一秒动画结束。
                FlushDamageTexts();
                FlushAttackText();
                // 最多二十条文字。把昂贵的Renderer.bounds/WorldToScreenPoint从两重循环里提出来：
                // 原来最多读取约800次矩形，现在只读取20次；两两比较仍保留，避免为省CPU改变遮挡规则。
                int count = 0;
                foreach (BattleEffectView effect in _textEffects)
                {
                    _textSnapshot[count] = effect;
                    _textRects[count] = effect.TextScreenRect;
                    count++;
                }
                for (int i = 0; i < count; i++)
                {
                    BattleEffectView older = _textSnapshot[i];
                    bool covered = false;
                    for (int j = 0; j < count; j++)
                    {
                        if (_textSnapshot[j].TextOrder > older.TextOrder && _textRects[j].Overlaps(_textRects[i]))
                        {
                            covered = true; break;
                        }
                    }
                    older.SetTextOccluded(covered);
                }
                // 计算完就清引用，不让工作数组在回池之后还持有上一回合的视图。
                System.Array.Clear(_textSnapshot, 0, count);
            }
        }

        /// <summary>本帧直接显示已结算数字；超预算只替换表现，不更改战斗数值。</summary>
        private void FlushDamageTexts()
        {
            int count = 0;
            float now = Time.time;
            _expiredDamageIds.Clear();
            foreach (PendingDamageText item in _pendingDamage.Values)
            {
                if (now - item.CreatedAt > 1.2f)
                {
                    _expiredDamageIds.Add(item.Key); continue;
                }
                // 最多八项的插入排序；不分配列表，也不在一帧内反复扫描整个字典八遍。
                int index = count;
                while (index > 0 && item.CreatedAt < _damageBatch[index - 1].CreatedAt) { index--; }
                if (index >= _damageBatch.Length) { continue; }
                int end = System.Math.Min(count, _damageBatch.Length - 1);
                for (int i = end; i > index; i--) { _damageBatch[i] = _damageBatch[i - 1]; }
                _damageBatch[index] = item;
                count = System.Math.Min(count + 1, _damageBatch.Length);
            }
            foreach (long id in _expiredDamageIds) { _pendingDamage.Remove(id); }
            for (int i = 0; i < count; i++)
            {
                if (!FlushOneDamageText(_damageBatch[i]))
                {
                    // 未能预约时保持余下记录，下帧重新按实时队列选择，不能提前删掉未显示的数字。
                    break;
                }
            }
        }

        /// <summary>显示已经选中的一条数字；返回false代表本帧预约已满，记录继续留在待播队列。</summary>
        private bool FlushOneDamageText(PendingDamageText oldest)
        {
            if (_textEffects.Count + _pendingTextLoads >= MaxFloatingTexts)
            {
                BattleEffectView retire = null;
                foreach (BattleEffectView text in _textEffects)
                {
                    if (retire == null || (text.IsAttackText && !retire.IsAttackText) ||
                        (text.IsAttackText == retire.IsAttackText && text.TextOrder < retire.TextOrder))
                    {
                        retire = text;
                    }
                }
                // 先结束遍历，再调用会摘除集合元素的回池回调。
                if (retire != null) { retire.RetireFeedback(); }
            }
            if (_textEffects.Count + _pendingTextLoads >= MaxFloatingTexts)
            {
                return false;
            }
            _pendingDamage.Remove(oldest.Key);
            _pendingTextLoads++;
            int generation = _effectGeneration;
            if (GamePoolRequests.TryGetReady<BattleEffectView>(out BattleEffectView ready, _viewRoot))
            {
                PresentDamageText(ready, oldest, generation);
                return true;
            }
            LoadDamageText(oldest, generation);
            return true;
        }

        private void LoadDamageText(PendingDamageText message, int generation)
        {
            GamePoolRequests.GetAsync<BattleEffectView>(effect => PresentDamageText(effect, message, generation), _viewRoot);
        }
        private void PresentDamageText(BattleEffectView effect, PendingDamageText message, int generation)
        {
            // 过期回调不能减新回合的计数：只有代次仍然一致，才有权释放当前这笔预约。
            if (_released || generation != _effectGeneration)
            {
                if (effect != null)
                {
                    RevPool.Return(effect, 0);
                }
                return;
            }
            _pendingTextLoads--;
            if (effect == null)
            {
                RevLog.Warn("伤害文字加载失败", "RevGame"); return;
            }
            _effects.Add(effect);
            _textEffects.Add(effect);
            effect.ShowDamage(message.Position, message.Damage, message.Kind, _effectFinishedCallback, message.Side, _arrangeTextCallback);
        }

        /// <summary>
        /// 先预留放大和上浮范围，再尝试周围25个位置；加密文字时扩大可选位置，避免全挤在同一棋子头顶。
        /// 所有计算都在屏幕像素中进行，所以改变窗口大小后仍按可见尺寸避让，不靠固定世界坐标猜间距。
        /// </summary>
        private void ArrangeFloatingText(BattleEffectView text)
        {
            using (FeedbackArrangeMarker.Auto())
            {
                Camera camera = Camera.main;
                if (camera == null)
                {
                    return;
                }
                text.SetTextOrder(++_textOrder);
                Rect original = text.TextReservationRect;
                Vector3 anchor = text.transform.position;
                Rect allowed = FeedbackScreenRect(camera);
                float bestCost = float.PositiveInfinity;
                Vector2 best = Vector2.zero;
                // 同一次选点过程中其它文字不移动，预留矩形只需采样一次，不必在25个候选点里重复计算。
                int occupiedCount = 0;
                foreach (BattleEffectView other in _textEffects)
                {
                    if (other != text)
                    {
                        _reservationRects[occupiedCount++] = other.TextReservationRect;
                    }
                }
                for (int i = 0; i < 25; i++)
                {
                    int row = i / 5;
                    int column = i % 5;
                    int horizontal = column == 0 ? 0 : ((column + 1) / 2) * (column % 2 == 1 ? -1 : 1);
                    int vertical = row == 0 ? 0 : ((row + 1) / 2) * (row % 2 == 1 ? 1 : -1);
                    Vector2 delta = new Vector2(horizontal * original.width * 1.02f, vertical * original.height * 0.86f);
                    Rect candidate = original;
                    candidate.position += delta;
                    delta += new Vector2(Mathf.Clamp(candidate.x, allowed.xMin, Mathf.Max(allowed.xMin, allowed.xMax - candidate.width)) - candidate.x,
                        Mathf.Clamp(candidate.y, allowed.yMin, Mathf.Max(allowed.yMin, allowed.yMax - candidate.height)) - candidate.y);
                    candidate = original;
                    candidate.position += delta;
                    // 重叠面积的权重远大于移动距离；先争取不挡字，再尽量贴近受击棋子。
                    float cost = delta.sqrMagnitude * 0.001f;
                    for (int j = 0; j < occupiedCount; j++)
                    {
                        Rect occupied = _reservationRects[j];
                        float width = Mathf.Max(0f, Mathf.Min(candidate.xMax, occupied.xMax) - Mathf.Max(candidate.xMin, occupied.xMin));
                        float height = Mathf.Max(0f, Mathf.Min(candidate.yMax, occupied.yMax) - Mathf.Max(candidate.yMin, occupied.yMin));
                        cost += width * height * 100f;
                    }
                    if (cost < bestCost)
                    {
                        bestCost = cost; best = delta;
                    }
                }
                Vector3 pixel = camera.WorldToScreenPoint(anchor);
                text.transform.position = camera.ScreenToWorldPoint(pixel + new Vector3(best.x, best.y, 0f));
            }
        }

        /// <summary>文字限定在棋盘可见矩形内，避免避让到顶部HUD、右栏或屏外；棋盘位置直接使用同一组常量。</summary>
        private static Rect FeedbackScreenRect(Camera camera)
        {
            Vector3 first = new BoardPos(0, 0).ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            Vector3 last = new BoardPos(Consts.BoardSize - 1, Consts.BoardSize - 1).ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            Vector3 min = camera.WorldToScreenPoint(first - Vector3.one * (Consts.CellSize * 0.46f));
            Vector3 max = camera.WorldToScreenPoint(last + Vector3.one * (Consts.CellSize * 0.46f));
            return Rect.MinMaxRect(Mathf.Max(camera.pixelRect.xMin + 8f, min.x), Mathf.Max(camera.pixelRect.yMin + 8f, min.y),
                Mathf.Min(camera.pixelRect.xMax - 8f, max.x), Mathf.Min(camera.pixelRect.yMax - 8f, max.y));
        }

        /// <summary>切阶段先使加载代次失效，再清本地预算；上一轮的异步结果只能归池，不能占用新一轮名额。</summary>
        private void ResetFeedbackBudget()
        {
            _meleeEffects.Clear();
            _pendingMeleeLoads = 0;
            _pendingDamage.Clear();
            _projectileQueue.Clear();
            _attackQueue.Clear();
            _textEffects.Clear();
            _projectileEffects.Clear();
            _attackTextAt.Clear();
            _pendingTextLoads = _pendingProjectileLoads = 0;
            _nextAttackAt = _nextProjectileAt = 0f;
            _textOrder = 0;
        }
    }
}
