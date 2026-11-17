using System;
using UnityEngine;

namespace Revolution.Gal
{
    /// <summary>业务入口。加载使用 Revolution 逻辑路径，默认面板通过 RevUI 打开。</summary>
    public static class RevGal
    {
        public const string PanelRoot = "UI/Gal";
        private static int _openRequest;

        public static RevGalSession Current => RevUI.Get<RevGalPanel>()?.Data;

        /// <summary>自定义面板使用此入口。返回的会话由调用方负责释放。</summary>
        public static RevTask<RevGalSession> LoadAsync(string storyPath, RevGalOptions options = null, string entry = null)
        {
            return LoadSession(storyPath, options, entry, null);
        }

        private static async RevTask<RevGalSession> LoadSession(string storyPath, RevGalOptions options, string entry, string slot)
        {
            if (!RevInitialize.IsInitialized)
            {
                throw new InvalidOperationException("请先由游戏启动入口初始化 Revolution");
            }
            using var story = await RevGalAsset<RevGalStory>.Load(storyPath);
            options = options?.Copy() ?? new RevGalOptions();
            options.Storage ??= new RevGalLocalStorage();
            return new RevGalSession(story.Value.Definition, options, entry, slot);
        }

        /// <summary>默认面板接管会话。slot 为空时从剧情起点开始，指定存档槽时验证后恢复。</summary>
        public static async RevTask<RevGalPanel> OpenAsync(string storyPath, RevGalOptions options = null, string slot = null)
        {
            int ticket = ++_openRequest;
            var session = await LoadSession(storyPath, options, null, slot);
            try
            {
                if (ticket != _openRequest)
                {
                    throw new OperationCanceledException("已有新的剧情打开请求");
                }
                var panel = await RevUI.OpenAsync<RevGalPanel, RevGalSession>(session);
                if (panel == null)
                {
                    throw new InvalidOperationException("默认剧情面板加载失败，请检查 UI/Gal/RevGalPanel 的资源配置");
                }
                if (ticket != _openRequest)
                {
                    if (panel.Data == session)
                    {
                        RevUI.Close(panel);
                    }
                    throw new OperationCanceledException("剧情打开请求已取消");
                }
                return panel;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public static bool Close()
        {
            ++_openRequest;
            return RevUI.Close<RevGalPanel>();
        }

        /// <summary>编辑器试读使用当前 SO 副本。默认没有磁盘存储，也不调用宿主信号。</summary>
        public static RevGalSession Preview(RevGalStory story, string entry = null)
        {
            if (story == null)
            {
                throw new ArgumentNullException(nameof(story));
            }
            return new RevGalSession(story.Definition, new RevGalOptions(), entry);
        }
    }

    internal sealed class RevGalAsset<T> : IDisposable where T : UnityEngine.Object
    {
        private readonly RevResScope _scope;
        public T Value { get; private set; }

        private RevGalAsset(RevResHandle handle)
        {
            _scope = RevResManager.OpenScope();
            _scope.Track(handle);
            Value = handle.Get<T>();
        }

        public static async RevTask<RevGalAsset<T>> Load(string path)
        {
            if (!RevGalValidation.IsLogicalPath(path) || path == RevGalStage.Clear)
            {
                throw new ArgumentException("需要 Revolution 资源逻辑路径：" + path);
            }
            int slash = path.LastIndexOf('/');
            string root = slash < 0 ? "" : path.Substring(0, slash + 1);
            string name = path.Substring(slash + 1);
            var completed = RevTask<RevResHandle>.CreateSource();
            RevResManager.LoadAsync(root, name, typeof(T), completed.SetResult, RevResGroup.UI);
            RevResHandle handle = await completed.Task;
            if (handle == null)
            {
                throw new InvalidOperationException("资源系统没有返回句柄：" + path);
            }
            var asset = new RevGalAsset<T>(handle);
            if (asset.Value == null)
            {
                asset.Dispose();
                throw new InvalidOperationException("加载剧情资源失败：" + path + "，" + handle.ErrorReason);
            }
            return asset;
        }

        public void Dispose()
        {
            Value = null;
            _scope.Dispose();
        }
    }
}
