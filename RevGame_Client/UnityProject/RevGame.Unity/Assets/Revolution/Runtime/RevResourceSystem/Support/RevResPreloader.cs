// ============================================================
// RevResPreloader.cs —— 资源预加载器
//
// 位置：Runtime\RevResourceSystem\Support\
//
// 【解决什么问题？】
//   进战斗 / 开界面之前，如果不提前把资源铺好，玩家会看到"按了技能卡一下"。
//   预加载就是"在空闲时，把后面大概率要用的资源先加载进内存"。
//
// 【关键设计：预加载"持有一份引用"】
//   预加载 = 正常异步加载 + 给句柄打一个 Preloaded 标志。
//   为什么要持有引用？—— 否则加载完没人引用，下一次自动卸载就被清掉了，等于白做。
//   这份引用由预加载系统记账，用 ReleaseGroup / ReleaseAll 显式归还。
//
// 【幂等】同一资源重复预加载不会重复占引用（第二次命中缓存，只 +1 引用；
//         若已带 Preloaded 标志则直接跳过，不再重复持有）。
//
// 【优先级】预加载默认走 Background，不抢玩家当前操作资源的 IO。
//
// 【参数口径】和其它地方一致："根目录 + 资源名"两段（根目录用生成的 RevResPath 常量）。
//   批量有三种写法：
//     · 同一目录一批：Preload(RevResPath.UI_Icon, new[] { "Hero_1001", "Hero_1002" }, RevResGroup.UI)
//     · 多目录混合：  Preload(new[] { (RevResPath.Hero, "1001"), (RevResPath.Skill, "1001_Effect") }, RevResGroup.Battle)
//     · 单个：        Preload(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI)
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 一次预加载任务的句柄：可以拿进度、判断完成、取消。
    /// </summary>
    public sealed class RevResPreloadTask
    {
        internal RevResPreloadTask(int total) { Total = total; }

        /// <summary>本次预加载涉及的资源总数（已去重）</summary>
        public int Total { get; private set; }

        /// <summary>已处理数量（无论成功失败）</summary>
        public int Finished { get; private set; }

        /// <summary>失败数量</summary>
        public int Failed { get; private set; }

        /// <summary>进度 0~1</summary>
        public float Progress => Total <= 0 ? 1f : (float)Finished / Total;

        /// <summary>是否已完成（全部回调结束，或被取消）</summary>
        public bool IsDone { get; private set; }

        /// <summary>是否被取消（取消后不再把新加载成功的资源标记为"预加载持有"）</summary>
        public bool IsCancelled { get; private set; }

        /// <summary>所属分组（便于整组释放）</summary>
        public RevResGroup Group { get; internal set; }

        /// <summary>进度回调：每完成一个资源调用一次</summary>
        public event Action<RevResPreloadTask> OnProgress;

        /// <summary>完成回调：全部处理完（或被取消）调用一次</summary>
        public event Action<RevResPreloadTask> OnCompleted;

        /// <summary>
        /// 取消本次预加载。
        /// 【语义】只影响"后续结果"：不再把新加载成功的资源标记为预加载持有，并立即结束任务通知。
        ///        已经加载并持有的那部分，请调用 RevResPreloader.Release(group) 归还。
        /// </summary>
        public void Cancel()
        {
            if (IsDone) return;
            IsCancelled = true;
            Complete();
        }

        internal void ReportOne(bool success)
        {
            if (IsDone) return;             // Cancel 后仍需清理引用，但不再回报进度
            Finished++;
            if (!success) Failed++;
            // 进度属于业务扩展口；一个监听者出错不能截断最后一次 Complete。
            InvokeListeners(OnProgress, "预加载进度回调");
        }

        internal void Complete()
        {
            if (IsDone) return;
            IsDone = true;
            InvokeListeners(OnCompleted, "预加载完成回调");
        }

        private void InvokeListeners(Action<RevResPreloadTask> listeners, string context)
        {
            if (listeners == null) { return; }
            foreach (Action<RevResPreloadTask> listener in listeners.GetInvocationList())
            {
                try { listener(this); }
                catch (Exception e) { RevLog.Exception(e, context, "Res"); }
            }
        }
    }

    public static class RevResPreloader
    {
        /// <summary>
        /// 预加载**一个**资源（异步、后台优先级）。
        /// 失败不会抛异常，最后可通过 task.Failed 查看失败数。
        /// </summary>
        public static RevResPreloadTask Preload(string rootPath, string resName,
            RevResGroup group = RevResGroup.Unknown,
            RevResLoadPriority priority = RevResLoadPriority.Background,
            Action<RevResPreloadTask> onProgress = null,
            Action<RevResPreloadTask> onCompleted = null)
        {
            return Preload(new[] { (rootPath, resName) }, group, priority, onProgress, onCompleted);
        }

        /// <summary>
        /// 预加载**同一目录下的多个**资源。
        /// 例：Preload(RevResPath.UI_Icon, new[] { "Hero_1001", "Hero_1002" }, RevResGroup.UI);
        /// </summary>
        public static RevResPreloadTask Preload(string rootPath, IEnumerable<string> resNames,
            RevResGroup group = RevResGroup.Unknown,
            RevResLoadPriority priority = RevResLoadPriority.Background,
            Action<RevResPreloadTask> onProgress = null,
            Action<RevResPreloadTask> onCompleted = null)
        {
            var items = new List<(string rootPath, string resName)>();
            if (resNames != null)
            {
                foreach (string name in resNames)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    items.Add((rootPath, name));
                }
            }

            return Preload(items, group, priority, onProgress, onCompleted);
        }

        /// <summary>
        /// 预加载一批资源（可跨多个目录）。
        /// 例：Preload(new[] { (RevResPath.Hero, "1001"), (RevResPath.Skill, "1001_Effect") }, RevResGroup.Battle);
        /// 资源不存在 / 加载失败不会中断整批，最后可通过 task.Failed 查看失败数。
        /// </summary>
        /// <param name="items">"根目录 + 资源名"列表</param>
        /// <param name="group">业务分组（决定归属，便于整组释放）</param>
        /// <param name="priority">加载优先级（默认 Background，不抢前台）</param>
        /// <param name="onProgress">进度回调（可为 null）</param>
        /// <param name="onCompleted">完成回调（可为 null）</param>
        public static RevResPreloadTask Preload(IEnumerable<(string rootPath, string resName)> items,
            RevResGroup group = RevResGroup.Unknown,
            RevResLoadPriority priority = RevResLoadPriority.Background,
            Action<RevResPreloadTask> onProgress = null,
            Action<RevResPreloadTask> onCompleted = null)
        {
            // ① 去重：同一批里重复写同一个资源很常见，别重复占引用。
            //    用"两段的键"去重（不拼字符串），和资源系统的缓存键同一套算法。
            var unique = new List<(string rootPath, string resName)>();
            var seen = new HashSet<ulong>();

            if (items != null)
            {
                foreach ((string rootPath, string resName) item in items)
                {
                    if (!RevResPathUtil.IsValidResName(item.resName)) continue;
                    if (seen.Add(RevResPathUtil.ComputeKey(item.rootPath, item.resName))) unique.Add(item);
                }
            }

            var task = new RevResPreloadTask(unique.Count) { Group = group };
            if (onProgress != null) task.OnProgress += onProgress;
            if (onCompleted != null) task.OnCompleted += onCompleted;

            if (unique.Count == 0)
            {
                task.Complete();
                return task;
            }

            // ② 逐个发起异步加载
            foreach ((string rootPath, string resName) item in unique)
            {
                // 用 typeof(UnityEngine.Object) 加载：预加载事先不知道具体类型，
                // 而三种加载器都能用"基类类型"把资源取出来（后续按具体类型取用不受影响）。
                RevResManager.LoadAsync(item.rootPath, item.resName, typeof(UnityEngine.Object), h =>
                {
                    bool ok = h != null && h.IsLoaded && RevResManager.IsCurrent(h);

                    // 每次 LoadAsync 都 +1。只保留一份全局预加载持有：失败、取消、
                    // 或其它预加载任务已经持有该句柄时，都必须归还本次多拿的引用。
                    if (ok && !task.IsCancelled && !h.HasFlag(RevResInstanceFlag.Preloaded))
                    {
                        RevResManager.MarkPreloaded(h);
                    }
                    else if (h != null && h.Key != 0)
                    {
                        RevResManager.DecRef(h);     // 必须按句柄身份释放，不能误扣同 key 的新一代缓存
                    }

                    try { task.ReportOne(ok); }
                    finally
                    {
                        if (task.Finished >= task.Total) { task.Complete(); }
                    }
                }, group, priority);
            }

            return task;
        }

        /// <summary>按分组预加载（语义更清晰的别名：同一目录下一批）</summary>
        public static RevResPreloadTask PreloadGroup(RevResGroup group, string rootPath, IEnumerable<string> resNames,
            RevResLoadPriority priority = RevResLoadPriority.Background,
            Action<RevResPreloadTask> onProgress = null,
            Action<RevResPreloadTask> onCompleted = null)
            => Preload(rootPath, resNames, group, priority, onProgress, onCompleted);

        // ==================== 释放 ====================

        /// <summary>归还某一分组全部"预加载持有"的引用</summary>
        public static int Release(RevResGroup group) => RevResManager.ReleasePreloaded(group);

        /// <summary>归还全部"预加载持有"的引用</summary>
        public static int ReleaseAll() => RevResManager.ReleasePreloadedAll();

        // ==================== 查询 ====================

        /// <summary>统计当前"预加载持有"的资源数量</summary>
        public static int GetPreloadedCount() => GetPreloadedCount(null);

        /// <summary>
        /// 统计某一分组下"预加载持有"的资源数量。
        /// ★ 参数是 RevResGroup?（可空）：null = 不筛分组、统计全部 —— 无参重载就是这么调的，
        ///   所以下面必须用 group.HasValue / group.Value。
        /// </summary>
        public static int GetPreloadedCount(RevResGroup? group)
        {
            int count = 0;
            List<RevResHandle> all = RevResManager.GetAllHandles();
            foreach (RevResHandle h in all)
            {
                if (!h.HasFlag(RevResInstanceFlag.Preloaded)) continue;
                if (group.HasValue && h.Group != group.Value) continue;
                count++;
            }
            return count;
        }
    }
}
