using System;
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Generated;
using Revolution;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.View
{
    /// <summary>
    /// Unity图集绑定与Revolution资源门面之间的项目适配器，生命周期由GameBootstrap持有。
    /// 预制体仍保留Sprite的GUID引用；当Unity发现它缺少图集像素时，atlasRequested通知本类。
    /// 本类异步加载SpriteAtlas后，把结果交回Unity，原有Image/SpriteRenderer自动改用图集。
    /// 因此不必每次播放一帧都GetSprite，也不会为每颗棋子克隆一批Sprite对象。
    /// </summary>
    public sealed class GameSpriteAtlasService : IDisposable
    {
        /// <summary>第一章专用图集标签；它与图片/材质/Shader同包，仅由当前背景租约持有。</summary>
        public const string TutorialAtlasName = "SceneBackdrop_0fc7e394Atlas";
        /// <summary>每个标签只发出一次LoadAsync；多个同时等待的渲染器复用同一条请求。</summary>
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        /// <summary>生成器给出的合法图集标签，不允许随意拼磁盘路径或尝试未知AB名。</summary>
        private readonly HashSet<string> _known = new HashSet<string>(GameSpriteAtlases.Names, StringComparer.Ordinal);
        /// <summary>场景离开后阻止迟到回调注册旧图集，避免重进游戏时出现旧资源引用。</summary>
        private bool _disposed;
        /// <summary>画面切换版本；延期释放只对发起那次切换有效，快速返回战斗会使旧操作失效。</summary>
        private int _screenVersion;

        /// <summary>一张图集的一次租约。Release只释放本类的引用，绝不强制卸载别人的依赖包。</summary>
        internal sealed class Entry
        {
            /// <summary>生成清单中的资源名，同时也是Release需要使用的名字；一生不改变。</summary>
            public readonly string Name;
            /// <summary>同一次在途请求的Unity绑定回调；成功、失败、取消时都清空，避免持有旧渲染器。</summary>
            public readonly List<Action<SpriteAtlas>> Waiters = new List<Action<SpriteAtlas>>(2);
            /// <summary>加载完成后的共享图集；释放时清null，不把已撤销的租约当成可用缓存。</summary>
            public SpriteAtlas Atlas;
            /// <summary>防重复Release标记；它与Finished分开，因为资源可能还没加载完就退出。</summary>
            public bool Released;
            /// <summary>成功或失败都为true，供启动预热结束等待；失败原因已由回调记录。</summary>
            public bool Finished;
            /// <summary>快速切章可同时存在多个请求；每个背景租约只归还自己的一份，最后一份退出才撤图集。</summary>
            public int TutorialUsers;
            /// <summary>先建立租约记录再调用LoadAsync，兼容资源缓存命中时立即同步回调。</summary>
            public Entry(string name) { Name = name; }
        }

        /// <summary>框架初始化后注册。不要用静态永久事件，否则反复Play会累积监听和加载次数。</summary>
        public GameSpriteAtlasService()
        {
            SpriteAtlasManager.atlasRequested += OnAtlasRequested;
            RevEvent.AddEventListener<GameScreen>(Events.ScreenChanged, OnScreenChanged, owner: this);
        }

        /// <summary>开发验收读取当前租约数量；它表示本类持有数，不代表整个Unity进程纹理数。</summary>
        public int HeldCount { get { return _entries.Count; } }

        /// <summary>第一章的一份明确租约；持有原Entry，旧请求失败后也绝不会扣减后来新请求的计数。</summary>
        public sealed class TutorialSpriteLease : IDisposable
        {
            private readonly GameSpriteAtlasService _owner;
            private readonly Entry _entry;
            private bool _released;
            internal TutorialSpriteLease(GameSpriteAtlasService owner, Entry entry) { _owner = owner; _entry = entry; }

            /// <summary>仅切入时复制一次Sprite，不逐帧查询；副本交给背景拥有并销毁，取消时返回null。</summary>
            public async RevTask<Sprite> LoadAsync(string spriteName)
            {
                while (!_released && !_owner._disposed && !_entry.Released && !_entry.Finished) { await RevTask.Yield(); }
                if (_released || _owner._disposed) { return null; }
                if (_entry.Released || _entry.Atlas == null) { throw new InvalidOperationException("第一章图集加载失败或已释放"); }
                Sprite sprite = _entry.Atlas.GetSprite(spriteName);
                if (sprite == null) { throw new InvalidOperationException("第一章图集不含背景Sprite：" + spriteName); }
                return sprite;
            }

            public void Dispose()
            {
                if (_released) { return; }
                _released = true;
                _owner.ReleaseTutorialSprite(_entry);
            }
        }

        /// <summary>第一章被选中才加载；多个在途背景共享图集，但各自拥有可独立取消的租约。</summary>
        public TutorialSpriteLease AcquireTutorialSprite()
        {
            if (_disposed) { throw new ObjectDisposedException(nameof(GameSpriteAtlasService)); }
            Entry entry = Request(TutorialAtlasName, null);
            entry.TutorialUsers++;
            return new TutorialSpriteLease(this, entry);
        }

        /// <summary>换到其它章节/退出时归还；不强制卸载仍有持有者的包，也不会重复扣减在途请求。</summary>
        private void ReleaseTutorialSprite(Entry entry)
        {
            if (entry.TutorialUsers <= 0) { return; }
            entry.TutorialUsers--;
            if (entry.TutorialUsers > 0) { return; }
            Release(entry);
            if (_entries.TryGetValue(TutorialAtlasName, out Entry current) && ReferenceEquals(current, entry))
            { _entries.Remove(TutorialAtlasName); }
        }

        /// <summary>补上场景加载早于Awake的背景请求。首次真机加载用RevTask等待，首页开放前就绪。</summary>
        public async RevTask PreloadSceneAsync()
        {
            await PreloadAsync(GameSpriteAtlases.SceneNames);
        }

        /// <summary>
        /// 面板打开前的图集屏障。必须先等本屏所需的图集完成，再实例化UI；否则原Sprite已经被AB剥离像素，
        /// Image会暂时使用白纹理。不能把“预制体句柄已成功”当成“依赖的图集已经可渲染”。
        /// 加载仍只走RevResManager，本服务持有唯一租约，调用方不重复Release它。
        /// </summary>
        public async RevTask PreloadAsync(string[] names)
        {
            foreach (string name in names)
            {
                if (!_known.Contains(name))
                {
                    throw new InvalidOperationException("未配置的图集：" + name);
                }
                Entry entry = Request(name, null);
                while (!_disposed && !entry.Finished) { await RevTask.Yield(); }
                if (_disposed)
                {
                    throw new OperationCanceledException("图集服务已释放");
                }
                if (entry.Atlas == null)
                {
                    throw new InvalidOperationException("图集未就绪：" + name);
                }
                // 即使AB已经缓存，也主动让出一帧，避免多张纹理上传挤在同一帧。
                await RevTask.Yield();
            }
        }

        /// <summary>Unity给出的register必须在图集完成后调用；缓存命中也可能同步发生，所以先建Entry再发请求。</summary>
        private void OnAtlasRequested(string tag, Action<SpriteAtlas> register)
        {
            if (_disposed)
            {
                return;
            }
            if (!_known.Contains(tag))
            {
                RevLog.Error("未配置的图集标签：" + tag + "，请重跑资源配置", "RevGame"); return;
            }
            Request(tag, register);
        }

        /// <summary>每个Entry成对Load/Release；失败也记录完成状态，让预热可报错，不会一直等待。</summary>
        private Entry Request(string name, Action<SpriteAtlas> register)
        {
            Entry entry;
            if (_entries.TryGetValue(name, out entry))
            {
                if (entry.Atlas != null)
                {
                    register?.Invoke(entry.Atlas);
                }
                else if (!entry.Finished && register != null)
                {
                    entry.Waiters.Add(register);
                }
                return entry;
            }
            entry = new Entry(name);
            if (register != null)
            {
                entry.Waiters.Add(register);
            }
            _entries.Add(name, entry);
            RevResManager.LoadAsync(RevResPath.Atlases, name, typeof(SpriteAtlas), handle =>
            {
                entry.Finished = true;
                // 退出时已Release的租约不能再减一次，也不能把旧atlas重新交给Unity。
                if (_disposed || entry.Released)
                {
                    entry.Waiters.Clear(); return;
                }
                if (!handle.IsLoaded)
                {
                    entry.Waiters.Clear();
                    RevLog.Error("图集加载失败：" + name + "，" + handle.ErrorReason, "RevGame");
                    Release(entry);
                    _entries.Remove(name);
                    return;
                }
                entry.Atlas = handle.Content as SpriteAtlas;
                for (int i = 0; i < entry.Waiters.Count; i++) { entry.Waiters[i](entry.Atlas); }
                entry.Waiters.Clear();
            }, RevResGroup.Scene);
            // 这里用Scene组是为了避免战斗域force回收误伤同包UI引用；战斗专用图集由下方显式释放。
            return entry;
        }

        /// <summary>离开战斗后延一帧，先让棋盘回池、面板关闭，再撤掉战斗图集租约。</summary>
        private void OnScreenChanged(GameScreen screen)
        {
            int version = ++_screenVersion;
            if (screen != GameScreen.Battle)
            {
                ReleaseBattleAfterViewsAsync(version);
            }
        }

        /// <summary>等本次UI/棋盘退出通知处理完；只有版本没变，才释放这次退出对应的战斗引用。</summary>
        private async void ReleaseBattleAfterViewsAsync(int version)
        {
            await RevTask.Yield();
            if (_disposed || version != _screenVersion)
            {
                return;
            }
            // 仅在流程边界分配这个小清单；不能一边遍历Dictionary一边删除元素。
            var names = new[] { "PieceFrames", "CombatFrames", "BoardStatus" };
            foreach (string name in names)
            {
                Entry entry;
                if (!_entries.TryGetValue(name, out entry))
                {
                    continue;
                }
                Release(entry);
                _entries.Remove(name);
            }
            // 真正卸载留给框架流程的资源整理点；这里不在每次关小面板时强制GC或UnloadUnusedAssets。
        }

        /// <summary>一次性释放自己的引用并断开等待者。重复退出、迟到回调不会导致负引用计数。</summary>
        private static void Release(Entry entry)
        {
            if (entry.Released)
            {
                return;
            }
            entry.Released = true;
            entry.Waiters.Clear();
            entry.Atlas = null;
            RevResManager.Release(RevResPath.Atlases, entry.Name);
        }

        /// <summary>先退订事件再释放资源，防止卸载过程中又收到新请求；由Bootstrap反向注销服务调用。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            SpriteAtlasManager.atlasRequested -= OnAtlasRequested;
            RevEvent.RemoveAllByOwner(this);
            foreach (Entry entry in _entries.Values) { Release(entry); }
            _entries.Clear();
        }
    }
}
