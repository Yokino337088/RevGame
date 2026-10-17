// ============================================================
// HandModel.cs —— 手牌的唯一数据来源。
// 为什么单独保存：两张同名牌仍是两张不同的牌，用编号才能准确消耗玩家点中的那张。
// 本类不认识按钮和图片；服务负责发牌、消费，界面只能读取 Cards。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;

namespace RevGame.Battle
{
    /// <summary>一张不可变的手牌；Id 在本次游戏中不重复，避免旧按钮误打新牌。</summary>
    public sealed class HandCard
    {
        public int Id { get; }
        public PieceKind Kind { get; }
        public StarTier Star { get; }

        public HandCard(int id, PieceKind kind, StarTier star)
        {
            Id = id;
            Kind = kind;
            Star = star;
        }
    }

    /// <summary>只处理手牌增删；阶段、额度和落点由 BattleService 统一校验。</summary>
    public sealed class HandModel
    {
        private readonly List<HandCard> _cards = new List<HandCard>();
        private readonly IReadOnlyList<HandCard> _readOnlyCards;
        private int _nextId = 1;
        public IReadOnlyList<HandCard> Cards
        {
            get
            {
                return _readOnlyCards;
            }
        }

        public HandModel()
        {
            // ★ 不直接把 List 当接口返回，否则外部仍可以强转回 List 并篡改手牌。
            _readOnlyCards = _cards.AsReadOnly();
        }

        public HandCard Find(int id)
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].Id == id)
                {
                    return _cards[i];
                }
            }

            return null;
        }

        internal bool Add(PieceKind kind, StarTier star)
        {
            if (_cards.Count >= Consts.MaxHandSize)
            {
                return false;
            }

            _cards.Add(new HandCard(_nextId++, kind, star));
            return true;
        }

        internal bool Remove(int id)
        {
            HandCard card = Find(id);
            return card != null && _cards.Remove(card);
        }

        internal void Clear()
        {
            _cards.Clear();
        }
    }
}
