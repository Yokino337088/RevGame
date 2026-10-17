// ============================================================
// RevResourcesLoader.cs —— Resources 加载器
//
// 位置：Runtime\资源加载\Sub\
//
// 兜底用：只处理 "Res/" 前缀的特殊资源，以及 AB 加载失败后的兜底。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    public class RevResourcesLoader : IRevResLoader
    {
        public object Load(RevResHandle handle, out RevResLoadErrorReason err)
        {
            UnityEngine.Object obj = Resources.Load(handle.RealPath, handle.ContentType);
            err = obj != null ? RevResLoadErrorReason.None : RevResLoadErrorReason.FileNotExist;
            return obj;
        }

        public void LoadAsync(RevResHandle handle, Action<RevResHandle> onFinished, RevCancellationToken token = null)
        {
            ResourceRequest req = Resources.LoadAsync(handle.RealPath, handle.ContentType);

            req.completed += _ =>
            {
                // ResourcesRequest 本身不可中断，但取消后不得把结果写回已卸载的句柄。
                if (token != null && token.IsCancelled)
                {
                    handle.ErrorReason = RevResLoadErrorReason.Cancelled;
                    handle.MarkError();
                }
                else
                {
                    handle.ErrorReason = req.asset != null ? RevResLoadErrorReason.None : RevResLoadErrorReason.FileNotExist;
                    handle.SetContent(req.asset);
                }
                onFinished?.Invoke(handle);
            };
        }
    }
}
