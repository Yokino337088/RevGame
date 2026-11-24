// ============================================================
// RevResManager.cs —— 资源管理器（统一门面，业务唯一入口）
//
// 位置：Runtime\资源加载\
//
// 【它把多个旧 Manager 的职责收了回来】
//   业务永远只写 RevResManager.Load<T>(根目录, 资源名)，
//   "从哪加载"由策略自动决定，缓存与引用计数全局只有一套。
//
// 【两个参数：根目录 + 资源名】
//     RevResManager.Load<Sprite>(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
//   · 根目录：资源所在的文件夹（用生成的 RevResPath 常量，如 "UI/Icon/"，带结尾斜杠）
//   · 资源名：该文件夹下的资源名（不带扩展名）
//   框架内部仍是**一条完整逻辑路径** "UI/Icon/Hero_1001"（映射表 ResMap / AB 包 / 句柄都认它），
//   两段合成一条的逻辑与"不拼字符串直接算键"的等价性由 RevResPathUtil 保证 ——
//   所以缓存命中的路径上零字符串分配（详见 RevResPathUtil 文件头）。
//
// 【内部只有三张表】
//   _policies —— 策略族（注册顺序即优先级）
//   _cache    —— 正在使用的资源：Key → 句柄
//   _unused   —— 引用归零、等待延迟释放的资源（避免频繁装卸）
//
// 【调用链】
//   Load → 算 Key → 查缓存 → 责任链选策略（失败可兜底）→ 建句柄 → 加载 → 入缓存 → 返回
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    public static partial class RevResManager
    {
        // ===== 策略族（注册顺序 = 优先级）=====
        private static readonly List<IRevResPolicy> _policies = new List<IRevResPolicy>();

        // ===== 缓存：Key → 句柄 =====
        private static readonly Dictionary<ulong, RevResHandle> _cache = new Dictionary<ulong, RevResHandle>();

        // ===== 加载中的业务回调：一条策略链（含 fallback）只在最终结果时通知所有请求方 =====
        private static readonly Dictionary<RevResHandle, List<Action<RevResHandle>>> _pendingAsyncCallbacks =
            new Dictionary<RevResHandle, List<Action<RevResHandle>>>();

        // ===== 未使用表：引用计数归零、等待延迟释放 =====
        private static readonly Dictionary<ulong, RevResHandle> _unused = new Dictionary<ulong, RevResHandle>();

        private static RevABResPolicy _abPolicy;

        public static IReadOnlyDictionary<ulong, RevResHandle> CachedHandles => _cache;
        public static int CachedCount => _cache.Count;
        public static int UnusedCount => _unused.Count;

        // ==================== 初始化 ====================

        public static void RegisterPolicy(IRevResPolicy policy)
        {
            if (policy == null) return;

            _policies.Add(policy);
            if (policy is RevABResPolicy ab) _abPolicy = ab;
        }

        public static void ClearAllPolicies()
        {
            _policies.Clear();
            _abPolicy = null;
        }

        // ==================== 归属分组（只影响卸载，不影响加载）====================

        /// <summary>
        /// 确定句柄的归属分组：**只在"尚未归属"时补齐，一旦归属就不再变更**。
        ///
        /// 规则：
        ///   · 传 Unknown            → 不动（不覆盖已有归属，也不做任何标记）
        ///   · 已有明确归属           → 不动（谁先定的算谁的）
        ///   · 当前是 Unknown 且传了  → 补上（覆盖 Unknown 是唯一的例外）
        ///
        /// 【为什么要允许"补齐 Unknown"？】
        ///   预加载常常不传分组（RevResPreloader.Preload 的 group 默认 Unknown），
        ///   等业务真正 Load 时再补上分组，句柄才能被整组卸载点名到。
        ///
        /// 【为什么不允许覆盖已有归属？】
        ///   见 RevResHandle.Group 的注释：覆盖会让先加载的分组 Shutdown 时点名不到它。
        /// </summary>
        /// <summary>
        /// ★ 作用域兜底：调用方没传分组（Unknown）时，看看有没有"当前域"（RevScope）并且它配置了回收分组，有就用它。
        /// 这是资源和对象池共用的"唯一漏斗"：对象池取 prefab 也是走 RevResManager.Load，所以池也自动归域。
        /// 没有域时 DefaultGroup 恒为 Unknown，返回值与传入值一致 —— 行为与没有作用域系统时逐字一致。
        /// </summary>
        private static RevResGroup EffectiveGroup(RevResGroup group)
            => group == RevResGroup.Unknown ? RevScope.DefaultGroup : group;

        private static void AssignGroup(RevResHandle handle, RevResGroup group)
        {
            if (handle == null) return;

            // 规则不变：只在"句柄还没有归属"时才补，已有归属的永远不动（不会把别人先定好的组抢过来）。
            group = EffectiveGroup(group);

            if (group == RevResGroup.Unknown) return;                    // 没传 → 不动
            if (handle.Group != RevResGroup.Unknown) return;             // 已有归属 → 不动（关键）
            handle.Group = group;                                     // 唯一允许的写入时机
        }

        // ==================== 同步加载 ====================

        /// <summary>
        /// 直接取得资源对象。引用默认归当前域，长期持有者可显式选择 Manual。
        /// 例：Sprite icon = RevResManager.Load&lt;Sprite&gt;(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
        /// 注意：失败时返回 null；要拿失败原因请用 LoadHandle&lt;T&gt;（或下面返回 RevResHandle 的重载）。
        /// </summary>
        public static T Load<T>(string rootPath, string resName, RevResGroup group = RevResGroup.Unknown, RevResLifetime lifetime = RevResLifetime.Automatic)
            where T : UnityEngine.Object
        {
            return Load(rootPath, resName, typeof(T), group, lifetime).Content as T;
        }

        /// <summary>
        /// 同步取得资源句柄和一份引用。默认由有效域归还，失败原因可从句柄读取。
        ///
        ///     RevResHandle h = RevResManager.LoadHandle&lt;Sprite&gt;(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
        ///     if (!h.IsLoaded) Debug.LogError($"失败：{h.ErrorReason}");
        /// </summary>
        public static RevResHandle LoadHandle<T>(string rootPath, string resName, RevResGroup group = RevResGroup.Unknown, RevResLifetime lifetime = RevResLifetime.Automatic)
            where T : UnityEngine.Object
            => Load(rootPath, resName, typeof(T), group, lifetime);

        /// <summary>
        /// 同步加载并登记本次持有。缓存和域账本已有该资源时，不构造路径字符串或单次引用对象。
        /// </summary>
        public static RevResHandle Load(string rootPath, string resName, Type contentType, RevResGroup group = RevResGroup.Unknown, RevResLifetime lifetime = RevResLifetime.Automatic)
        {
            RevScopeHandle scope = ResourceScope(lifetime);
            RevResHandle handle = LoadCore(rootPath, resName, contentType, group);
            TrackScopeReference(scope, handle);
            return handle;
        }

        /// <summary>完成实际加载或缓存命中。引用归属由公开入口登记。</summary>
        private static RevResHandle LoadCore(string rootPath, string resName, Type contentType, RevResGroup group)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return RevResHandle.Empty;

            // 命中与冷路径复用同一个键。仅创建新句柄时才需要完整字符串。
            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);
            if (TryHitCache(key, contentType, group, out RevResHandle hit)) return hit;

            return LoadUncached(key, RevResPathUtil.Join(rootPath, resName), contentType, group);
        }

        /// <summary>单次查缓存。有效句柄取得引用，已归零的失败条目撤掉后允许重试。</summary>
        private static bool TryHitCache(ulong key, Type contentType, RevResGroup group, out RevResHandle handle)
        {
            if (_cache.TryGetValue(key, out RevResHandle cached))
            {
                // 失败缓存有持有者时保留回执，归零后下一次请求应能尝试新资源。
                if (!cached.IsLoaded && !cached.IsLoading && cached.RefCount <= 0)
                {
                    DiscardFailedHandle(key, cached);
                    handle = null;
                    return false;
                }
                // 缓存表用资源路径查找，不把请求的 C# 类型也放进 key。同一路径如果已经按 Sprite 加载，后来又按 Texture 请求，
                // 不检查类型就会错误复用 Sprite 句柄，业务拿到的 Content as Texture 只会变成 null，看不出真正原因。
                // 返回一个独立的 TypeMismatch 失败句柄，并且不增加原资源的引用数；这样既明确报错，也不影响正确请求的资源寿命。
                if (!CanSatisfyType(cached, contentType))
                {
                    handle = CreateTypeMismatchHandle(cached.StandardPath, contentType, cached.ContentType);
                    return true;
                }

                cached.RefCount++;
                cached.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                _unused.Remove(key);
                cached.Touch();
                AssignGroup(cached, group);
                handle = cached;
                return true;
            }

            handle = null;
            return false;
        }

        private static bool CanSatisfyType(RevResHandle cached, Type requestedType)
        {
            if (cached == null || requestedType == null) return false;
            if (cached.IsLoaded && cached.Content != null) return requestedType.IsInstanceOfType(cached.Content);
            return cached.ContentType != null && requestedType.IsAssignableFrom(cached.ContentType);
        }

        private static RevResHandle CreateTypeMismatchHandle(string path, Type requestedType, Type cachedType)
        {
            var failed = new RevResHandle
            {
                Key = 0,
                StandardPath = path,
                ContentType = requestedType,
                RefCount = 0,
                ErrorReason = RevResLoadErrorReason.TypeMismatch
            };
            failed.MarkError();
            RevLog.Warn($"资源 {path} 已按 {cachedType?.Name ?? "未知类型"} 缓存，不能以 {requestedType?.Name ?? "未知类型"} 复用同一缓存键。", "Res");
            return failed;
        }

        private static void DiscardFailedHandle(ulong key, RevResHandle handle)
        {
            if (!_cache.TryGetValue(key, out RevResHandle current) || !ReferenceEquals(current, handle)) return;
            ReleaseBundleOf(handle);
            _cache.Remove(key);
            _unused.Remove(key);
            handle.RemoveFlag(RevResInstanceFlag.MarkedUnused);
        }

        /// <summary>使用已知未命中的键按策略加载，成功或失败只登记一次句柄。</summary>
        private static RevResHandle LoadUncached(ulong key, string standardPath, Type contentType, RevResGroup group)
        {
            // 按优先级依次尝试策略。第一个接单策略才创建新句柄。
            RevResHandle handle = null;

            for (int i = 0; i < _policies.Count; i++)
            {
                IRevResPolicy policy = _policies[i];
                if (!policy.Match(standardPath)) continue;      // 不归它管 → 问下一条

                // 首次命中才创建句柄（避免为失败的尝试白建对象）
                if (handle == null)
                {
                    handle = new RevResHandle
                    {
                        Key = key,
                        StandardPath = standardPath,
                        ContentType = contentType,
                        Group = EffectiveGroup(group),             // ★ 新建句柄也要过作用域兜底（见 EffectiveGroup）
                        RefCount = 1,
                        Flags = RevResInstanceFlag.NeedCache
                    };
                    handle.Touch();                  // 记录首次使用时间
                }

                if (TryLoadWithPolicy(policy, handle, out RevResLoadErrorReason err))
                    break;                                      // 加载成功 → 结束

                handle.ErrorReason = err;                       // 记下失败原因（以最后一次为准）
                if (!policy.AllowFallback) break;               // 不允许兜底 → 就此定案
            }

            // ③ 没有任何策略匹配 → 空对象
            if (handle == null) return RevResHandle.Empty;

            // ④ 入缓存（失败也入，避免每帧重复尝试同一个坏资源）
            if (!handle.IsLoaded) handle.MarkError();
            _cache[key] = handle;
            return handle;
        }

        /// <summary>用指定策略尝试加载一次；失败时通过 err 说明原因</summary>
        private static bool TryLoadWithPolicy(IRevResPolicy policy, RevResHandle handle, out RevResLoadErrorReason err)
        {
            if (!TryResolvePath(policy, handle))
            {
                err = RevResLoadErrorReason.PathNotMapped;
                return false;
            }

            // 2) 交给策略对应的加载器读取文件或 AssetBundle。自定义 Loader 可能因路径、磁盘或 AB 状态异常而抛错，
            //    若让异常直接传到游戏业务，调用方会拿不到正常的失败句柄，已经增加的资源引用也难以按统一流程归还。
            //    因此在这里记录异常并把它转换成 BundleLoadFail；后续仍由资源系统缓存和释放这个失败句柄。
            handle.MarkLoading();
            object content;
            try { content = policy.CreateLoader().Load(handle, out err); }
            catch (Exception e)
            {
                err = RevResLoadErrorReason.BundleLoadFail;
                RevLog.Exception(e, $"同步资源加载器异常：{handle.StandardPath}", "Res");
                return false;
            }
            if (content == null)
            {
                return false;
            }

            // 3) 自定义加载器可以返回托管包装器仍存在的 Unity 对象。由句柄校验原生对象，再决定是否切换策略。
            handle.ErrorReason = RevResLoadErrorReason.None;
            handle.SetContent(content);
            if (!handle.IsLoaded)
            {
                err = handle.ErrorReason;
                return false;
            }
#if UNITY_EDITOR
            if (policy is RevEditorResPolicy)
            {
                handle.AddFlag(RevResInstanceFlag.LoadFromEditor);
            }
#endif
            return true;
        }

        // ==================== 异步加载 ====================

        /// <summary>
        /// 异步加载。有效域关闭时撤销它的回调和引用，共享读取继续服务其它请求。
        /// 缓存命中时，在调用同步回调前完成归属登记。
        /// </summary>
        public static RevResHandle LoadAsync(string rootPath, string resName, Type contentType,
            Action<RevResHandle> onFinished, RevResGroup group = RevResGroup.Unknown,
            RevResLoadPriority priority = RevResLoadPriority.Normal, RevResLifetime lifetime = RevResLifetime.Automatic)
        {
            return LoadInScope(rootPath, resName, contentType, onFinished, group, priority, ResourceScope(lifetime));
        }

        /// <summary>在模块已经解析好的域下取得引用。使对象池冷开与缓存命中使用相同的归属规则。</summary>
        internal static RevResHandle LoadInScope(string rootPath, string resName, Type contentType,
            Action<RevResHandle> onFinished, RevResGroup group, RevResLoadPriority priority, RevScopeHandle scope)
        {
            if (!RevResPathUtil.IsValidResName(resName))
            {
                InvokeFinished(onFinished, RevResHandle.Empty, scope);
                return RevResHandle.Empty;
            }

            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);

            // ① 缓存已就绪：类型安全后立即回调（零字符串分配）
            if (_cache.TryGetValue(key, out RevResHandle ready) && ready.IsLoaded)
            {
                if (!CanSatisfyType(ready, contentType))
                {
                    RevResHandle mismatch = CreateTypeMismatchHandle(ready.StandardPath, contentType, ready.ContentType);
                    InvokeFinished(onFinished, mismatch, scope);
                    return mismatch;
                }

                ready.RefCount++;
                ready.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                _unused.Remove(key);
                ready.Touch();
                AssignGroup(ready, group);
                TrackScopeReference(scope, ready);
                InvokeFinished(onFinished, ready, scope);
                return ready;
            }

            // 传递本次已经查到的缓存项。已有在途请求用其完整路径，无须再拼一次或重算键。
            if (scope == null || onFinished == null)
            {
                RevResHandle handle = LoadAsyncRequest(key, ready, rootPath, resName, contentType, onFinished, group, priority);
                TrackScopeReference(scope, handle);
                return handle;
            }
            var request = new ScopedLoadCallback(scope, onFinished);
            RevResHandle result = LoadAsyncRequest(key, ready, rootPath, resName, contentType, request.Finish, group, priority);
            request.Register(result);
            return result;
        }

        /// <summary>在主线程复用或重试已查到的句柄。确实发起新的读取时才创建新路径与句柄。</summary>
        private static RevResHandle LoadAsyncRequest(ulong key, RevResHandle cached, string rootPath, string resName, Type contentType,
            Action<RevResHandle> onFinished, RevResGroup group,
            RevResLoadPriority priority)
        {
            // ① 缓存命中：成功句柄直接复用；在途句柄合并回调。
            //    已完成失败句柄若引用归零，则丢弃失败项并重新尝试（例如 UI 资源路径修正后再次打开）。
            if (cached != null)
            {
                if (!cached.IsLoaded && !cached.IsLoading && cached.RefCount <= 0)
                {
                    DiscardFailedHandle(key, cached);
                }
                else if (cached.IsLoading && cached.ContentType == typeof(UnityEngine.Object) && contentType != null && contentType != typeof(UnityEngine.Object))
                {
                    return JoinWeakTypedRequest(cached, contentType, onFinished, group);
                }
                // 即使资源还在加载，也要比较请求类型：路径表只按目录和文件名区分，不同类型会共享同一个在途句柄。
                // 如果一个请求要 Sprite、另一个要不兼容的 Texture，合并后第二个回调拿到的内容转换会变成 null，且加载成功状态会掩盖错误。
                // 所以类型不兼容时立即回 TypeMismatch，不把新请求加入旧任务，也不增加旧资源的引用数。
                else if (!CanSatisfyType(cached, contentType))
                {
                    RevResHandle mismatch = CreateTypeMismatchHandle(cached.StandardPath, contentType, cached.ContentType);
                    InvokeFinished(onFinished, mismatch);
                    return mismatch;
                }
                else
                {
                    cached.RefCount++;
                    cached.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                    _unused.Remove(key);
                    cached.Touch();
                    AssignGroup(cached, group);

                    if (cached.IsLoaded || !cached.IsLoading)
                    {
                        InvokeFinished(onFinished, cached);
                        return cached;
                    }

                    AddPendingCallback(cached, onFinished);
                    return cached;
                }
            }

            // ② 建实体并"先入缓存"——异步要先登记句柄，后续同 key 请求才能合并。
            var handle = new RevResHandle
            {
                Key = key,
                StandardPath = RevResPathUtil.Join(rootPath, resName),
                ContentType = contentType,
                Group = EffectiveGroup(group),                     // ★ 新建句柄也要过作用域兜底（见 EffectiveGroup）
                RefCount = 1,
                Flags = RevResInstanceFlag.NeedCache | RevResInstanceFlag.InAsyncLoading
            };
            handle.MarkLoading();
            _cache[key] = handle;
            AddPendingCallback(handle, onFinished);

            // ③ 从第 0 条策略开始走"责任链"（失败会自动换下一条兜底）。
            TryLoadAsyncFrom(handle, 0, priority);
            return handle;
        }

        /// <summary>复用弱类型在途请求。完成后给具体类型视图共享已取得的地址和资源。</summary>
        private static RevResHandle JoinWeakTypedRequest(RevResHandle owner, Type requestedType, Action<RevResHandle> callback, RevResGroup group)
        {
            owner.RefCount++;
            owner.RemoveFlag(RevResInstanceFlag.MarkedUnused);
            _unused.Remove(owner.Key);
            AssignGroup(owner, group);
            var view = new RevResHandle
            {
                Key = owner.Key, StandardPath = owner.StandardPath, ContentType = requestedType,
                Group = owner.Group, RefCount = 1, ReferenceOwner = owner
            };
            view.MarkLoading();
            // 保留 Object 的底层加载类型，完成后才校验真实对象；一个错误类型请求不能污染其它等待者。
            AddPendingCallback(owner, result =>
            {
                view.CopyResolvedPathFrom(result);
                view.ErrorReason = result.ErrorReason;
                if (result.IsLoaded && !requestedType.IsInstanceOfType(result.Content))
                {
                    view.ErrorReason = RevResLoadErrorReason.TypeMismatch;
                    view.MarkError();
                }
                else if (result.IsLoaded) { view.SetContent(result.Content); }
                else { view.MarkError(); }
                InvokeFinished(callback, view);
            });
            return view;
        }

        /// <summary>
        /// 异步加载（泛型版）。调用域结束时撤销回调，跨域持有使用显式租约。
        ///
        ///     RevResManager.LoadAsync&lt;Sprite&gt;(RevResPath.UI_Icon, "Hero_1001", sprite =&gt;
        ///     {
        ///         if (sprite == null) return;      // 失败：回调收到的就是 null
        ///         image.sprite = sprite;
        ///     }, RevResGroup.UI);
        ///
        /// 【失败怎么查原因】回调里拿到 null 时，用返回值那个句柄（或 RevResManager.Get(rootPath, resName)）看 ErrorReason。
        /// 【和下面那个重载的区别】这个只把"内容"给你（最常见需求）；需要区分"加载中/失败/类型不符"
        ///   这类细节时，用返回 RevResHandle 的重载拿完整句柄。
        /// </summary>
        public static RevResHandle LoadAsync<T>(string rootPath, string resName, Action<T> onFinished,
            RevResGroup group = RevResGroup.Unknown, RevResLoadPriority priority = RevResLoadPriority.Normal,
            RevResLifetime lifetime = RevResLifetime.Automatic)
            where T : UnityEngine.Object
        {
            return LoadAsync(rootPath, resName, typeof(T),
                onFinished == null ? null : handle => onFinished(handle.Content as T), group, priority, lifetime);
        }

        /// <summary>从指定策略继续异步尝试。成功、取消和失败依照原策略结束或继续回退。</summary>
        private static void TryLoadAsyncFrom(RevResHandle handle, int startIndex, RevResLoadPriority priority)
        {
            for (int i = startIndex; i < _policies.Count; i++)
            {
                IRevResPolicy policy = _policies[i];
                if (!policy.Match(handle.StandardPath)) continue;

                if (!TryResolvePath(policy, handle))
                {
                    handle.ErrorReason = RevResLoadErrorReason.PathNotMapped;
                    handle.MarkError();
                    if (policy.AllowFallback) continue;
                    CompleteAsync(handle);
                    return;
                }

                handle.MarkLoading();
                handle.ErrorReason = RevResLoadErrorReason.None;

                int next = i + 1;
                RevAsyncLoadPump.Submit(handle, policy.CreateLoader(), h =>
                {
                    // 取消是终态，不能继续走 Resources fallback；非取消失败才走下一策略。
                    if (h.ErrorReason == RevResLoadErrorReason.Cancelled)
                    {
                        CompleteAsync(h);
                        return;
                    }

                    if (h.IsLoaded || !policy.AllowFallback)
                    {
                        CompleteAsync(h);
                        return;
                    }

                    TryLoadAsyncFrom(handle, next, priority);
                }, (int)priority);
                return;
            }

            handle.ErrorReason = RevResLoadErrorReason.PolicyNotFound;
            handle.MarkError();
            CompleteAsync(handle);
        }

        /// <summary>内置 AB 策略直传共享地址，其他策略继续根据公开接口返回路径文本。</summary>
        private static bool TryResolvePath(IRevResPolicy policy, RevResHandle handle)
        {
            if (policy is RevABResPolicy bundlePolicy && bundlePolicy.UsesResolvedMap)
            {
                return bundlePolicy.TryResolveAddress(handle);
            }
            string path = policy.MapPath(handle.StandardPath, handle.ContentType);
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            handle.RealPath = path;
            return true;
        }

        private static void AddPendingCallback(RevResHandle handle, Action<RevResHandle> callback)
        {
            if (handle == null || callback == null) return;
            if (!_pendingAsyncCallbacks.TryGetValue(handle, out List<Action<RevResHandle>> callbacks))
                _pendingAsyncCallbacks[handle] = callbacks = new List<Action<RevResHandle>>(2);
            callbacks.Add(callback);
        }

        /// <summary>结束共享读包，提交等待回收的零引用代次，逐个分发仍有效的请求。</summary>
        private static void CompleteAsync(RevResHandle handle)
        {
            if (handle == null || handle.Key == 0) return;
            OnAsyncLoaded(handle.Key, handle);

            if (!_cache.TryGetValue(handle.Key, out RevResHandle current) || !ReferenceEquals(current, handle))
            {
                // Shutdown / 分组卸载已摘除此代句柄；若加载器在取消前取得了 AB 包，归还那份孤立引用。
                ReleaseBundleOf(handle);
            }
            else if (handle.RefCount <= 0)
            {
                handle.AddFlag(RevResInstanceFlag.MarkedUnused);
                handle.UnusedTime = Time.realtimeSinceStartup;
                _unused[handle.Key] = handle;
            }

            EnqueueCompletedScopeReclaim(handle);
            if (!_pendingAsyncCallbacks.TryGetValue(handle, out List<Action<RevResHandle>> callbacks)) return;
            _pendingAsyncCallbacks.Remove(handle);
            for (int i = 0; i < callbacks.Count; i++) InvokeFinished(callbacks[i], handle);
        }

        /// <summary>向有效的消费者交付结果。域回执临时继承原所有者，异常不影响其它请求。</summary>
        private static void InvokeFinished(Action<RevResHandle> callback, RevResHandle handle, RevScopeHandle scope = null)
        {
            if (callback == null || (scope != null && !scope.IsOpen))
            {
                return;
            }
            try
            {
                if (scope == null)
                {
                    callback(handle);
                }
                else
                {
                    RevScope.Core.RunOwnedCallback(scope, callback, handle);
                }
            }
            catch (Exception e)
            {
                RevLog.Exception(e, "资源加载完成回调异常", "Res");
            }
        }

        // ==================== 引用计数 ====================

        /// <summary>给已有缓存增加一份引用，默认登记到当前域。</summary>
        public static void AddRef(ulong key, RevResLifetime lifetime = RevResLifetime.Automatic)
        {
            if (_cache.TryGetValue(key, out RevResHandle h))
            {
                h.RefCount++;
                h.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                _unused.Remove(key);
                h.Touch();
                TrackScopeReference(ResourceScope(lifetime), h);
            }
        }

        // 资源路径对应的 key 会被反复使用：旧资源卸载后，同一路径重新加载会生成一张新的 RevResHandle。
        // 旧业务代码此时可能还拿着旧 handle；若只按路径 key 减引用，就会把新资源的计数减掉，导致它被提前卸载。
        // 所以按 handle 释放前还要确认缓存里当前对象正是这个 handle 实例；过期句柄不能动新资源的计数。
        internal static bool IsCurrent(RevResHandle handle)
        {
            if (handle == null || handle.Key == 0 || handle.ReferenceReleased) { return false; }
            RevResHandle owner = handle.ReferenceOwner ?? handle;
            return _cache.TryGetValue(owner.Key, out RevResHandle current) && ReferenceEquals(current, owner);
        }

        /// <summary>归还已明确由调用者持有的原始引用。按缓存代次检查，旧句柄不影响新缓存。</summary>
        internal static bool DecRef(RevResHandle handle, bool removeWhenZero = true)
        {
            if (!IsCurrent(handle) || !ReleaseManualReference(handle.ReferenceOwner ?? handle, removeWhenZero))
            {
                return false;
            }
            if (handle.ReferenceOwner != null)
            {
                handle.ReferenceReleased = true;
                handle.RefCount = 0;
            }
            return true;
        }

        /// <summary>提前归还当前域的一份同路径引用。没有匹配归属时，只能归还普通手动引用。</summary>
        public static void DecRef(ulong key, bool removeWhenZero = true, RevResLifetime lifetime = RevResLifetime.Automatic)
        {
            if (!_cache.TryGetValue(key, out RevResHandle handle))
            {
                return;
            }
            if (!ReleaseScopeReference(ResourceScope(lifetime), handle, removeWhenZero) &&
                !ReleaseManualReference(handle, removeWhenZero))
            {
                RevLog.Warn($"没有可手动归还的引用：{handle.StandardPath}。请核对当前域或使用原租约。", "Res");
            }
        }

        /// <summary>普通归还只消耗未被域、具名租约或预加载登记的计数。</summary>
        private static bool ReleaseManualReference(RevResHandle handle, bool removeWhenZero)
        {
            int reserved = handle.ScopeReferences + (handle.NamedLeases?.Count ?? 0);
            if (handle.HasFlag(RevResInstanceFlag.Preloaded))
            {
                reserved++;
            }
            if (handle.RefCount <= reserved)
            {
                return false;
            }
            ReleaseReferences(handle, 1, removeWhenZero);
            return true;
        }

        /// <summary>归还已经核实所有权的计数。退域归零的资源交给有预算的主线程回收。</summary>
        private static void ReleaseReferences(RevResHandle handle, int count, bool removeWhenZero)
        {
            handle.RefCount -= count;
            if (handle.RefCount != 0 || !removeWhenZero)
            {
                return;
            }
            handle.AddFlag(RevResInstanceFlag.MarkedUnused);
            handle.UnusedTime = Time.realtimeSinceStartup;
            _unused[handle.Key] = handle;
            if (RevScope.Core.IsClosing)
            {
                ScheduleScopeReclaim(handle);
            }
        }

        /// <summary>按逻辑路径提前归还。跨域的持有方使用原具名租约，或显式归还手动引用。</summary>
        public static void Release(string rootPath, string resName, RevResLifetime lifetime = RevResLifetime.Automatic)
        {
            if (RevResPathUtil.IsValidResName(resName))
            {
                DecRef(RevResPathUtil.ComputeKey(rootPath, resName), lifetime: lifetime);
            }
        }

        /// <summary>
        /// 把"未使用表"里真正该释放的资源清掉。
        ///
        /// 【为什么要延迟释放？】
        ///   引用一归零就立刻卸载，会出现"刚卸完下一帧又加载回来"的抖动，
        ///   而且频繁调用 Resources.UnloadUnusedAssets 本身很昂贵。
        /// 【执行时机建议】切场景的读条阶段、或每 N 秒的定时检查里调用一次。
        /// </summary>
        public static void FlushUnused()
        {
            var removeUnused = new List<ulong>(_unused.Count);
            foreach (var kv in _unused)
            {
                RevResHandle h = kv.Value;
                if (h.RefCount > 0 || h.HasFlag(RevResInstanceFlag.Resident))
                {
                    h.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                    removeUnused.Add(kv.Key);
                    continue;
                }
                if (h.IsLoading) continue;

                ReleaseBundleOf(h);
                _cache.Remove(kv.Key);
                removeUnused.Add(kv.Key);
            }
            for (int i = 0; i < removeUnused.Count; i++) _unused.Remove(removeUnused[i]);

            SweepNativeAssets(requested: true);
        }

        // ==================== 分组卸载 ====================

        /// <summary>
        /// 按业务域批量卸载（切场景 / 退出战斗 / 关界面用）。
        ///
        /// 【只认 RevResHandle.Group 这一个字段】—— 与 AB 分包、与加载来源都无关。
        ///   所以别名背后有两条前提，缺一条就会"点名不到"：
        ///     ① 加载时传了分组（传 Unknown 的永远不在这里被释放）；
        ///     ② 归属是"首次确定"的（见 AssignGroup），不会被后来者覆盖。
        ///
        /// 【force 只影响一件事：是否解除"还有人在用"的保护】
        ///   false（推荐）：只卸载 RefCount ≤ 0 的 —— 绝不会打断其它域正在使用的资源；
        ///   true（彻底）  ：连 RefCount > 0 的也一并清账，用于"整个域整体销毁"（切场景 / 退出战斗）。
        ///                  注意：它会把别的域仍在引用的共享资源也从缓存里摘掉 ——
        ///                  跨域共享的资源请打 Resident 标志（见下），或改用 force = false。
        ///
        /// 【Resident 永远不参与分组卸载】连 force 也不行，只能靠 UnloadAll()。
        /// </summary>
        public static void UnloadGroup(RevResGroup group, bool force = false)
        {
            // 在途任务还没完成时，句柄可能仍标记为 Loading，单靠下面清缓存会跳过它；等它晚到完成又可能把资源写回已卸载的组。
            // 所以无论调用方是经由 Bootstrap 还是直接调用 Manager，都先取消该组的等待/加载任务，再处理已完成句柄。
            RevAsyncLoadPump.CancelGroup(group);
            var toRemove = new List<ulong>();

            foreach (var kv in _cache)
            {
                RevResHandle h = kv.Value;
                if (h.Group != group) continue;                                // 不归本组 → 跳过
                if (h.HasFlag(RevResInstanceFlag.Resident)) continue;             // 常驻 → 永不参与分组卸载
                if (h.IsLoading) continue;                                     // 在途加载必须由取消回调完成后再清
                if (h.RefCount > 0 && !force) continue;                        // 仍在使用 → 跳过（force 可越过）

                ReleaseBundleOf(h);
                toRemove.Add(kv.Key);
            }

            foreach (ulong k in toRemove)
            {
                _cache.Remove(k);
                _unused.Remove(k);
            }

            SweepNativeAssets(requested: true);
        }

        // ==================== 资源域 ====================

        public static RevResScope OpenScope() => new RevResScope();

        // ==================== 查询 / 调试 ====================

        /// <summary>按"根目录 + 资源名"查句柄（没加载过返回 Empty）。</summary>
        public static RevResHandle Get(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return RevResHandle.Empty;
            return _cache.TryGetValue(RevResPathUtil.ComputeKey(rootPath, resName), out RevResHandle h) ? h : RevResHandle.Empty;
        }

        /// <summary>
        /// 按完整逻辑路径查句柄（框架内部用：手上本来就是一条拼好的路径时）。
        /// 业务请用两个参数的重载。
        /// </summary>
        internal static RevResHandle GetByPath(string standardPath)
            => _cache.TryGetValue(ComputeKey(standardPath), out RevResHandle h) ? h : RevResHandle.Empty;

        /// <summary>按"根目录 + 资源名"判断是否已缓存。</summary>
        public static bool Contains(string rootPath, string resName)
            => RevResPathUtil.IsValidResName(resName) && _cache.ContainsKey(RevResPathUtil.ComputeKey(rootPath, resName));

        /// <summary>按"根目录 + 资源名"取当前引用计数。</summary>
        public static int GetRefCount(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return 0;
            return _cache.TryGetValue(RevResPathUtil.ComputeKey(rootPath, resName), out RevResHandle h) ? h.RefCount : 0;
        }

        // ==================== 预加载持有 / 批量与强制卸载 ====================

        /// <summary>
        /// 把句柄标记为"由预加载系统持有"。
        /// 预加载成功后调用；它有 RefCount（正常占一份引用），
        /// 另有 Preloaded 标志用于"批量释放"与"自动卸载默认保留"。
        /// </summary>
        internal static void MarkPreloaded(RevResHandle handle)
        {
            if (!IsCurrent(handle) || !handle.IsLoaded) return;
            handle.AddFlag(RevResInstanceFlag.Preloaded);
        }

        /// <summary>释放指定分组下"预加载持有"的引用（不影响业务自己持有的引用）</summary>
        public static int ReleasePreloaded(RevResGroup group)
        {
            List<ulong> keys = CollectPreloaded(group);
            ReleasePreloadedKeys(keys);
            return keys.Count;
        }

        /// <summary>释放全部"预加载持有"的引用</summary>
        public static int ReleasePreloadedAll()
        {
            List<ulong> keys = CollectPreloaded(null);
            ReleasePreloadedKeys(keys);
            return keys.Count;
        }

        private static List<ulong> CollectPreloaded(RevResGroup? group)
        {
            var keys = new List<ulong>();
            foreach (var kv in _cache)
            {
                RevResHandle h = kv.Value;
                if (!h.HasFlag(RevResInstanceFlag.Preloaded)) continue;
                if (group.HasValue && h.Group != group.Value) continue;
                keys.Add(kv.Key);
            }
            return keys;
        }

        private static void ReleasePreloadedKeys(List<ulong> keys)
        {
            foreach (ulong k in keys)
            {
                if (!_cache.TryGetValue(k, out RevResHandle h)) continue;

                h.RemoveFlag(RevResInstanceFlag.Preloaded);
                DecRef(k, lifetime: RevResLifetime.Manual);
            }
        }

        /// <summary>
        /// 强制从缓存移除一个句柄（供自动卸载使用）。
        /// ★ 调用方必须确保它已无引用（RefCount ≤ 0）且允许被释放。
        /// </summary>
        public static void ForceRemove(ulong key)
        {
            if (!_cache.TryGetValue(key, out RevResHandle h)) return;
            if (h.RefCount > 0 || h.IsLoading || h.HasFlag(RevResInstanceFlag.Resident))
            {
                RevLog.Warn($"拒绝强制移除仍在使用/加载/常驻的资源：{h.StandardPath}", "Res");
                return;
            }

            ReleaseBundleOf(h);
            _cache.Remove(key);
            _unused.Remove(key);
            h.RemoveFlag(RevResInstanceFlag.MarkedUnused);
        }

        /// <summary>
        /// 清空全部缓存并释放（回登录界面 / 大版本切换用）。
        /// 注意：正在被业务使用的资源也会被"账面清空"，请确保此时没人再用。
        /// </summary>
        public static void UnloadAll()
        {
            foreach (var kv in _cache) ReleaseBundleOf(kv.Value);

            ResetScopeReclaims();
            _cache.Clear();
            _unused.Clear();
            SweepNativeAssets(requested: true);
        }

        /// <summary>快照：所有缓存的句柄（自动卸载做 LRU 排序用；不要在遍历中改动它）</summary>
        public static List<RevResHandle> GetAllHandles()
        {
            var list = new List<RevResHandle>(_cache.Count);
            foreach (var kv in _cache) list.Add(kv.Value);
            return list;
        }

        // ==================== 内部 ====================

        /// <summary>
        /// 完整逻辑路径的键（FNV-1a 64 位）。
        /// 实现搬到 <see cref="RevResPathUtil"/> 了：那里同时提供"两段增量算键"的版本，
        /// 两者严格等价，且有工程外断言把关 —— 所以本方法保持原样转发即可。
        /// </summary>
        internal static ulong ComputeKey(string path) => RevResPathUtil.ComputeKey(path);

        private static void ReleaseBundleOf(RevResHandle handle)
        {
            if (handle == null || !handle.BundleAcquired) return;
            // 先把这张句柄上的“已取得 AB 包”标记和依赖清单摘掉，再通知 AB 加载器释放。
            // 这样如果 Shutdown、失败清理等路径重复走到这里，第二次会发现租约已归还并直接退出，不会把同一份引用减两次。
            // 同时保存本次加载时的依赖快照；不能释放时再查当前 Manifest，因为热更新后依赖清单可能已经变了。
            handle.BundleAcquired = false;
            RevABLoader loader = handle.BundleLoader;
            string bundleName = handle.BundleName;
            string[] dependencies = handle.BundleDependencies;
            handle.BundleLoader = null;
            handle.BundleName = null;
            handle.BundleDependencies = null;
            if (loader != null && !handle.HasFlag(RevResInstanceFlag.LoadFromEditor) && !string.IsNullOrEmpty(bundleName))
                loader.ReleaseBundle(bundleName, dependencies, handle.BundleGeneration);
        }

        /// <summary>异步加载完成时由异步泵回调。</summary>
        internal static void OnAsyncLoaded(ulong key, RevResHandle handle)
        {
            if (handle == null) return;
            handle.RemoveFlag(RevResInstanceFlag.InAsyncLoading);
            handle.AddFlag(RevResInstanceFlag.UsedAccurate);
        }
    }
}
