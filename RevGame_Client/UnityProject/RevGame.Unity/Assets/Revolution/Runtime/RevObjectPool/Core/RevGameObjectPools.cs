// ============================================================
// RevGameObjectPools.cs —— GameObject 池的注册表（Unity 侧）
//
// 位置：Runtime\ObjectPool\Core\
//
// 【三张索引表，分别服务三种"找池"的路径】
//   _byKey       业务传"根目录 + 资源名"时 —— 最常用。键就是资源系统的缓存键
//                （RevResPathUtil 用两段增量算出来：命中已有池时**不拼字符串、零分配**）
//   _byPrefabId  业务直接传 prefab 引用时（连哈希都省了）
//   _byPoolId    归还时（从实例身上的 RevPooledMember 读出 PoolId）
//   三张表指向同一批池对象，池不多，内存可以忽略。
//
// 【同步 Get 的前提】
//   池里已有实例、或 prefab 已经加载过 → 同步返回，一行代码搞定。
//   池是空的且 prefab 还没加载 → 走 RevResManager 同步加载：
//     · 编辑器 / PC / Android 可用；
//     · WebGL（含微信、QQ 小游戏）**同步加载不可用** → 必须用 GetAsync。
//   这一点和资源系统完全一致，报错信息里也会提醒。
//
// 【prefab 被外部卸载时自愈】
//   如果池里的 prefab 已经不在了（切场景分组卸载、被人 Destroy），
//   下次 Get 会重新走资源系统加载并 Rebind（旧实例清掉重建），而不是一直报错。
//
// 【和分组卸载配对】
//   prefab 是带分组的（RevResGroup.Battle 等），退出该域时先调 RevPool.DestroyGroup(group)，
//   销毁空闲 / 延迟 / 借出实例并归还池的 prefab 租约，再调 RevResBootstrap.Shutdown(group)。
// ============================================================
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Revolution
{
    /// <summary>GameObject / 组件池的注册表（框架内部）。业务请用 <see cref="RevPool"/>。</summary>
    internal static class RevGameObjectPools
    {
        private static readonly Dictionary<int, RevGameObjectPool> _byPoolId = new Dictionary<int, RevGameObjectPool>(32);
        private static readonly Dictionary<int, RevGameObjectPool> _byPrefabId = new Dictionary<int, RevGameObjectPool>(32);
        // 键 = 资源系统同款键（两段增量算出来的 FNV），所以这里查池不用拼字符串
        private static readonly Dictionary<ulong, RevGameObjectPool> _byKey = new Dictionary<ulong, RevGameObjectPool>(32);

        private static int _nextPoolId = 1;

        internal static int PoolCount => _byPoolId.Count;

        // ==================== 取 ====================

        /// <summary>
        /// 按"根目录 + 资源名"取一个 GameObject（池里有就复用，没有就加载 prefab 并实例化）。
        /// 命中已有池时**不拼字符串**：直接用两段算出的键查表（热路径零分配）。
        /// </summary>
        internal static GameObject Get(string rootPath, string resName, Transform parent, RevResGroup group)
        {
            if (!RevResPathUtil.IsValidResName(resName))
            {
                RevPoolLog.Error("取对象时资源名为空。");
                return null;
            }

            // ① 池已就绪（最常见的情况）→ 直接复用
            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);
            if (_byKey.TryGetValue(key, out RevGameObjectPool ready) && ready.HasPrefab)
                return ready.Get(parent);

            // ② 池不在 / prefab 被外部卸载了 → 走资源系统同步加载
            RevResHandle handle = RevResManager.LoadHandle<GameObject>(rootPath, resName, group);
            if (!handle.IsLoaded)
            {
                RevPoolLog.Error($"取对象失败：「{RevResPathUtil.Join(rootPath, resName)}」没能同步加载到 prefab（原因：{handle.ErrorReason}）。" +
                                 $"① 首次加载（真机 / AB 模式）请改用 RevPool.GetAsync；" +
                                 $"② WebGL / 小游戏平台同步加载不可用；" +
                                 $"③ 编辑器里确认「根目录 + 资源名」是否正确（根目录可用生成的 RevResPath 常量）。");
                return null;
            }

            GameObject prefab = handle.Content as GameObject;
            if (prefab == null)
            {
                RevPoolLog.Error($"「{RevResPathUtil.Join(rootPath, resName)}」加载出来的不是 GameObject（池只能池化 GameObject 预制体）。");
                ReleaseHandleIfCached(handle);
                return null;
            }

            return BindPool(key, rootPath, resName, group, prefab, handle).Get(parent);
        }

        /// <summary>按 prefab 引用取一个 GameObject（调用方已经持有 prefab，省掉路径哈希）。</summary>
        internal static GameObject Get(GameObject prefab, Transform parent)
        {
            if (prefab == null)
            {
                RevPoolLog.Error("取对象时 prefab 为 null。");
                return null;
            }

            int prefabId = prefab.GetInstanceID();

            // ★ 除了对 ID，还要对引用：Unity 的对象实例 ID 会被回收再利用，
            //   只比 ID 有可能命中一条"上一个已销毁对象"遗留的池
            if (_byPrefabId.TryGetValue(prefabId, out RevGameObjectPool pooled) && pooled.Prefab == prefab)
                return pooled.Get(parent);

            // prefab 是调用方自己持有的（不是池通过资源系统加载的），所以这里没有 RevResHandle 要端，
            // 也没有"根目录 + 资源名"，只能用 prefab 引用索引（PathKey = 0）
            RevGameObjectPool pool = new RevGameObjectPool(++_nextPoolId, null, null, RevResGroup.Unknown, prefab, null);
            Index(pool);
            RevPoolLog.Debug($"新建 GameObject 池：{pool.Name}（用 prefab 引用建，池不持有资源引用，prefab 的生命周期由业务自己保证）");
            return pool.Get(parent);
        }

        /// <summary>异步取一个 GameObject（真机首次加载 / WebGL 必须走这条）。</summary>
        internal static RevResHandle GetAsync(string rootPath, string resName, Action<GameObject> onFinished,
            Transform parent, RevResGroup group)
        {
            if (!RevResPathUtil.IsValidResName(resName))
            {
                RevPoolLog.Error("异步取对象时资源名为空。");
                InvokeGetCallback(onFinished, null);
                return RevResHandle.Empty;
            }

            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);

            // 池已经加载好 prefab 时，不必再异步读取资源，可以马上从池里取对象并回调。
            // 返回的 handle 是本次调用自己的资源引用：调用方以后可以 DecRef；池需要的长期引用由池自己另行持有，不能共用这一份。
            if (_byKey.TryGetValue(key, out RevGameObjectPool ready) && ready.HasPrefab)
            {
                RevResHandle callerLease = RevResManager.Get(rootPath, resName);
                GameObject item = ready.Get(parent);
                InvokeGetCallback(onFinished, item);
                return callerLease;
            }

            // 池还没有可用 prefab 时，请资源系统异步加载。LoadAsync 会把资源使用计数加 1，这一份先归发起请求的调用方。
            // 如果这次请求负责新建池，还要额外 AddRef 一次给池长期持有；否则调用方完成后 DecRef，池会失去之后复制 prefab 所需的资源。
            return RevResManager.LoadAsync(rootPath, resName, typeof(GameObject), handle =>
            {
                GameObject prefab = handle != null ? handle.Get<GameObject>() : null;
                if (prefab == null)
                {
                    RevPoolLog.Error($"异步取对象失败：「{RevResPathUtil.Join(rootPath, resName)}」加载不到 prefab" +
                                     $"（原因：{(handle != null ? handle.ErrorReason : RevResLoadErrorReason.None)}）。");
                    InvokeGetCallback(onFinished, null);
                    return;
                }

                // 异步加载期间其他请求可能先完成并创建了同一条池。此时新请求直接从现成池取对象，
                // 本次 LoadAsync 增加的资源引用仍属于调用方，不能转交或丢给池；调用方用完需自行归还这份引用。
                if (_byKey.TryGetValue(key, out RevGameObjectPool existing) && existing.Prefab == prefab)
                {
                    InvokeGetCallback(onFinished, existing.Get(parent));
                    return;
                }

                RevResHandle poolLease = handle;
                if (!RevResManager.IsCurrent(poolLease) || poolLease.Content != prefab)
                {
                    // 资源可能在加载过程中被 Shutdown/UnloadAll 移出缓存；这时 handle 已不是当前有效租约。
                    // 若仍 AddRef 或交给池保存，就会出现没有资源系统记录对应的“孤儿引用”，之后既无法正确统计也无法配对释放，所以不让池接管它。
                    RevPoolLog.Warning($"「{RevResPathUtil.Join(rootPath, resName)}」加载完成后资源缓存已不再持有同一句柄，池将不持有它的引用。");
                    poolLease = null;
                }
                else
                {
                    // 调用方的类型化视图只代表它自己的一份租约；池的额外持有必须记录底层原句柄。
                    poolLease = poolLease.ReferenceOwner ?? poolLease;
                    RevResManager.AddRef(poolLease.Key);
                }

                InvokeGetCallback(onFinished, BindPool(key, rootPath, resName, group, prefab, poolLease).Get(parent));
            }, group);
        }

        // ==================== 还 ====================

        /// <summary>归还一个 GameObject。</summary>
        internal static bool Return(GameObject item, int delayFrames)
        {
            if (item == null)
            {
                RevPoolLog.Warning("归还了 null 或已销毁的 GameObject，已忽略。");
                return false;
            }

            RevPooledMember member = item.GetComponent<RevPooledMember>();

            if (member == null || member.PoolId == 0)
            {
                RevPoolLog.Error($"这个 GameObject 不是从对象池取出来的，已忽略归还：{item.name}。" +
                                 $"要么它本来就是你自己 Instantiate 的（那就自己 Destroy），" +
                                 $"要么它是被另存的预制体实例（池实例不该另存为预制体）。");
                return false;
            }

            if (!_byPoolId.TryGetValue(member.PoolId, out RevGameObjectPool pool))
            {
                // 池已经销毁了（DestroyPool / 域重载）→ 兜底销毁，不留孤儿
                RevPoolLog.Warning($"对象所属的池已经不存在了，直接销毁它：{item.name}");
                UnityEngine.Object.Destroy(item);      // ★ 必须写全名：本文件同时 using System 与 UnityEngine，Object 会歧义
                return false;
            }

            return pool.Recycle(item, delayFrames);
        }

        /// <summary>归还一个组件（等价于归还它所在的 GameObject）。</summary>
        internal static bool Return(Component component, int delayFrames)
            => Return(component != null ? component.gameObject : null, delayFrames);

        // ==================== 驱动 ====================

        /// <summary>推进所有池的延迟回收（由 <see cref="RevPoolDriver"/> 每帧调用）。返回推进了几条池。</summary>
        internal static int Tick()
        {
            if (_byPoolId.Count == 0) return 0;

            int count = 0;
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++)
            {
                if (pools[i].RecyclingCount == 0) continue;
                pools[i].Tick();
                count++;
            }

            return count;
        }

        // ==================== 清理 ====================

        /// <summary>清空某条池的空闲实例（池保留）。返回销毁数量。</summary>
        internal static int Clear(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return 0;

            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);
            return _byKey.TryGetValue(key, out RevGameObjectPool pool) ? pool.ClearIdle() : 0;
        }

        /// <summary>清空所有池的空闲实例（池保留）。返回销毁总数。</summary>
        internal static int ClearAll()
        {
            int removed = 0;
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++) removed += pools[i].ClearIdle();

            return removed;
        }

        /// <summary>清空某资源分组下各池的空闲实例，池和 prefab 租约仍保留。</summary>
        internal static int ClearGroup(RevResGroup group)
        {
            int removed = 0;
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++)
            {
                if (pools[i].Group != group) continue;
                removed += pools[i].ClearIdle();
            }

            return removed;
        }

        /// <summary>销毁某资源组全部池（包括当前借出的实例），并归还各池持有的 prefab 句柄。</summary>
        internal static int DestroyGroup(RevResGroup group)
        {
            int destroyed = 0;
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++)
            {
                RevGameObjectPool pool = pools[i];
                if (pool.Group != group) continue;
                destroyed += pool.ActiveCount + pool.IdleCount + pool.RecyclingCount;
                Unindex(pool);
                pool.Dispose();
            }
            return destroyed;
        }

        /// <summary>按"根目录 + 资源名"销毁整条池（空闲实例销毁 + 还掉 prefab 引用）。</summary>
        internal static bool DestroyPool(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return false;

            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);
            if (!_byKey.TryGetValue(key, out RevGameObjectPool pool)) return false;

            Unindex(pool);
            pool.Dispose();
            RevPoolLog.Debug($"销毁 GameObject 池：{pool.Name}");
            return true;
        }

        /// <summary>按 prefab 引用销毁整条池。</summary>
        internal static bool DestroyPool(GameObject prefab)
        {
            // Unity 对象被 Destroy 后看起来等于 null，但 C# 里的托管包装对象有时还在，且还保留原 InstanceID。
            // 先用 ReferenceEquals 只拦真正的 C# null，才能从索引表找到并清理这个已销毁 prefab 原来对应的池；普通 prefab == null 检查会错过它。
            if (ReferenceEquals(prefab, null)) return false;

            int prefabId;
            try { prefabId = prefab.GetInstanceID(); }
            catch (MissingReferenceException) { return false; }
            if (!_byPrefabId.TryGetValue(prefabId, out RevGameObjectPool pool)) return false;
            if (pool.Prefab != prefab && !(pool.Prefab == null && prefab == null)) return false;

            Unindex(pool);
            pool.Dispose();
            return true;
        }

        /// <summary>销毁全部池（清实例 + 还引用）。回登录 / 退出时用。</summary>
        internal static void DestroyAll()
        {
            List<RevGameObjectPool> pools = Snapshot();

            _byPoolId.Clear();
            _byPrefabId.Clear();
            _byKey.Clear();

            for (int i = 0; i < pools.Count; i++) pools[i].Dispose();
        }

        /// <summary>统一设置所有池（含以后新建的池）的空闲上限。</summary>
        internal static void ApplyCapacityToAll(int capacity)
        {
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++)
            {
                pools[i].Capacity = capacity;
                if (capacity > 0) pools[i].Trim(capacity);   // 0 = 不限，不能按 0 裁剪（会把空闲清空）
            }
        }

        /// <summary>每条池最多留 keepCount 个空闲实例，其余销毁。返回销毁总数。</summary>
        internal static int TrimAll(int keepCount)
        {
            int removed = 0;
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++) removed += pools[i].Trim(keepCount);

            return removed;
        }

        // ==================== 统计 ====================

        internal static RevPoolStats GetStats(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return default;
            return _byKey.TryGetValue(RevResPathUtil.ComputeKey(rootPath, resName), out RevGameObjectPool pool)
                ? pool.GetStats()
                : default;
        }

        /// <summary>把某条池的请求计数清零。</summary>
        internal static void ResetStats(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return;

            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);
            if (_byKey.TryGetValue(key, out RevGameObjectPool pool)) pool.ResetStats();
        }

        /// <summary>把所有池（含引用对象池）的请求计数清零。</summary>
        internal static void ResetStatsAll()
        {
            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++) pools[i].ResetStats();

            RevRefPools.ResetStatsAll();
        }

        internal static RevPoolStats GetGlobalStats()
        {
            var total = new RevPoolStats();

            List<RevGameObjectPool> pools = Snapshot();
            for (int i = 0; i < pools.Count; i++)
            {
                RevPoolStats one = pools[i].GetStats();
                total.Created += one.Created;
                total.Destroyed += one.Destroyed;
                total.Lost += one.Lost;
                total.GetCount += one.GetCount;
                total.Hit += one.Hit;
                total.Miss += one.Miss;
                total.ReturnCount += one.ReturnCount;
                total.IdleCount += one.IdleCount;
                total.ActiveCount += one.ActiveCount;
                total.Recycling += one.Recycling;
            }

            return total;
        }

        /// <summary>所有池名（排序，便于对比两次快照）。调试用，会分配。</summary>
        internal static List<string> GetPoolNames()
        {
            List<RevGameObjectPool> pools = Snapshot();
            var names = new List<string>(pools.Count);
            for (int i = 0; i < pools.Count; i++) names.Add(pools[i].Name);

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>把所有池的统计打成一坨文本（排查用）。</summary>
        internal static string DumpStats()
        {
            var sb = new StringBuilder(512);

            List<RevGameObjectPool> pools = Snapshot();
            pools.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            sb.Append("[RevPool] GameObject 池 ").Append(pools.Count)
              .Append(" 条，空闲合计 ").Append(GetGlobalStats().IdleCount).Append(" 个\n");

            for (int i = 0; i < pools.Count; i++)
                sb.Append("  ").Append(pools[i].Name).Append("（路径 ").Append(pools[i].Path ?? "<prefab 引用>")
                  .Append("）：").Append(pools[i].GetStats()).Append('\n');

            sb.Append("[RevPool] 引用对象池 ").Append(RevRefPools.PoolCount)
              .Append(" 条：").Append(RevRefPools.GetGlobalStats());

            return sb.ToString();
        }

        // ==================== 内部 ====================

        private static void InvokeGetCallback(Action<GameObject> callback, GameObject item)
        {
            if (callback == null) return;
            try { callback(item); }
            catch (Exception e) { RevPoolLog.Error($"RevPool.GetAsync 回调异常（已隔离）：{e}"); }
        }

        private static RevGameObjectPool BindPool(ulong key, string rootPath, string resName,
            RevResGroup group, GameObject prefab, RevResHandle handle)
        {
            if (_byKey.TryGetValue(key, out RevGameObjectPool old))
            {
                // 同一路径可能重新加载出新的 prefab 实例（例如旧资源卸载后又加载，或热更新替换了资源）。
                // 这时保留旧对象会让新请求取到旧版本，所以 Rebind 会清理旧实例，再把池改绑到新 prefab。
                // 改绑后还要从索引表删除旧 prefab 的 InstanceID；否则旧 prefab 引用以后来销毁池时，仍会误命中这条已经属于新 prefab 的池。
                int oldPrefabId = old.PrefabInstanceId;
                old.Rebind(prefab, handle, rootPath, resName);
                if (oldPrefabId != 0 && oldPrefabId != old.PrefabInstanceId &&
                    _byPrefabId.TryGetValue(oldPrefabId, out RevGameObjectPool indexed) && ReferenceEquals(indexed, old))
                    _byPrefabId.Remove(oldPrefabId);
                _byPrefabId[prefab.GetInstanceID()] = old;
                return old;
            }

            RevGameObjectPool pool = new RevGameObjectPool(++_nextPoolId, rootPath, resName, group, prefab, handle);
            Index(pool);
            RevPoolLog.Debug($"新建 GameObject 池：{pool.Name}（分组 {pool.Group}）");
            return pool;
        }

        private static void Index(RevGameObjectPool pool)
        {
            _byPoolId[pool.PoolId] = pool;
            if (pool.PrefabInstanceId != 0) _byPrefabId[pool.PrefabInstanceId] = pool;
            if (pool.PathKey != 0) _byKey[pool.PathKey] = pool;
        }

        private static void Unindex(RevGameObjectPool pool)
        {
            _byPoolId.Remove(pool.PoolId);
            if (pool.PrefabInstanceId != 0) _byPrefabId.Remove(pool.PrefabInstanceId);
            if (pool.PathKey != 0) _byKey.Remove(pool.PathKey);
        }

        private static List<RevGameObjectPool> Snapshot()
        {
            var list = new List<RevGameObjectPool>(_byPoolId.Count);
            foreach (KeyValuePair<int, RevGameObjectPool> pair in _byPoolId) list.Add(pair.Value);

            return list;
        }

        /// <summary>还掉一份"加载了但没用上"的引用（例如加载出来不是 GameObject）。</summary>
        private static void ReleaseHandleIfCached(RevResHandle handle)
        {
            if (handle == null || handle.Key == 0) return;
            if (!ReferenceEquals(RevResManager.GetByPath(handle.StandardPath), handle)) return;

            RevResManager.DecRef(handle.Key);
        }
    }
}
