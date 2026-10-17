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
    public static class RevResManager
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
        /// 业务最常用的入口：直接拿资源对象。
        /// 例：Sprite icon = RevResManager.Load&lt;Sprite&gt;(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
        /// 注意：失败时返回 null；要拿失败原因请用 LoadHandle&lt;T&gt;（或下面返回 RevResHandle 的重载）。
        /// </summary>
        public static T Load<T>(string rootPath, string resName, RevResGroup group = RevResGroup.Unknown)
            where T : UnityEngine.Object
        {
            return Load(rootPath, resName, typeof(T), group).Content as T;
        }

        /// <summary>
        /// 同步加载并拿到完整句柄（泛型版）—— 和 Load&lt;T&gt; 一样不写 typeof，但能看失败原因：
        ///
        ///     RevResHandle h = RevResManager.LoadHandle&lt;Sprite&gt;(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
        ///     if (!h.IsLoaded) Debug.LogError($"失败：{h.ErrorReason}");
        /// </summary>
        public static RevResHandle LoadHandle<T>(string rootPath, string resName, RevResGroup group = RevResGroup.Unknown)
            where T : UnityEngine.Object
            => Load(rootPath, resName, typeof(T), group);

        /// <summary>
        /// 同步加载（两个参数版，业务入口）。
        /// 缓存命中时**不拼字符串**：直接用两段算出的键查表（热路径零分配）。
        /// </summary>
        public static RevResHandle Load(string rootPath, string resName, Type contentType, RevResGroup group = RevResGroup.Unknown)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return RevResHandle.Empty;

            // ① 缓存命中（零字符串分配）
            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);
            // 失败结果也会暂存在缓存里，避免每帧都重复尝试同一个坏路径；但调用方释放失败句柄、引用数归零后，
            // 以后再次 Load 应该允许重新尝试（例如玩家修正资源或热更已补上文件）。若保留旧失败项，后续请求只会不断拿到上次的错误。
            if (_cache.TryGetValue(key, out RevResHandle existing) && !existing.IsLoaded && !existing.IsLoading && existing.RefCount <= 0)
                DiscardFailedHandle(key, existing);
            if (TryHitCache(key, contentType, group, out RevResHandle hit)) return hit;

            // ② 未命中：这时才拼出完整逻辑路径（映射表 / 句柄 / 日志都要它，一次分配可接受）
            return LoadByPath(RevResPathUtil.Join(rootPath, resName), contentType, group);
        }

        /// <summary>缓存命中处理：校验内容类型后引用 +1、刷新 LRU 并补分组。</summary>
        private static bool TryHitCache(ulong key, Type contentType, RevResGroup group, out RevResHandle handle)
        {
            if (_cache.TryGetValue(key, out RevResHandle cached))
            {
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

        /// <summary>按完整逻辑路径加载（框架内部：两条对外入口最终都汇到这里）。</summary>
        private static RevResHandle LoadByPath(string standardPath, Type contentType, RevResGroup group)
        {
            if (string.IsNullOrEmpty(standardPath)) return RevResHandle.Empty;

            ulong key = ComputeKey(standardPath);

            // ① 缓存命中：计数 +1，直接返回
            if (_cache.TryGetValue(key, out RevResHandle existing) && !existing.IsLoaded && !existing.IsLoading && existing.RefCount <= 0)
                DiscardFailedHandle(key, existing);
            if (TryHitCache(key, contentType, group, out RevResHandle cached)) return cached;

            // ② 按优先级依次尝试策略（责任链 + 兜底）
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
            // 1) 映射真实路径
            string realPath = policy.MapPath(handle.StandardPath, handle.ContentType);
            if (string.IsNullOrEmpty(realPath)) { err = RevResLoadErrorReason.PathNotMapped; return false; }
            handle.RealPath = realPath;

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
            if (content == null) return false;

            // 3) 成功
            handle.ErrorReason = RevResLoadErrorReason.None;
            handle.SetContent(content);
#if UNITY_EDITOR
            if (policy is RevEditorResPolicy) handle.AddFlag(RevResInstanceFlag.LoadFromEditor);
#endif
            return true;
        }

        // ==================== 异步加载 ====================

        /// <summary>
        /// 异步加载（两个参数版，业务入口）。
        /// 缓存已就绪时立即回调，且**不拼字符串**。
        /// </summary>
        public static RevResHandle LoadAsync(string rootPath, string resName, Type contentType,
            Action<RevResHandle> onFinished, RevResGroup group = RevResGroup.Unknown,
            RevResLoadPriority priority = RevResLoadPriority.Normal)
        {
            if (!RevResPathUtil.IsValidResName(resName))
            {
                InvokeFinished(onFinished, RevResHandle.Empty);
                return RevResHandle.Empty;
            }

            ulong key = RevResPathUtil.ComputeKey(rootPath, resName);

            // ① 缓存已就绪：类型安全后立即回调（零字符串分配）
            if (_cache.TryGetValue(key, out RevResHandle ready) && ready.IsLoaded)
            {
                if (!CanSatisfyType(ready, contentType))
                {
                    RevResHandle mismatch = CreateTypeMismatchHandle(ready.StandardPath, contentType, ready.ContentType);
                    InvokeFinished(onFinished, mismatch);
                    return mismatch;
                }

                ready.RefCount++;
                ready.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                _unused.Remove(key);
                ready.Touch();
                AssignGroup(ready, group);
                InvokeFinished(onFinished, ready);
                return ready;
            }

            // ② 其余情况交给"路径版"（要登记句柄、要拼路径查映射表）
            return LoadAsyncByPath(RevResPathUtil.Join(rootPath, resName), contentType, onFinished, group, priority);
        }

        /// <summary>按完整逻辑路径异步加载（框架内部）。</summary>
        private static RevResHandle LoadAsyncByPath(string standardPath, Type contentType,
            Action<RevResHandle> onFinished, RevResGroup group,
            RevResLoadPriority priority)
        {
            if (string.IsNullOrEmpty(standardPath))
            {
                InvokeFinished(onFinished, RevResHandle.Empty);
                return RevResHandle.Empty;
            }

            ulong key = ComputeKey(standardPath);

            // ① 缓存命中：成功句柄直接复用；在途句柄合并回调。
            //    已完成失败句柄若引用归零，则丢弃失败项并重新尝试（例如 UI 资源路径修正后再次打开）。
            if (_cache.TryGetValue(key, out RevResHandle cached))
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
                StandardPath = standardPath,
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
                view.RealPath = result.RealPath;
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
        /// 异步加载（泛型版，推荐）—— 不必再手写 typeof(T)：
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
            RevResGroup group = RevResGroup.Unknown, RevResLoadPriority priority = RevResLoadPriority.Normal)
            where T : UnityEngine.Object
        {
            return LoadAsync(rootPath, resName, typeof(T),
                handle => onFinished?.Invoke(handle.Content as T), group, priority);
        }

        /// <summary>
        /// 异步版"责任链"：从 startIndex 开始找能处理 handle 的策略；
        /// 若该策略加载失败且 AllowFallback = true，就自动换下一条策略重试。
        /// </summary>
        private static void TryLoadAsyncFrom(RevResHandle handle, int startIndex, RevResLoadPriority priority)
        {
            for (int i = startIndex; i < _policies.Count; i++)
            {
                IRevResPolicy policy = _policies[i];
                if (!policy.Match(handle.StandardPath)) continue;

                string realPath = policy.MapPath(handle.StandardPath, handle.ContentType);
                if (string.IsNullOrEmpty(realPath))
                {
                    handle.ErrorReason = RevResLoadErrorReason.PathNotMapped;
                    handle.MarkError();
                    if (policy.AllowFallback) continue;
                    CompleteAsync(handle);
                    return;
                }

                handle.RealPath = realPath;
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

        private static void AddPendingCallback(RevResHandle handle, Action<RevResHandle> callback)
        {
            if (handle == null || callback == null) return;
            if (!_pendingAsyncCallbacks.TryGetValue(handle, out List<Action<RevResHandle>> callbacks))
                _pendingAsyncCallbacks[handle] = callbacks = new List<Action<RevResHandle>>(2);
            callbacks.Add(callback);
        }

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

            if (!_pendingAsyncCallbacks.TryGetValue(handle, out List<Action<RevResHandle>> callbacks)) return;
            _pendingAsyncCallbacks.Remove(handle);
            for (int i = 0; i < callbacks.Count; i++) InvokeFinished(callbacks[i], handle);
        }

        private static void InvokeFinished(Action<RevResHandle> callback, RevResHandle handle)
        {
            if (callback == null) return;
            try { callback(handle); }
            catch (Exception e) { RevLog.Exception(e, "资源加载完成回调异常", "Res"); }
        }

        // ==================== 引用计数 ====================

        public static void AddRef(ulong key)
        {
            if (_cache.TryGetValue(key, out RevResHandle h))
            {
                h.RefCount++;
                h.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                _unused.Remove(key);
                h.Touch();
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

        internal static bool DecRef(RevResHandle handle, bool removeWhenZero = true)
        {
            if (!IsCurrent(handle)) { return false; }
            if (handle.ReferenceOwner != null)
            {
                handle.ReferenceReleased = true;
                handle.RefCount = 0;
                DecRef(handle.ReferenceOwner.Key, removeWhenZero);
            }
            else { DecRef(handle.Key, removeWhenZero); }
            return true;
        }

        public static void DecRef(ulong key, bool removeWhenZero = true)
        {
            if (!_cache.TryGetValue(key, out RevResHandle h)) return;
            if (h.RefCount <= 0)
            {
                RevLog.Warn($"资源句柄重复 Release：{h.StandardPath}（RefCount 已为 0）", "Res");
                return;
            }

            if (--h.RefCount <= 0 && removeWhenZero)
            {
                h.AddFlag(RevResInstanceFlag.MarkedUnused);
                h.UnusedTime = Time.realtimeSinceStartup;   // 记录"归零时刻"，供冷却期判断
                _unused[key] = h;               // 延迟释放：先进未使用表
            }
        }

        /// <summary>按"根目录 + 资源名"释放（业务更常用）</summary>
        public static void Release(string rootPath, string resName)
        {
            if (!RevResPathUtil.IsValidResName(resName)) return;
            DecRef(RevResPathUtil.ComputeKey(rootPath, resName));
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

            Resources.UnloadUnusedAssets();
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

            Resources.UnloadUnusedAssets();
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
                DecRef(k);          // 还掉"预加载"那份引用（归零则进未使用表）
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

            _cache.Clear();
            _unused.Clear();
            Resources.UnloadUnusedAssets();
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
