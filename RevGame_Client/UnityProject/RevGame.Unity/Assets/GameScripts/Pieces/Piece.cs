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
        public PieceDef Def { get; }
        /// <summary>阵营。</summary>
        public GameSide Side { get; }
        /// <summary>占格边长。Pos 始终指左下角，身体覆盖 Size×Size 格。</summary>
        public int Size { get; }
        public bool IsBoss { get; }
        /// <summary>只有带王冠的主将决定擒王胜负；其余大棋是护卫。</summary>
        public bool IsKing { get; }
        public int EyeCount { get; internal set; }
        public int ConnectedCells { get; internal set; }
        public int BonusLiberties { get; internal set; }
        internal float ActionProgress { get; set; }

        private readonly int _maxHp;
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

        /// <summary>是否被锁死（不能移动、不能攻击、挨双倍伤害）。</summary>
        public bool IsLocked
        {
            get
            {
                return GoState == GroupState.Locked;
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

        public Piece(int id, PieceDef def, GameSide side, int size = 0, bool isBoss = false, bool isKing = false, float hpScale = 1f)
        {
            Id = id;
            Def = def;
            Side = side;
            Size = size > 0 ? size : (side == GameSide.Player && def.Star == StarTier.Three ? 2 : 1);
            IsBoss = isBoss;
            IsKing = isKing;
            _maxHp = System.Math.Max(1, (int)(def.MaxHp * hpScale));
            Hp = _maxHp;
            GoState = GroupState.Normal;
            Liberties = 4;
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

            if (!IsAlive)
            {
                return 0;
            }

            int real = amount > Hp ? Hp : amount;
            Hp -= real;
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
