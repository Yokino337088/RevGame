// ============================================================
// RevEditorLoader.cs —— 编辑器直读加载器
//
// 位置：Runtime\资源加载\Sub\
//
// 开发期直接读工程资源（AssetDatabase），无需打包。
// 只在编辑器下有效；真机编译时走 #else 分支返回失败。
// ============================================================
using System;
using UnityEngine;

namespace Revolution
{
    public class RevEditorLoader : IRevResLoader
    {
        public object Load(RevResHandle handle, out RevResLoadErrorReason err)
        {
#if UNITY_EDITOR
            UnityEngine.Object obj = UnityEditor.AssetDatabase.LoadAssetAtPath(handle.RealPath, handle.ContentType);

            if (obj == null) { err = RevResLoadErrorReason.FileNotExist; return null; }
            if (!handle.ContentType.IsInstanceOfType(obj)) { err = RevResLoadErrorReason.TypeMismatch; return null; }

            err = RevResLoadErrorReason.None;
            return obj;
#else
            err = RevResLoadErrorReason.FileNotExist;
            return null;
#endif
        }

        public void LoadAsync(RevResHandle handle, Action<RevResHandle> onFinished, RevCancellationToken token = null)
        {
            // 编辑器直读是同步操作 —— 立刻出结果，但仍按异步契约回调
            handle.SetContent(Load(handle, out RevResLoadErrorReason err));
            handle.ErrorReason = err;
            onFinished?.Invoke(handle);
        }
    }
}
