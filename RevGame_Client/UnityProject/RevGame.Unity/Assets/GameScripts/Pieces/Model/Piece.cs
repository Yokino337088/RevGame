// ============================================================
// Piece.cs
// 只保存一颗棋子的局内状态，不持有 GameObject；视图销毁不会改变战斗胜负。
// ============================================================
using RevGame.Core;
using RevGame.Data;

namespace RevGame.Pieces
{
    /// <summary>
    /// 棋盘上的一颗棋子（运行期对象，会掉血、会移动、会被锁死）。
    ///
    /// 和 <see cref = "PieceDef"/> 的区别：Def 是"图鉴上的一页"（静态、只读），
    /// 这里是"棋盘上的这一颗"（有 Id、有当前血量、有坐标）。**不要把血量写进 Def**。
    ///
    /// 为什么带 Id：表现层（GameObject）和逻辑层要靠 Id 对上号，
    /// 不要用"对象引用"当字典 key —— 对象销毁后引用会变成"假 null"，排查起来非常痛苦。
    /// </summary>
    public sealed class Piece
    {
        /// <summary>本局内唯一 Id（从 1 开始递增）。</summary>
        public int Id { get; }
        /// <summary>静态定义（外形、职业、星级、走法）。</summary>
        public PieceDef Def { get; private set; }
        /// <summary>阵营。</summary>
        public GameSide Side { get; }
        /// <summary>占格边长。Pos 始终指左下角，身体覆盖 Size×Size 格。</summary>
        public int Size { get; }
        public bool IsBoss { get; }
        /// <summary>只有带王冠的主将决定擒王胜负；其余大棋是护卫。</summary>
        public bool IsKing { get; }
        public int EyeCount { get; internal set; }
        /// <summary>棋群实际空格气数，不含加气增益；AI以此判断外围是否完全封住。</summary>
        public int OpenLiberties { get; internal set; }
        /// <summary>本棋是否正在有效的本方眼中驻守。仅规则刷新写入，离开、死亡或守眼骨架破坏立即失效。</summary>
        public bool IsEyeGarrison { get; internal set; }
        /// <summary>驻眼独立攻击倍率；乘在已有攻击结果上，反复刷新不会叠加。</summary>
        public float EyeAttackMultiplier => IsEyeGarrison ? 1f + (Side == GameSide.Player ? Balance.PlayerEyeAttackBonus : Balance.EnemyEyeAttackBonus) : 1f;
        /// <summary>驻眼独立攻速倍率；只影响攻击进度，不缩短移动冷却。</summary>
        public float EyeSpeedMultiplier => IsEyeGarrison ? 1f + (Side == GameSide.Player ? Balance.PlayerEyeSpeedBonus : Balance.EnemyEyeSpeedBonus) : 1f;
        /// <summary>敌方眼位不给防御；我方按等效生命倍率抵消伤害，至少保留1点有效伤害。</summary>
        public float EyeDefenseMultiplier => IsEyeGarrison && Side == GameSide.Player ? 1f + Balance.PlayerEyeDefenseBonus : 1f;
        /// <summary>是否为有效我方眼的上下左右守子；规则层重算，多个眼共享一颗棋也只给一份光环。</summary>
        public bool HasEyeGuardAura { get; internal set; }
        /// <summary>守眼光环独立防御倍率，和驻眼/本轮防御相乘；破眼立即还原。</summary>
        public float EyeGuardDefenseMultiplier => HasEyeGuardAura ? 1f + Balance.EyeGuardDefenseBonus : 1f;
        /// <summary>本棋一生只可驻眼复活一次；移动、升星、换轮不会重置，快照单独复制。</summary>
        public bool EyeReviveUsed { get; private set; }
        /// <summary>致死结算内部调用；仅仍在有效我方眼中且尚未消费资格的棋可恢复生命，不改变占格身份。</summary>
        internal bool TryReviveInEye()
        {
            if (IsAlive || Side != GameSide.Player || !IsEyeGarrison || EyeReviveUsed) { return false; }
            EyeReviveUsed = true;
            Hp = System.Math.Max(1, (int)(MaxHp * Balance.EyeReviveHpRatio));
            QueryBoard?.TargetStateChanged();
            return true;
        }
        public int ConnectedCells { get; internal set; }
        public int BonusLiberties { get; internal set; }
        internal float ActionProgress { get; set; }
        /// <summary>敌棋下一次允许自动靠近的战斗时间；攻速增益不能提前这个时间。</summary>
        internal float NextMoveAt { get; set; }

