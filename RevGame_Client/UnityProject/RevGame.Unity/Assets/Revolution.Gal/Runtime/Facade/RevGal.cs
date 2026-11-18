using System;

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
            return RevGalPlayback.LoadAsync(storyPath, options, entry);
        }

        /// <summary>默认面板接管会话。slot 为空时从剧情起点开始，指定存档槽时验证后恢复。</summary>
        public static RevTask<RevGalPanel> OpenAsync(string storyPath, RevGalOptions options = null, string slot = null)
        {
            return OpenAsync(storyPath, options, slot, null);
        }

        /// <summary>宿主可取消本次打开。面板交付后由宿主或用户关闭，不再响应打开令牌。</summary>
        public static async RevTask<RevGalPanel> OpenAsync(string storyPath, RevGalOptions options, string slot, RevCancellationToken cancellation)
        {
            cancellation?.ThrowIfCancelled();
            int ticket = ++_openRequest;
            var session = await RevGalPlayback.LoadAsync(storyPath, options, slot: slot, cancellation: cancellation);
            bool openingPanel = false;
            try
            {
                ThrowIfSuperseded();
                // 先持有预制体引用，确保取消后不会由晚到的资源回调在新业务域实例化面板。
                using var prefab = cancellation == null ? null : await RevGalAsset<UnityEngine.GameObject>.Load(PanelRoot + "/" + nameof(RevGalPanel));
                cancellation?.ThrowIfCancelled();
                ThrowIfSuperseded();
                openingPanel = true;
                cancellation?.Register(() =>
                {
                    if (openingPanel && ticket == _openRequest)
                    {
                        Close();
                    }
                });
                var panel = await RevUI.OpenAsync<RevGalPanel, RevGalSession>(session);
                openingPanel = false;
                if (panel == null)
                {
                    throw new InvalidOperationException("默认剧情面板加载失败，请检查 UI/Gal/RevGalPanel 的资源配置");
                }
                if (ticket != _openRequest || cancellation?.IsCancelled == true)
                {
                    if (panel.Data == session)
                    {
                        RevUI.Close(panel);
                    }
                    cancellation?.ThrowIfCancelled();
                    ThrowIfSuperseded();
                }
                return panel;
            }
            catch
            {
                session.Dispose();
                cancellation?.ThrowIfCancelled();
                throw;
            }
            finally
            {
                openingPanel = false;
            }

            void ThrowIfSuperseded()
            {
                if (ticket != _openRequest)
                {
                    throw new OperationCanceledException("剧情打开请求已被关闭或替换");
                }
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
            return RevGalPlayback.Preview(story, entry);
        }
    }
}
