// ============================================================
// RevGameObjectPool.cs —— 一个 prefab 对应一条池
//
// 位置：Runtime\ObjectPool\Core\
//
// 【用 prefab 作 Key】（王者 CGameObjectPool 的做法）
//   同一个 prefab 造出来的实例共用一条池。键用 prefab 的实例 ID（int），
//   而不是旧框架的"对象名字符串"——名字会被改（instance 带 "(Clone)"、业务改名字），
//   一旦对不上，旧框架的 poolDic[obj.name] 直接抛 KeyNotFound，
//   或者更糟：两个不同路径的同名 prefab 共用了同一条池，取出来的是另一个东西。
//
// 【池端着 prefab 的资源句柄】
//   这是"基于资源加载系统"的关键：
//     · prefab 通过 RevResManager 加载 → 拿到 RevResHandle（带一份引用计数）；
//     · 池持有这个句柄 → 只要池还在，prefab 就不会被卸载（池里实例的贴图/材质才安全）；
//     · 池销毁时才 DecRef 还回去 → prefab 才有机会被卸载。
//   旧框架在这里是 Resources.Load + Instantiate，既绕过了引用计数，也没有 AB 概念。
//
// 【池节点：空闲实例挂在自己池子下面】
//   归还后挂到池节点（DontDestroyOnLoad 下），好处：
//     · 不污染业务层级（否则"已经归还的对象"还挂在 UI 节点下，布局/射线都可能扫到它）；
//     · 切场景不会把池里的对象一起带走。
//   王者为了省一点开销在正式包不挂父节点，这里选择始终挂 —— 一次 SetParent
//   相对 Instantiate/Destroy 的开销可以忽略，换来的是层级干净、行为可预期。
// ============================================================
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Revolution
{
    /// <summary>一个 prefab 一条池。业务请用 <see cref="RevPool"/>。</summary>
    internal sealed class RevGameObjectPool
    {
        private readonly RevPoolCore<GameObject> _core;
        private readonly string _nodeName;

        private Transform _root;
        private Transform _takeParent;     // 本次取出的目标父节点（OnTake 里用；Get 返回后清空）
        private bool _disposed;

        internal RevGameObjectPool(int poolId, string rootPath, string resName, RevResGroup group,
            GameObject prefab, RevResHandle handle)
        {
            PoolId = poolId;
            PathKey = RevResPathUtil.IsValidResName(resName) ? RevResPathUtil.ComputeKey(rootPath, resName) : 0UL;
            Path = RevResPathUtil.IsValidResName(resName) ? RevResPathUtil.Join(rootPath, resName) : null;
            Group = ResolveGroup(group, handle);
            Prefab = prefab;
            PrefabHandle = handle;
            PrefabInstanceId = prefab != null ? prefab.GetInstanceID() : 0;

            string tag = prefab != null ? prefab.name : (Path ?? "Unknown");
            _nodeName = "Pool-" + tag;

            _core = new RevPoolCore<GameObject>(
                name: "GameObject:" + tag,
                capacity: RevPool.MaxIdlePerPool,
                create: CreateInstance,
                isAlive: item => item != null,          // Unity 的 == 重载会把"已销毁"判成 null
                onTake: OnTake,
                onPut: OnPut,
                onDestroy: DestroyPooledItem);
        }

        // ==================== 身份与资源 ====================

        internal int PoolId { get; }

        /// <summary>
        /// 池的键 = "根目录 + 资源名"算出来的哈希（与资源系统的缓存键同源）。
        /// 0 = 这条池是用 prefab 引用建的，没有"根目录 + 资源名"。
        /// </summary>
        internal ulong PathKey { get; }

        /// <summary>拼好的完整逻辑路径（日志 / 统计展示用；用 prefab 引用建的池为 null）。</summary>
        internal string Path { get; private set; }

        /// <summary>归属的资源分组（与资源系统的 RevResGroup 一致，用于 ClearGroup 点名）。</summary>
        internal RevResGroup Group { get; private set; }

        internal GameObject Prefab { get; private set; }

        /// <summary>prefab 的实例 ID（即使 prefab 被销毁也保留，用于从注册表里摘掉这条池）。</summary>
        internal int PrefabInstanceId { get; private set; }

        /// <summary>池端着的独立 prefab 租约（销毁池时还回去）。</summary>
        internal RevResHandle PrefabHandle { get; private set; }

        internal bool HasPrefab => Prefab != null;

        internal string Name => _core.Name;

        internal int Capacity
        {
            get => _core.Capacity;
            set => _core.Capacity = value;
        }

        internal int IdleCount => _core.IdleCount;

        internal int ActiveCount => _core.ActiveCount;

        internal int RecyclingCount => _core.RecyclingCount;

        // ==================== 取 / 还 ====================

        internal GameObject Get(Transform parent)
        {
            if (_disposed)
            {
                RevPoolLog.Error($"池 \"{Name}\" 已经销毁，不能再取对象（检查一下是不是刚刚 DestroyPool 过）。");
                return null;
            }

            _takeParent = parent;
            try
            {
                return _core.Get();                // 挂父节点 / 激活在 OnTake 里做（激活前先挂好）
            }
            finally
            {
                _takeParent = null;
            }
        }

        internal bool Recycle(GameObject item, int delayFrames)
        {
            if (_disposed)
            {
                RevPoolLog.Warning($"池 \"{Name}\" 已销毁，归还的对象直接销毁：{item.name}");
                Object.Destroy(item);
                return false;
            }

            return _core.Return(item, delayFrames);
        }

        internal void Tick() => _core.Tick();

        internal int ClearIdle() => _core.ClearIdle();

        internal int Trim(int keepCount) => _core.Trim(keepCount);

        internal RevPoolStats GetStats() => _core.GetStats();

        internal void ResetStats() => _core.ResetStats();

        /// <summary>
        /// prefab 换了（原来的被外部卸载 / 销毁）：销毁旧代的空闲、延迟和借出实例，再更换 prefab 与句柄。
        /// 旧实例跟新 prefab 可能已经对不上（比如资源被重新导入过），所以宁可重建也不复用。
        /// </summary>
        internal void Rebind(GameObject prefab, RevResHandle handle, string rootPath, string resName)
        {
            _core.ClearIdle();
            // Rebind 表示资源路径对应的 prefab 已换成新版本。已经借给业务的对象仍由旧 prefab 创建，不能留给新版本继续复用。
            // 每个对象上的 PoolId 仍指向这条池；若只清空闲对象而不销毁借出对象，旧对象稍后 Return 时会被当作新 prefab 对象收回，
            // 下一次 Get 就可能拿到旧外观、旧组件或旧配置。因此换 prefab 时也销毁所有仍借出的旧代实例。
            _core.DestroyActive();
            ReleasePrefabHandle();                 // 旧句柄若还在资源缓存里，先还掉这份引用再换新

            Prefab = prefab;
            PrefabHandle = handle;
            PrefabInstanceId = prefab != null ? prefab.GetInstanceID() : 0;
            Group = ResolveGroup(Group, handle);
            if (RevResPathUtil.IsValidResName(resName)) Path = RevResPathUtil.Join(rootPath, resName);
        }

        /// <summary>销毁这条池：清空闲实例、拆掉池节点、还掉 prefab 的引用。</summary>
        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _core.ClearIdle();
            // 销毁整条池时，业务可能还持有尚未 Return 的实例；只清池内空闲对象会让这些对象留在场景里，且仍带着旧 PoolId。
            // 因此 Dispose 也销毁全部借出实例，避免池记录已消失、对象却永久遗留或之后被错误归还。
            _core.DestroyActive();

            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }

            ReleasePrefabHandle();
            Prefab = null;
        }

        // ==================== 内部 ====================

        /// <summary>
        /// 创建一个实例：Instantiate → 改名去掉 "(Clone)" → 挂身份组件 → 挂到池节点下。
        /// 返回 null 表示创建失败（池会把"取对象"的调用方拿到 null，并打错误日志）。
        /// </summary>
        private GameObject CreateInstance()
        {
            if (Prefab == null)
            {
                RevPoolLog.Error($"池 \"{Name}\" 的 prefab 已经不在了（资源被卸载 / 被销毁）。" +
                                 $"如果这是切场景后的正常现象，请在资源 Shutdown 前调 RevPool.DestroyGroup(group) 或 RevPool.DestroyAll()。");
                return null;
            }

            GameObject item = Object.Instantiate(Prefab);
            item.name = Prefab.name;                    // Unity 会给实例加 "(Clone)"，去掉便于在层级里认

            RevPooledMember member = item.GetComponent<RevPooledMember>();
            if (member == null) member = item.AddComponent<RevPooledMember>();
            member.Bind(PoolId);

            item.transform.SetParent(GetPoolNode(), false);
            return item;
        }

        private void OnTake(GameObject item)
        {
            // ★ OnPoolGet 先重置业务状态，再激活触发 OnEnable。
            Transform t = item.transform;
            if (_takeParent != null)
            {
                t.SetParent(_takeParent, false);
            }
            else
            {
                // 不指定父节点 = 交给当前活动场景（池节点是 DontDestroyOnLoad 的，
                // 留在它下面会导致"业务以为在场景里"的对象切场景不销毁、销毁池时被连带删掉）
                t.SetParent(null, false);
                Scene active = SceneManager.GetActiveScene();
                if (active.IsValid() && item.scene != active) SceneManager.MoveGameObjectToScene(item, active);
            }

            RevPooledMember member = item.GetComponent<RevPooledMember>();
            if (member != null) member.NotifyPoolGet();
            item.SetActive(true);
        }

        private void DestroyPooledItem(GameObject item)
        {
            if (item == null) return;
            RevPooledMember member = item.GetComponent<RevPooledMember>();
            if (member != null) member.Unbind(PoolId);
            Object.Destroy(item);
        }

        private void OnPut(GameObject item)
        {
            // 先在"对象还活着"的时候回调业务清理（粒子停止、计时器归零、数据清空）
            RevPooledMember member = item.GetComponent<RevPooledMember>();
            if (member != null) member.NotifyPoolReturn();

            item.SetActive(false);
            item.transform.SetParent(GetPoolNode(), false);
        }

        /// <summary>池节点（懒创建）：挂在 [RevObjectPool] 根下，编辑器里一眼能看到每条池有多少空闲对象。</summary>
        private Transform GetPoolNode()
        {
            if (_root != null) return _root;

            var node = new GameObject(_nodeName);
            _root = node.transform;
            _root.SetParent(RevPoolDriver.Root, false);
            return _root;
        }

        private static RevResGroup ResolveGroup(RevResGroup fallback, RevResHandle handle)
        {
            // 句柄上的归属才是权威（资源系统规定：分组首次确定后不再变更）
            if (handle != null && handle.Group != RevResGroup.Unknown) return handle.Group;
            return fallback;
        }

        /// <summary>
        /// 还掉池端着的那份 prefab 引用。
        /// ★ 只有它还在资源缓存里才还：若资源系统已经清过账（分组卸载 / UnloadAll），
        ///   再还一次就变成了"多还"，会把别人那份引用一起减掉。
        /// </summary>
        private void ReleasePrefabHandle()
        {
            RevResHandle handle = PrefabHandle;
            PrefabHandle = null;

            if (handle == null || handle.Key == 0) return;
            if (!ReferenceEquals(RevResManager.GetByPath(handle.StandardPath), handle)) return;

            RevResManager.DecRef(handle.Key);
        }
    }
}