        /// <summary>Boss控制由本局服务写入；隐藏仍保留占格预约，防止返场时被挤掉。快照复制，离场清零。</summary>
        /// <summary>仅BoardModel.Place/Remove维护，用于隐藏/死亡时失效当前棋盘射界缓存，不持有Unity对象。</summary>
        internal Board.BoardModel QueryBoard;
        private bool _bossHidden;
        public bool BossHidden
        {
            get { return _bossHidden; }
            internal set
            {
                if (_bossHidden == value) { return; }
                _bossHidden = value;
                QueryBoard?.TargetStateChanged();
            }
        }
        public bool BossFrozen { get; internal set; }
        public bool BossInvulnerable { get; internal set; }
        internal float BossHiddenUntil, BossFrozenUntil;
        /// <summary>领域的临时攻击/攻速倍率，默认1，每个模拟步按当前区域重算，禁止改共享图鉴。</summary>
        public float BossAttackFactor { get; internal set; } = 1f;
        public float BossSpeedFactor { get; internal set; } = 1f;

        private int _maxHp;
        private int _baseMaxHp;
        private float _roundHealthScale = 1f;
        /// <summary>关卡成长后的攻击倍率；与只读图鉴分开，避免一颗敌人变强污染全图鉴。</summary>
        public float AttackScale { get; private set; } = 1f;
        /// <summary>本轮免疫围困：气眼仍真实计算，但不禁止行动、不吃锁死/孤立增伤。</summary>
        public bool IgnoreEncirclement { get; internal set; }
        /// <summary>本轮尚未用掉的护盾次数；快照也复制，AI 试算不能消耗实战护盾。</summary>
        public int ShieldCharges { get; internal set; }
        /// <summary>当前坐标（由棋盘 <c>Place/Move/Remove</c> 维护，业务不要自己改）。</summary>
        public BoardPos Pos { get; internal set; }
        /// <summary>当前血量。</summary>
        public int Hp { get; private set; }

        /// <summary>最大血量（取自 Def）。</summary>
        public int MaxHp
        {
            get
            {
                return _maxHp;
            }
        }

        /// <summary>还剩几口气（由 <c>GoRules.Evaluate</c> 统一刷新）。</summary>
        public int Liberties { get; internal set; }
        /// <summary>围棋状态：正常 / 叫吃 / 锁死。</summary>
        public GroupState GoState { get; internal set; }

        /// <summary>是否还活着。</summary>
        public bool IsAlive
        {
            get
            {
                return Hp > 0;
            }
        }

        /// <summary>是否零气锁死：不能自动移动、按配置承受额外伤害；敌方不能攻击，我方仍可守阵反击。</summary>
        public bool IsLocked
        {
            get
            {
                return !IgnoreEncirclement && GoState == GroupState.Locked;
            }
        }

        /// <summary>血量百分比（0~1），UI 画血条用。</summary>
        public float HpRatio
        {
            get
            {
                return MaxHp <= 0 ? 0f : (float)Hp / MaxHp;
            }
        }

        public Piece(int id, PieceDef def, GameSide side, int size = 0, bool isBoss = false, bool isKing = false, float hpScale = 1f, float attackScale = 1f)
        {
            Id = id;
            Def = def;
            Side = side;
            // 我方占格按星级取配置（StarConfig.PlayerBodySize）；敌方普通棋固定 1×1，Boss 大棋由调用方显式传入占格。
            Size = size > 0 ? size : (side == GameSide.Player ? PieceCatalog.PlayerBodySize(def.Star) : 1);
            IsBoss = isBoss;
            IsKing = isKing;
            _maxHp = System.Math.Max(1, (int)(def.MaxHp * hpScale));
            _baseMaxHp = _maxHp;
            AttackScale = attackScale;
            Hp = _maxHp;
            GoState = GroupState.Normal;
            Liberties = 4;
        }

        /// <summary>
        /// 创建算法试算用的独立棋子。只共享只读图鉴，血量、坐标、气眼、行动进度全部复制。
        /// 不能把实战棋子直接放进临时棋盘：GoRules 会回写气眼，试走一步就会污染真实战斗。
        /// </summary>
        public Piece CreateSnapshot()
        {
            return new Piece(this);
        }

        private Piece(Piece source)
        {
            Id = source.Id; Side = source.Side; Size = source.Size; IsBoss = source.IsBoss; IsKing = source.IsKing;
            CopySnapshotState(source);
        }

