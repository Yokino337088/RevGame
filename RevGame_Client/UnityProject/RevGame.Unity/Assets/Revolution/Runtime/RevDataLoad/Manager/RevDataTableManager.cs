// ============================================================
// RevDataTableManager.cs —— 数据表管理器（配置表唯一入口）
//
// 位置：Runtime\RevDataLoad\Manager\
//
// 【核心思想：配置表也是一种"资源"】
//   所以这里不碰任何文件 API —— 数据表统一交给资源系统去取，白拿四件事：
//     · 缓存：同一张表只读一次盘（重复请求直接命中）
//     · 引用计数：业务都释放了才真正卸载
//     · 分组卸载：切场景时可 RevResBootstrap.Shutdown(RevResGroup.Config) 整组回收
//     · 平台透明：编辑器直读工程、真机走 AB，业务代码一行都不用改
//
// 【为什么禁止直接读 StreamingAssets？】
//   ① 跨平台行为不一致：Android 里它在 APK 内（只能 UnityWebRequest 读）、
//      iOS 里是只读路径、WebGL 里干脆是个 URL；
//   ② 绕过资源系统 = 没有缓存、没有引用计数 —— 表会被反复读、且永远卸不掉；
//   ③ 热更新无处落脚：改配置只能重新出包。
//   走资源系统，"读哪儿、怎么读"由策略决定，业务只认逻辑路径。
//
// 【最常用三行】
//   await RevDataTableManager.LoadAsync&lt;HeroTable&gt;();        // 加载
//   HeroTable tbl = RevDataTableManager.Get&lt;HeroTable&gt;();    // 取容器（按类型）
//   if (tbl.FindByKey(1001, out Hero cfg)) { ... }          // 查一条（主键可以是 int / string 等任意类型）
//
// 【字符串驱动也能用】
//   表名与主键都可以是字符串，编译期不必知道具体是哪个容器类：
//     RevIDataTable t = RevDataTableManager.Get("Buff");               // 按表名取表
//     if (RevDataTableManager.TryGet&lt;BuffTable&gt;("Buff", out var tb)) { ... }
//     tb.FindByKey("BUFF_ATK_UP", out Buff buff);                // 字符串主键查一行
//
// 【主键类型由工具生成，运行时不关心】
//   int 主键 → RevDataTable&lt;int, T&gt;；string 主键 → RevDataTable&lt;string, T&gt;；
//   管理器只做"表"这一层的登记与查找，与主键是什么类型无关。
//
// 【两种失败风格，随你挑】
//   LoadAsync&lt;T&gt;()                  → 失败抛 RevDataTableLoadException（await 时直接抛出，不会静默）
//   LoadAsync&lt;T&gt;(onFinished)        → 失败回调 (null, 原因)，不抛异常（对齐资源系统的风格）
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    public static class RevDataTableManager
    {
        /// <summary>
        /// 表名 → 容器实例。★ 键用的是【表名】（如 "Hero"，来自 RevIDataTable.TableName），
        /// 不是容器类名（"HeroTable"）—— 因为表名是"数据侧"的名字，可以被配置、被字符串驱动；
        /// 容器类名是"代码侧"的名字，业务不该依赖它。
        /// </summary>
        private static readonly Dictionary<string, RevIDataTable> _tables =
            new Dictionary<string, RevIDataTable>(System.StringComparer.Ordinal);

        /// <summary>
        /// 容器类型 → 容器实例。给 Get&lt;T&gt;() / Unload&lt;T&gt;() 用：
        /// 泛型查询走类型键（O(1)、不分配字符串），字符串查询走表名键，两者指向同一个实例。
        /// </summary>
        private static readonly Dictionary<System.Type, RevIDataTable> _tablesByType =
            new Dictionary<System.Type, RevIDataTable>();

        /// <summary>
        /// 加载中的容器类型 → 该次加载的完成源（★ Bug 修复 2026-09-30 新增）。
        /// 【为什么必须有这张表】资源系统只会把"同一资源的并发 IO"合并成一次，但
        /// <b>容器实例与引用计数不会自动合并</b> —— 没有它时，两个并发调用会各 new 一个容器、
        /// 各占一次资源引用；最终只有一个实例被登记（另一个被覆盖、无人持有），
        /// 引用计数却永远回不到零 → TextAsset 再也卸不掉（泄漏），且两个调用方拿到不同实例。
        /// 有了它：同类型的并发请求共享同一份加载任务。
        /// </summary>
        private static readonly Dictionary<Type, RevPendingLoad> _loading = new Dictionary<Type, RevPendingLoad>();
        private static readonly Dictionary<Type, RevResHandle> _leases = new Dictionary<Type, RevResHandle>();
        private static readonly Dictionary<Type, string> _registeredNames = new Dictionary<Type, string>();
        private static int _generation;
        private static bool _clearing;

        // 请求独立保存租约和完成源。卸载先摘登记再取消，晚到回调只能归还自己的句柄，不能回填旧表。
        private abstract class RevPendingLoad
        {
            internal RevResHandle Handle;
            internal bool Released;
            internal void Release()
            {
                if (Released || Handle == null) { return; }
                Released = true;
                RevResManager.DecRef(Handle);
            }
            internal abstract void Cancel();
        }

        private sealed class RevPendingLoad<T> : RevPendingLoad where T : class, RevIDataTable
        {
            internal readonly RevTaskCompletionSource<T> Source;
            internal readonly string Name;
            internal readonly string Root;
            internal readonly string Resource;
            internal RevPendingLoad(RevTaskCompletionSource<T> source, string name, string root, string resource)
            {
                Source = source;
                Name = name;
                Root = root;
                Resource = resource;
            }
            internal override void Cancel()
            {
                Release();
                Source.SetException(new RevDataTableLoadException(Name, Root, Resource, RevResLoadErrorReason.Cancelled));
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            UnloadAll();
        }

        // ==================== 查询 ====================

        /// <summary>已装载的表数量</summary>
        public static int LoadedCount => _tables.Count;

        /// <summary>全部已装载的表（键 = 表名；调试窗口 / 排查用）</summary>
        public static IReadOnlyDictionary<string, RevIDataTable> Tables => _tables;

        /// <summary>取容器；没加载过返回 null（不 new、不抛异常）</summary>
        public static T Get<T>() where T : class, RevIDataTable
            => _tablesByType.TryGetValue(typeof(T), out RevIDataTable table) ? table as T : null;

        /// <summary>
        /// 【按表名取表】给"字符串驱动"的场景用：表名来自配置 / 命令行 / 策划表，
        /// 编译期不知道是哪个容器类型时，就用这个。
        /// 取到后是 RevIDataTable，需要具体容器时再 as 一下（或直接用它的 Count / IsLoaded）。
        /// </summary>
        public static RevIDataTable Get(string tableName)
            => (tableName != null && _tables.TryGetValue(tableName, out RevIDataTable table)) ? table : null;

        /// <summary>按表名取具体容器（TryGet 风格，不抛异常、不装箱）</summary>
        public static bool TryGet<T>(string tableName, out T table) where T : class, RevIDataTable
        {
            if (tableName != null && _tables.TryGetValue(tableName, out RevIDataTable found) && found.IsLoaded)
            {
                table = found as T;
                return table != null;
            }
            table = null;
            return false;
        }

        /// <summary>是否已装载（泛型版）</summary>
        public static bool IsLoaded<T>() where T : class, RevIDataTable
            => _tablesByType.TryGetValue(typeof(T), out RevIDataTable table) && table.IsLoaded;

        /// <summary>是否已装载（按表名）</summary>
        public static bool IsLoaded(string tableName)
            => tableName != null && _tables.TryGetValue(tableName, out RevIDataTable table) && table.IsLoaded;

        /// <summary>取容器（TryGet 风格；没加载过返回 false）</summary>
        public static bool TryGet<T>(out T table) where T : class, RevIDataTable
        {
            if (_tablesByType.TryGetValue(typeof(T), out RevIDataTable found) && found.IsLoaded)
            {
                table = (T)found;
                return true;
            }
            table = null;
            return false;
        }

        // ==================== 加载 ====================

        /// <summary>
        /// 同步加载。仅在"编辑器直读"或"该资源已在资源缓存里"时才返回结果；
        /// 真机第一次加载请用异步版（同步加载 AB 是拿不到结果的）。
        /// 失败返回 null。
        /// </summary>
        public static T Load<T>(RevResGroup group = RevResGroup.Config) where T : class, RevIDataTable, new()
        {
            if (_clearing) { return null; }
            T cached = Get<T>();
            if (cached != null && cached.IsLoaded) { return cached; }
            // 同步入口不能抢占同类型在途请求，否则会产生两份容器和两份无人管理的引用。
            if (_loading.ContainsKey(typeof(T))) { return null; }
            int generation = _generation;
            RevResHandle handle = null;
            bool transferred = false;
            try
            {
                var table = new T();
                string name = table.TableName;
                handle = RevResManager.Load(table.ResourceRoot, table.ResourceName, typeof(TextAsset), group);
                if (!ReadText(table, handle) || generation != _generation) { return null; }
                Register(table, handle, name);
                transferred = true;
                return table;
            }
            finally
            {
                // TypeMismatch 的独立失败句柄没有取得引用；按句柄归还不会误扣原路径的其它持有者。
                if (!transferred) { RevResManager.DecRef(handle); }
            }
        }

        /// <summary>同类型并发共享任务；解析异常写入失败任务，卸载使旧请求以 Cancelled 结束。</summary>
        public static RevTask<T> LoadAsync<T>(RevResGroup group = RevResGroup.Config, RevResLoadPriority priority = RevResLoadPriority.Normal) where T : class, RevIDataTable, new()
        {
            var source = RevTask<T>.CreateSource();
            if (_clearing)
            {
                source.SetException(new RevOperationCanceledException());
                return source.Task;
            }
            RevPendingLoad<T> request = null;
            try
            {
                T cached = Get<T>();
                if (cached != null && cached.IsLoaded) { return RevTask<T>.FromResult(cached); }
                if (_loading.TryGetValue(typeof(T), out RevPendingLoad pending))
                {
                    return ((RevPendingLoad<T>)pending).Source.Task;
                }

                int generation = _generation;
                var table = new T();
                request = new RevPendingLoad<T>(source, table.TableName, table.ResourceRoot, table.ResourceName);
                RevPendingLoad<T> currentRequest = request;
                if (generation != _generation) { request.Cancel(); return source.Task; }
                _loading[typeof(T)] = request;
                RevResHandle returned = RevResManager.LoadAsync(request.Root, request.Resource, typeof(TextAsset), handle =>
                {
                    currentRequest.Handle = handle;
                    bool transferred = false;
                    try
                    {
                        if (generation != _generation || !IsCurrent<T>(currentRequest))
                        {
                            currentRequest.Cancel();
                            return;
                        }
                        if (!ReadText(table, handle))
                        {
                            RevResLoadErrorReason reason = handle == null ? RevResLoadErrorReason.PolicyNotFound : handle.ErrorReason;
                            if (reason == RevResLoadErrorReason.None) { reason = RevResLoadErrorReason.AssetLoadFail; }
                            throw new RevDataTableLoadException(currentRequest.Name, currentRequest.Root, currentRequest.Resource, reason);
                        }
                        // 解析器是外部代码，可能在 LoadText 内卸载；回来后必须再确认请求仍属于当前会话。
                        if (generation != _generation || !IsCurrent<T>(currentRequest))
                        {
                            currentRequest.Cancel();
                            return;
                        }
                        Register(table, handle, currentRequest.Name);
                        transferred = true;
                        currentRequest.Released = true; // 归还责任已交给表租约，完成续体里 UnloadAll 也不能再由请求重复归还。
                        _loading.Remove(typeof(T));
                        source.SetResult(table);
                    }
                    catch (Exception e)
                    {
                        if (IsCurrent<T>(currentRequest)) { _loading.Remove(typeof(T)); }
                        source.SetException(e);
                    }
                    finally
                    {
                        if (!transferred) { currentRequest.Release(); }
                        if (IsCurrent<T>(currentRequest)) { _loading.Remove(typeof(T)); }
                    }
                }, group, priority);
                // 缓存命中可能同步回调；不要覆盖回调已登记的句柄或重复归还。
                if (request.Handle == null) { request.Handle = returned; }
                if (generation != _generation) { request.Release(); }
            }
            catch (Exception e)
            {
                if (request != null)
                {
                    if (IsCurrent<T>(request)) { _loading.Remove(typeof(T)); }
                    request.Release();
                }
                source.SetException(e);
            }
            return source.Task;
        }

        private static bool IsCurrent<T>(RevPendingLoad request)
        {
            return _loading.TryGetValue(typeof(T), out RevPendingLoad current) && ReferenceEquals(current, request);
        }

        /// <summary>
        /// 异步加载（回调式，不抛异常）：失败时 table 为 null，reason 说明原因。
        /// 适合不想在每个调用处写 try/catch 的场景。
        /// </summary>
        /// <remarks>
        /// ★ Bug 修复（2026-09-30）：实现改为"复用上面的 await 版 + 回调中继"—— 之前是独立的一份实现，
        /// 既漏了并发合并（同表两次并发请求会各建一个容器、引用计数失衡），又漏了失败还引用。
        /// 现在两种风格共享同一套加载逻辑，修一处两处都受益。
        /// </remarks>
        public static void LoadAsync<T>(Action<T, RevResLoadErrorReason> onFinished,
            RevResGroup group = RevResGroup.Config, RevResLoadPriority priority = RevResLoadPriority.Normal)
            where T : class, RevIDataTable, new()
            => RelayToCallback(LoadAsync<T>(group, priority), onFinished);

        /// <summary>把"可 await 的加载任务"转成回调风格（失败给出原因码，不抛异常）</summary>
        private static async void RelayToCallback<T>(RevTask<T> task, Action<T, RevResLoadErrorReason> onFinished)
            where T : class, RevIDataTable
        {
            if (onFinished == null) return;

            T table = null;
            RevResLoadErrorReason reason = RevResLoadErrorReason.None;
            try { table = await task; }
            catch (RevDataTableLoadException e) { reason = e.Reason; }
            catch (RevOperationCanceledException) { reason = RevResLoadErrorReason.Cancelled; }
            catch (Exception e)
            {
                reason = RevResLoadErrorReason.AssetLoadFail;
                RevLog.Exception(e, "配置表加载失败", "Data");
            }
            // 加载失败和业务回调失败是两件事：回调只执行一次，不把业务异常伪装成第二次加载失败。
            try { onFinished(table, reason); }
            catch (Exception e) { RevLog.Exception(e, "配置表业务回调异常", "Data"); }
        }

        // ==================== 卸载 ====================

        /// <summary>卸载一张表：清空数据 + 还掉资源引用（引用归零后由资源系统延迟释放）</summary>
        public static bool Unload<T>() where T : class, RevIDataTable
            => _registeredNames.TryGetValue(typeof(T), out string name) && Unload(name);

        /// <summary>按表名卸载（配合 Get(string) 的字符串驱动场景）</summary>
        public static bool Unload(string tableName)
        {
            if (string.IsNullOrEmpty(tableName) || !_tables.TryGetValue(tableName, out RevIDataTable table)) { return false; }
            Unregister(table);
            ReleaseTable(table);
            return true;
        }

        /// <summary>卸载全部已加载表，并取消在途请求；回调期间拒绝重入创建新表。</summary>
        public static void UnloadAll()
        {
            if (_clearing) { return; }
            _clearing = true;
            _generation++;
            RevPendingLoad[] pending = new List<RevPendingLoad>(_loading.Values).ToArray();
            RevIDataTable[] tables = new List<RevIDataTable>(_tablesByType.Values).ToArray();
            _loading.Clear();
            _tables.Clear();
            _tablesByType.Clear();
            _registeredNames.Clear();
            try
            {
                foreach (RevPendingLoad request in pending) { request.Cancel(); }
                foreach (RevIDataTable table in tables) { ReleaseTable(table); }
            }
            finally { _clearing = false; }
        }

        private static void ReleaseTable(RevIDataTable table)
        {
            Type type = table.GetType();
            if (_leases.TryGetValue(type, out RevResHandle handle))
            {
                _leases.Remove(type);
                RevResManager.DecRef(handle);
            }
            try { table.Clear(); }
            catch (Exception e) { RevLog.Exception(e, "配置表卸载清理异常", "Data"); }
        }

        /// <summary>先校验外部元数据，再原子登记；不覆盖其它类型的同名表。</summary>
        private static void Register(RevIDataTable table, RevResHandle handle, string name)
        {
            Type type = table.GetType();
            if (string.IsNullOrEmpty(name)) { throw new InvalidOperationException("配置表 TableName 不能为空"); }
            if (_tables.ContainsKey(name) || _tablesByType.ContainsKey(type))
            {
                throw new InvalidOperationException($"配置表重复登记：{name} / {type.Name}");
            }
            _tables.Add(name, table);
            _tablesByType.Add(type, table);
            _leases.Add(type, handle);
            _registeredNames.Add(type, name);
        }

        private static void Unregister(RevIDataTable table)
        {
            Type type = table.GetType();
            // 登记时保存的名字才是索引；业务属性之后改变或抛异常，也不能阻止卸载归还资源。
            if (_registeredNames.TryGetValue(type, out string name)) { _tables.Remove(name); }
            _registeredNames.Remove(type);
            _tablesByType.Remove(type);
        }

        /// <summary>把资源句柄里的 TextAsset 文本喂给容器；成功返回 true</summary>
        private static bool ReadText(RevIDataTable table, RevResHandle handle)
        {
            if (table == null || handle == null || !handle.IsLoaded) return false;

            if (!(handle.Content is TextAsset text)) return false;

            table.LoadText(text.text, out _);

            // ★ Bug 修复（2026-09-30）：把"空文本 = 损坏"也算失败 ——
            //   原来无条件返回 true：一个空的 TextAsset 会登记一张"永远空"的表
            //   （容器已进管理器、IsLoaded 却是 false），业务查到 Count=0 也不知道出了什么事。
            //   非空文本哪怕全是注释行，LoadText 也会标记 IsLoaded=true，不受影响。
            return table.IsLoaded;
        }
    }
}
