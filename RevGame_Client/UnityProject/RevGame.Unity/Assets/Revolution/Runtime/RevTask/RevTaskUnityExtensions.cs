// ============================================================
// RevTaskUnityExtensions.cs —— 桥接 Unity 的异步操作
//
// 位置：Runtime\RevTask\
//
// 覆盖资源加载会碰到的全部异步类型（它们都继承自 AsyncOperation）：
//   · AssetBundleCreateRequest       （AssetBundle.LoadFromFileAsync）
//   · AssetBundleRequest             （bundle.LoadAssetAsync）
//   · ResourceRequest                （Resources.LoadAsync）
//   · AsyncOperation                 （SceneManager.LoadSceneAsync）
//   · UnityWebRequestAsyncOperation  （UnityWebRequest.SendWebRequest）
//
// 【一个关键坑】
//   不要写"注册 completed 时捕获 this 的 awaiter 结构体"：
//   结构体被状态机复制后，回调写入的是副本，续体永远拿不到 → 死等。
//   这里改为包装成 RevTask（Promise 是引用类型），从根上避免该问题。
//
// 【性能说明】每 await 一次会分配一个小的完成源（约 100B）。
//   相比协程"整个等待期间每帧都在分配"，已经好一个数量级。
// ============================================================
using UnityEngine;

namespace Revolution
{
    public static class RevTaskUnityExtensions
    {
        /// <summary>把 Unity 的异步操作转成 RevTask</summary>
        public static RevTask ToRevTask(this AsyncOperation operation)
        {
            // 已完成 / 空 → 零分配直接返回
            if (operation == null || operation.isDone) return RevTask.Completed;

            var source = RevTask.CreateSource();
            operation.completed += _ => source.SetResult();   // 捕获的是 source（引用类型），安全
            return source.Task;
        }

        /// <summary>让 `await someAsyncOperation;` 直接可用</summary>
        public static RevTaskAwaiter GetAwaiter(this AsyncOperation operation)
            => operation.ToRevTask().GetAwaiter();
    }
}
