// ============================================================
// RevDataTable.cs —— 数据表容器基类（字典存储 + 顺序遍历）
//
// 位置：Runtime\RevDataLoad\Core\
//
// 【它是什么】
//   一行数据（TData）→ 按主键收进字典。生成器只为每张表实现两个方法，
//   其余查询能力全部在这里复用 —— 生成的容器代码因此极短、也极难写错。
//
// 【为什么字典 + 列表两份，内存不就翻倍了？】
//   字典：按主键查 O(1)              —— 业务 99% 的用法（按 ID 查配置）
//   列表：按下标遍历 O(1)、顺序可预期 —— "遍历所有配置"用它
//   两者指向同一份 struct 数据，多出来的只是一个引用大小的指针数组，
//   换来"按 ID 查"和"按下标遍历"都 O(1)，非常划算。
//
// 【为什么数据用 struct 而不是 class？】
//   配置表动辄上万条，class 每条都要在托管堆上分配 → GC 压力大。
//   struct 连续存放在 List 的内部数组里，缓存友好、几乎不产生 GC。
//
// 【为什么生成的是两个文件（数据结构类 / 容器类）？】
//   数据结构 = "一条数据长什么样"（会被业务到处 using，稳定、改得少）
//   容器     = "一张表怎么组织与查询"（随工具重新生成）
//   分成两个文件，重新导表时容器的改动不会波及业务引用的数据结构文件，diff 也清爽。
// ============================================================
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 数据表容器基类。TKey = 主键类型，TData = 数据结构（struct）。
    /// </summary>
    public abstract class RevDataTable<TKey, TData> : RevIDataTable where TData : struct
    {
        /// <summary>主键 → 数据（按主键查 O(1)）</summary>
        private readonly Dictionary<TKey, TData> _map;

        /// <summary>顺序表（按下标遍历 O(1)，顺序 = 数据文件里的行顺序）</summary>
        private readonly List<TData> _list;

        /// <summary>
        /// 配置表默认所在的根目录（= 生成的 <c>RevResPath.Data</c> 的值）。
        /// 这里写字面量是**有意为之**：Runtime 程序集不能反向依赖 Generation 程序集（会成环）。
        /// </summary>
        public const string DefaultResourceRoot = "Data/";

        /// <param name="tableName">表名（也是默认资源名）</param>
        /// <param name="resourceRoot">数据文件所在根目录；不传则用 <see cref="DefaultResourceRoot"/>（"Data/"）</param>
        /// <param name="resourceName">数据文件名；不传则用表名</param>
        /// <param name="capacity">预估行数（避免字典/列表多次扩容）</param>
        /// <remarks>
        /// ★ 参数变过：以前是 (tableName, resourcePath, capacity)，现在把"路径"拆成
        ///   (resourceRoot, resourceName) 两段，和 RevResManager 的参数口径一致。
        ///   生成器写 <c>base("Hero")</c> 仍然有效（默认 "Data/" + "Hero"）。
        /// </remarks>
        protected RevDataTable(string tableName, string resourceRoot = null, string resourceName = null, int capacity = 64)
        {
            TableName = tableName;
            ResourceRoot = string.IsNullOrEmpty(resourceRoot) ? DefaultResourceRoot : resourceRoot;
            ResourceName = string.IsNullOrEmpty(resourceName) ? tableName : resourceName;

            _map = new Dictionary<TKey, TData>(capacity);
            _list = new List<TData>(capacity);
        }

        // ==================== RevIDataTable ====================

        public string TableName { get; }
        public string ResourceRoot { get; }
        public string ResourceName { get; }
        public int Count => _list.Count;
        public bool IsLoaded { get; private set; }

        // ==================== 生成器实现的两个方法 ====================

        /// <summary>从一条数据里取出主键（生成器按 Excel 的第一个字段生成）</summary>
        protected abstract TKey GetKey(in TData data);

        /// <summary>
        /// 把一行文本解析成一条数据。
        /// 【零反射、零装箱】生成器按字段类型生成直接的赋值语句 —— 这是本方案"高 GC 压力"问题的正解：
        /// 反射解析每行每字段都要装箱，上万行表会产生几十万次装箱，直接卡帧。
        /// </summary>
        /// <param name="cells">已拆分并反转义好的单元格（cells[0] 对应 Excel 第一列）</param>
        /// <returns>解析成功返回 true；行数据不合法返回 false（计入 errorCount，不中断整张表）</returns>
        protected abstract bool ParseRow(string[] cells, out TData data);

        // ==================== 查询（业务最常用）====================

        /// <summary>按主键查一条（对齐业界导表工具的 FindByKey 习惯）</summary>
        public bool FindByKey(TKey key, out TData data) => _map.TryGetValue(key, out data);

        /// <summary>按主键查一条（TryGet 风格，与 FindByKey 等价，给不同习惯的人用）</summary>
        public bool TryGet(TKey key, out TData data) => _map.TryGetValue(key, out data);

        /// <summary>按主键取一条；查不到返回 default（不抛异常，业务自己决定怎么处理）</summary>
        public TData Get(TKey key) => _map.TryGetValue(key, out TData data) ? data : default;

        public bool ContainsKey(TKey key) => _map.ContainsKey(key);

        /// <summary>按下标取一条（配合 Count 遍历整张表）</summary>
        public TData GetByIndex(int index) => _list[index];

        public bool TryGetByIndex(int index, out TData data)
        {
            if (index < 0 || index >= _list.Count) { data = default; return false; }
            data = _list[index];
            return true;
        }

        /// <summary>全部数据（顺序 = 数据文件行顺序），遍历整表用</summary>
        public IReadOnlyList<TData> All => _list;

        /// <summary>已装载出来的主键集合（调试 / 校验用）</summary>
        public IReadOnlyCollection<TKey> Keys => (IReadOnlyCollection<TKey>)_map.Keys;

        // ==================== 装载 / 卸载 ====================

        public void Clear()
        {
            _map.Clear();
            _list.Clear();
            IsLoaded = false;
        }

        /// <summary>
        /// 解析数据文本并装载。由 RevDataTableManager 在拿到 TextAsset 后调用。
        ///
        /// 【单行出错不中断整张表】某一行类型写错（如 int 列填了"无"）只让那一行作废、
        /// 计入 errorCount，其余行照常装载 —— 一张表的笔误不该让整个游戏起不来。
        /// 导出工具会在生成时就校验，这些错误正常不该出现，这里是最后一道防线。
        /// </summary>
        public int LoadText(string text, out int errorCount)
        {
            Clear();
            errorCount = 0;
            if (string.IsNullOrEmpty(text)) return 0;

            string[] lines = text.Split(RevDataTextFormat.LineSeparator);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = RevDataTextFormat.TrimLineEnd(lines[i]);
                if (RevDataTextFormat.IsSkippable(line)) continue;          // 空行 / "#" 注释行

                string[] cells = RevDataTextFormat.SplitFields(line);

                if (!ParseRow(cells, out TData data)) { errorCount++; continue; }

                TKey key = GetKey(in data);

                // 主键重复：保留先出现的那条，跳过后来的。
                // 【为什么不是"后者覆盖"？】覆盖会让 _list 与 _map 条数对不上（Count 虚高、
                // 遍历时同一条出现两次），业务侧会看到"两个来源给出不同结果"的怪现象。
                // 跳过则两份数据永远一致：Count == 字典条目数。
                if (_map.ContainsKey(key)) { errorCount++; continue; }

                _map.Add(key, data);
                _list.Add(data);
            }

            IsLoaded = true;      // 能解析出结构就算装载成功；空表也是"已装载（0 行）"
            return _list.Count;
        }
    }
}
