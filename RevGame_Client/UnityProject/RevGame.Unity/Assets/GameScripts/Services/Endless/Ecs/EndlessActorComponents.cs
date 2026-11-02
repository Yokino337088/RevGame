using System.Collections;
using System.Collections.Generic;

namespace RevGame.Endless
{
    /// <summary>
    /// 棋盘演员组件与实体编号索引。保留稳定遍历顺序供围棋规则适配，按Id查询为常数时间。
    /// 占格数据仍由BoardModel管理；此集合仅管理演员寿命，不自行修改棋盘。
    /// </summary>
    public sealed class EndlessActorComponents : IEnumerable<EndlessUnit>
    {
        /// <summary>保持固定动作顺序的演员组件列，最大为棋盘演员加盘外领主；不保留死演员。</summary>
        private readonly List<EndlessUnit> _ordered = new List<EndlessUnit>(81);
        /// <summary>本局实体编号到组件的索引，Add/Remove对称维护。</summary>
        private readonly Dictionary<int, EndlessUnit> _byId = new Dictionary<int, EndlessUnit>(81);
        public int Count => _ordered.Count;
        public EndlessUnit this[int index] => _ordered[index];
        /// <summary>实体创建时登记唯一Id；同Id升星必须先移除旧演员，再登记新组件。</summary>
        public void Add(EndlessUnit actor)
        {
            _byId.Add(actor.Piece.Id, actor);
            _ordered.Add(actor);
        }
        public bool Remove(EndlessUnit actor)
        {
            if (!_ordered.Remove(actor))
            {
                return false;
            }
            _byId.Remove(actor.Piece.Id);
            return true;
        }
        /// <summary>统一移除实体索引，死亡后迟到的查询无法再找到旧演员。</summary>
        public void RemoveAt(int index)
        {
            _byId.Remove(_ordered[index].Piece.Id);
            _ordered.RemoveAt(index);
        }
        public EndlessUnit Find(int id)
        {
            return _byId.TryGetValue(id, out EndlessUnit actor) ? actor : null;
        }
        public void Clear()
        {
            _ordered.Clear();
            _byId.Clear();
        }
        public List<EndlessUnit>.Enumerator GetEnumerator()
        {
            return _ordered.GetEnumerator();
        }
        IEnumerator<EndlessUnit> IEnumerable<EndlessUnit>.GetEnumerator()
        {
            return _ordered.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return _ordered.GetEnumerator();
        }
    }
}
