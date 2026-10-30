// ============================================================
// BattleService.LogCache.cs —— 战报只在新内容出现时拼接，重复攻击复用已有字符串。
// 战报属于服务实例，不共享静态草稿。缓存上限256条，保存文字身份/数值，不持有棋子或Unity对象。
// StringBuilder只减少未命中的中间拼接；ToString仍生成最终字符串，所以真正省重复分配靠缓存命中。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>战报完整输入：两个显示名、实际伤害、锁死状态及显示倍率；不同文案不能错误共用。</summary>
        private readonly struct AttackLogKey : IEquatable<AttackLogKey>
        {
            // 文案身份而不是Piece引用，棋子死亡/退场后有限字符串仍安全；改图鉴名字会形成新键。
            internal readonly string Attacker, Target;
            // 实际扣血点数，不能取屏幕乘十数值；保持原战报语义，与飘字显示倍率独立。
            internal readonly int Damage;
            internal readonly bool Locked;
            internal readonly float Multiplier;
            internal AttackLogKey(Piece attacker, Piece target, int damage)
            {
                Attacker = attacker.Def.DisplayName;
                Target = target.Def.DisplayName;
                Damage = damage;
                Locked = target.IsLocked;
                Multiplier = Locked ? Balance.LockedDamageMultiplier : 0f;
            }
            public bool Equals(AttackLogKey other)
            {
                return Attacker == other.Attacker && Target == other.Target && Damage == other.Damage && Locked == other.Locked && Multiplier == other.Multiplier;
            }
            public override bool Equals(object other)
            {
                return other is AttackLogKey key && Equals(key);
            }
            public override int GetHashCode()
            {
                unchecked
                {
                    return (((Attacker.GetHashCode() * 397) ^ Target.GetHashCode()) * 397 ^ Damage) * 397 ^ Multiplier.GetHashCode() ^ (Locked ? 1 : 0);
                }
            }
        }
        // 256是有限战报工作集容量，不是策划数值。环形FIFO覆盖最旧新键，命中不生成Builder或字符串。
        private readonly Dictionary<AttackLogKey, string> _attackLogCache = new Dictionary<AttackLogKey, string>(256);
        private readonly AttackLogKey[] _attackLogSlots = new AttackLogKey[256];
        private readonly StringBuilder _attackLogBuilder = new StringBuilder(128);
        // 下一个可替换槽0～255，不使用无限递增游标，长时间战斗不受int负溢出影响。
        private int _attackLogCursor;
        /// <summary>累计复用/新格式化次数，用于采样差值；不是硬件缓存命中率。</summary>
        public long AttackLogHits
        {
            get; private set;
        }
        /// <summary>本服务遇到新文案键而格式化的次数；新键超过容量淘汰旧键，但不清业务日志。</summary>
        public long AttackLogMisses
        {
            get; private set;
        }
        /// <summary>只格式化已结算攻击，保持旧战报逐字一致；不改伤害、不合并业务攻击事件。</summary>
        internal string FormatAttackLog(Piece attacker, Piece target, int realDamage)
        {
            var key = new AttackLogKey(attacker, target, realDamage);

            // 步骤1命中直接借已生成不可变字符串，不Clear/Append/ToString、不再产生这次文案分配。
            if (_attackLogCache.TryGetValue(key, out string content))
            {
                AttackLogHits++;
                return content;
            }
            AttackLogMisses++;
            int slot = _attackLogCursor;
            _attackLogCursor = (slot + 1) % _attackLogSlots.Length;

            if (_attackLogSlots[slot].Attacker != null)
            {
                _attackLogCache.Remove(_attackLogSlots[slot]);
            }
            // 步骤2未命中先淘汰，再复用Builder；最后只建一个结果字符串，字典总数始终有界。
            _attackLogBuilder.Clear();
            _attackLogBuilder.Append(key.Attacker).Append(" → ").Append(key.Target).Append(" -").Append(realDamage);

            if (key.Locked)
            {
                _attackLogBuilder.Append("（锁死×").Append(key.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)).Append('）');
            }
            content = _attackLogBuilder.ToString();
            _attackLogSlots[slot] = key;
            _attackLogCache.Add(key, content);
            return content;
        }
    }
}
