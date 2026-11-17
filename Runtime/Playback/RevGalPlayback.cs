using System;

namespace Revolution.Gal
{
    /// <summary>剧情会话入口。宿主负责呈现和释放会话，可用项目资源路径、已有 SO 或已装配的剧情定义。</summary>
    public static class RevGalPlayback
    {
        /// <summary>在宿主已经取得剧情 SO 时建立独立会话，调用时复制剧情数据和播放选项。</summary>
        public static RevGalSession Create(RevGalStory story, RevGalOptions options = null, string entry = null, string slot = null)
        {
            if (story == null)
            {
                throw new ArgumentNullException(nameof(story));
            }
            return CreateFromDefinition(story.Definition, options, entry, slot);
        }

        /// <summary>允许使用宿主自己的数据装配，校验、分支、存档仍由同一个 Gal 会话执行。</summary>
        public static RevGalSession CreateFromDefinition(RevGalDefinition definition, RevGalOptions options = null, string entry = null, string slot = null)
        {
            if (!string.IsNullOrEmpty(entry) && !string.IsNullOrEmpty(slot))
            {
                throw new ArgumentException("请选择节点入口或存档槽其中一个");
            }
            options = options?.Copy() ?? new RevGalOptions();
            if (options.UseDefaultStorage && options.Storage == null)
            {
                options.Storage = new RevGalLocalStorage();
            }
            return new RevGalSession(definition, options, entry, slot);
        }

        /// <summary>通过已初始化的 Revolution 资源系统取得独立会话。取消后归还收到的资源引用。</summary>
        public static async RevTask<RevGalSession> LoadAsync(string storyPath, RevGalOptions options = null, string entry = null, string slot = null, RevCancellationToken cancellation = null)
        {
            cancellation?.ThrowIfCancelled();
            if (!RevInitialize.IsInitialized)
            {
                throw new InvalidOperationException("请先由游戏启动入口初始化 Revolution");
            }
            // 会话会复制 SO。离开作用域后，加载故事本身的引用即可归还。
            using var story = await RevGalAsset<RevGalStory>.Load(storyPath);
            cancellation?.ThrowIfCancelled();
            return Create(story.Value, options, entry, slot);
        }

        /// <summary>给编辑器及离屏测试使用。每次试读持有独立状态，入场时无需准备面板或资源包。</summary>
        public static RevGalSession Preview(RevGalStory story, string entry = null)
        {
            return Create(story, new RevGalOptions { UseDefaultStorage = false }, entry);
        }
    }

    /// <summary>Gal 自己持有的一次框架资源引用。剧情复制完成或媒体被替换时释放。</summary>
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