        /// <summary>AI工作区独占副本复用：每轮覆盖全部可变字段，只共享只读Def，不共享原棋引用或缓存。</summary>
        internal void CopySnapshotState(Piece source)
        {
            if (Id != source.Id || Side != source.Side || Size != source.Size || IsBoss != source.IsBoss || IsKing != source.IsKing)
            { throw new System.InvalidOperationException("快照身份不一致，不能复用"); }
            Def = source.Def;
            _maxHp = source._maxHp;
            _baseMaxHp = source._baseMaxHp;
            _roundHealthScale = source._roundHealthScale;
            AttackScale = source.AttackScale;
            IgnoreEncirclement = source.IgnoreEncirclement;
            ShieldCharges = source.ShieldCharges;
            Hp = source.Hp;
            BossHidden = source.BossHidden; BossFrozen = source.BossFrozen;
            BossInvulnerable = source.BossInvulnerable;
            BossHiddenUntil = source.BossHiddenUntil; BossFrozenUntil = source.BossFrozenUntil;
            BossAttackFactor = source.BossAttackFactor; BossSpeedFactor = source.BossSpeedFactor;
            Pos = source.Pos;
            EyeCount = source.EyeCount;
            OpenLiberties = source.OpenLiberties;
            IsEyeGarrison = source.IsEyeGarrison;
            HasEyeGuardAura = source.HasEyeGuardAura;
            EyeReviveUsed = source.EyeReviveUsed;
            ConnectedCells = source.ConnectedCells;
            BonusLiberties = source.BonusLiberties;
            ActionProgress = source.ActionProgress;
            NextMoveAt = source.NextMoveAt;
            Liberties = source.Liberties;
            GoState = source.GoState;
        }

        /// <summary>原位升一星：换图鉴和属性，绝不换 Id/坐标/占格，所以眼阵里的棋子也能安全升级。</summary>
        public bool UpgradeInPlace()
        {
            if (!IsAlive || Def.Star == StarTier.Three)
            {
                return false;
            }
            PieceDef next = PieceCatalog.Get(Def.Kind, (StarTier)((int)Def.Star + 1));
            if (next == null)
            {
                return false;
            }
            float ratio = HpRatio;
            // ★ 先保存比例，再换最大生命。残血升级仍保留伤势，不能无意变成免费满血。
            float growth = (float)_baseMaxHp / Def.MaxHp;
            Def = next;
            _baseMaxHp = System.Math.Max(1, (int)(next.MaxHp * growth));
            _maxHp = System.Math.Max(1, (int)(_baseMaxHp * _roundHealthScale));
            Hp = System.Math.Max(1, (int)(_maxHp * ratio));
            return true;
        }

        /// <summary>每轮生命加成以基础值重算，而非反复相乘；换轮移除后不会越滚越大。</summary>
        internal void SetRoundHealthScale(float scale)
        {
            if (System.Math.Abs(_roundHealthScale - scale) < 0.00001f)
            {
                return;
            }
            float ratio = HpRatio;
            bool alive = IsAlive;
            _roundHealthScale = scale;
            _maxHp = System.Math.Max(1, (int)(_baseMaxHp * scale));
            Hp = alive ? System.Math.Max(1, (int)(_maxHp * ratio)) : 0;
        }

        /// <summary>取出完整攻击次数并保留小数进度。1e-6只是float累计误差容限，防止1.9999999被截成1；不是策划攻速。</summary>
        public int ConsumeCompletedActions()
        {
            int actions = (int)(ActionProgress + 0.000001f);
            ActionProgress = System.Math.Max(0f, ActionProgress - actions);
            return actions;
        }

        /// <summary>
        /// 受到伤害。返回**实际扣掉的血**（可能因为血量不够而小于传入值），
        /// 让调用方能在日志/飘字里显示真实数字（而不是"面板上写着 -30，实际只掉了 5"）。
        /// </summary>
        public int TakeDamage(int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }

            if (!IsAlive || BossHidden || BossInvulnerable)
            {
                return 0;
            }

            int real = amount > Hp ? Hp : amount;
            Hp -= real;
            if (Hp == 0) { QueryBoard?.TargetStateChanged(); }
            return real;
        }

        /// <summary>回血，返回实际回复量（不会超过上限）。</summary>
        public int Heal(int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }

            if (!IsAlive)
            {
                return 0;
            }

            int room = MaxHp - Hp;
            int real = amount > room ? room : amount;
            Hp += real;
            return real;
        }

        public override string ToString()
        {
            return "#" + Id + " " + Def.DisplayName + " [" + Side + "] HP " + Hp + "/" + MaxHp + " @" + Pos;
        }
    }
}
