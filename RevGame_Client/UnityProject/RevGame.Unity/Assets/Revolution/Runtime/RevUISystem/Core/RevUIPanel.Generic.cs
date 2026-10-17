// ============================================================
// RevUIPanel.Generic.cs —— 带数据的面板基类（RevUIPanel&lt;TData&gt;）
//
// 位置：Runtime\RevUISystem\Core\
//
// 【它把"数据"这一层单独拎出来了】
//   有数据的界面继承它，于是"数据"有了一个**强类型的名字**：
//
//       public sealed class BagData          // 纯 C# 数据对象（放哪都行，同一个文件也可以）
//       {
//           public bool UseCellLayout;
//           public IReadOnlyList<BagItem> Items;
//       }
//
//       [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]
//       public sealed class BagPanel : RevUIPanel<BagData>
//       {
//           [RevBind] private ScrollRect _list;      // 表现：控件
//
//           protected override void OnBindView()   { /* 只装配 */ }
//           protected override void OnRefreshView() { /* 只画 Data */ }
//           protected override void OnDataChanged(BagData oldData, BagData newData) { /* 增量刷新（可选） */ }
//       }
//
//   ★ 数据对象用**纯 C# 类**：它不碰 UnityEngine（除了展示用的 Sprite 之类），
//     所以业务数据可以被普通单元测试直接构造与断言 —— 这是"数据与表现分离"最实际的收益。
//
// 【这条规则是被强制的】OnRefreshView 在这里是**抽象方法**：
//   有数据的界面必须明确"数据怎么落到界面上"，不允许糊过去。
//   而没有数据的界面（纯静态说明页）继承 RevUIPanel 就少写一个方法。
// ============================================================
namespace Revolution
{
    /// <summary>带业务数据的面板基类：数据用强类型 TData 承载（推荐 class，避免装箱）</summary>
    public abstract class RevUIPanel<TData> : RevUIPanel
    {
        /// <summary>当前业务数据（未设置过时为 default）</summary>
        public TData Data { get; private set; }

        /// <summary>设置数据（强类型入口，不装箱）</summary>
        public void SetData(TData data)
        {
            Data = data;
            base.SetData(data);           // 走基类的统一流程：OnDataChanged → 可见则重画
        }

        /// <summary>数据变化（增量刷新的地方：只更新变化的那几个控件，别整屏重刷）</summary>
        protected virtual void OnDataChanged(TData oldData, TData newData) { }

        /// <summary>★ 数据落屏：有数据的界面必须实现（框架用它把"分离"变成结构约束）</summary>
        protected abstract override void OnRefreshView();

        /// <summary>基类的 object 通道 → 转成强类型钩子（业务不必关心 object）</summary>
        protected sealed override void OnDataChanged(object oldData, object newData)
            => OnDataChanged(As(oldData), As(newData));

        /// <summary>
        /// ★ Bug 修复（2026-09-30）：管理器走的是基类 SetData(object) 通道（本类的 SetData(TData)
        ///   是方法隐藏不是重写，管理器调不到）—— 原实现这条通道只写了基类 DataObject，
        ///   强类型 Data 永远是 null / 上一次的旧值，OnRefreshView"只画 Data"的契约被整体破坏。
        ///   现在统一在这里把 object 同步成强类型 Data（弱类型 SetData(TData) 最终也汇到这条通道，行为一致）。
        /// </summary>
        protected sealed override void OnDataSet(object data)
            => Data = data is TData typed ? typed : default;

        /// <summary>复用时连强类型数据一起清掉（否则会出现"上次的数据还在"这种最难查的 bug）</summary>
        internal override void InternalClearData()
        {
            base.InternalClearData();
            Data = default;
        }

        private static TData As(object value) => value is TData typed ? typed : default;
    }
}
