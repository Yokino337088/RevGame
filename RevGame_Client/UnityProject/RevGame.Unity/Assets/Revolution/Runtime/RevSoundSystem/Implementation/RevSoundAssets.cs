// RevSoundAssets.cs —— 资源层：音效片段（接 RevResourceSystem）+ 播放器（接 RevObjectPool）
//
// 【为什么单独一层】"怎么拿到 clip、怎么复用 AudioSource"是内核里唯一与资源系统/对象池打交道的地方，
//   收在一个文件里 → 换资源后端、换池实现都只改这里（对应王者"策略可插拔、内核无知"）。
//
// 【只用异步加载，绝不用同步】RevResourceSystem 的同步 Load 在"某个异步加载正在路上"时会给你一个 Content == null
//   的句柄（真坑），而 WebGL/小游戏同步加载直接失败。所以本层**只调 LoadAsync**，首次播放晚一点出声是可以接受的。
//
// 【语音/音效的引用怎么还】加载成功 = 欠资源系统一份引用；本层把它记在缓存里，直到 Unload / UnloadAll 才还。
//   正在播的 AudioSource 自己还持有 clip 引用，所以 Unload 不会把"正在响的声音"掐断（引用归零只是不再常驻）。
//
// 【播放器池】用公开的 RevPoolCore<AudioSource> 自建池（不依赖任何预制体：小白零资产就能跑）。
//   RevPool 的预制体池要求你先做一个带 AudioSource 的 prefab —— 那是"开箱即用"的障碍，这里刻意不用。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    internal sealed class RevSoundAssets
    {
        // ============================================================
        // 音频放哪（上层 API 只认"名字"；目录由打包工具窗口配置，运行期读常量）
        //
        //   ① 在「Revolution.Tools/资源/RevAB 打包工具」窗口的①配置里选两个目录
        //      （文件夹槽 / 选择… / 默认）→ 值存进 Assets/Editor/ABBuildConfig.asset（团队共享）；
        //   ② 窗口改完自动重新生成 RevSoundPath.cs（生成到 Runtime 程序集内部，见 Generated\）；
        //   ③ 本文件读 RevSoundPath 常量 → 零运行期 IO，目录写错编译不过。
        //
        // 【为什么不像 RevResPath 那样放 Generation 程序集】
        //   Revolution.Runtime.asmdef 的 references 是空的，而 Generation 反过来引用了 Runtime ——
        //   Runtime 反向引用 Generation 会成环，所以音效路径常量必须生成在 Runtime 内部。
        // ============================================================

        /// <summary>音频底层租约及使用范围。跨域预加载和域内播放可共用同一份加载。</summary>
        private sealed class Entry
        {
            /// <summary>当前代成功加载的音频对象，未到或失败时为空。</summary>
            internal AudioClip Clip;
            /// <summary>音频逻辑根目录。</summary>
            internal string Root;
            /// <summary>音频逻辑名，支持根目录下的相对子目录。</summary>
            internal string Name;
            /// <summary>共用的读取当前是否还未完成。</summary>
            internal bool Loading;
            /// <summary>同一代读取失败时保留结果，避免反复请求。</summary>
            internal bool Failed;
            /// <summary>共享音频缓存自己持有的资源引用，摘除条目时配对归还。</summary>
            internal RevResHandle Handle;
            /// <summary>显式预加载或全局声音保留到宿主主动卸载。</summary>
            internal bool Pinned;
            /// <summary>曾使用这一音频且尚未退出的业务域。</summary>
            internal HashSet<RevScopeHandle> Scopes;
        }

        /// <summary>按音频根目录与名称查缓存。查播放音频不拼接字符串。</summary>
        private readonly Dictionary<(string root, string name), Entry> _map = new Dictionary<(string root, string name), Entry>();
        /// <summary>每个域触及的实际缓存条目。退域不扫描完整音效目录。</summary>
        private readonly Dictionary<RevScopeHandle, HashSet<Entry>> _entriesByScope = new Dictionary<RevScopeHandle, HashSet<Entry>>();
        private RevPoolCore<AudioSource> _sources;
        private Transform _root;

        // ============================================================
        // 初始化 / 释放
        // ============================================================

        internal Transform Root => _root;

        internal void Initialize(Transform root)
        {
            if (_sources != null) return;

            _root = root;

            _sources = new RevPoolCore<AudioSource>(
                name: "RevSoundVoice",
                capacity: 32,                                    // 空闲上限：超出的归还直接销毁（在用数量不受限）
                create: () =>
                {
                    GameObject go = new GameObject("RevSoundVoice");
                    go.transform.SetParent(_root, false);
                    AudioSource source = go.AddComponent<AudioSource>();
                    source.playOnAwake = false;                  // ★ 必须：否则每次取出都可能自动播一次
                    return source;
                },
                isAlive: source => source != null,
                onTake: source =>
                {
                    source.gameObject.SetActive(true);
                    source.enabled = true;
                    source.Stop();
                    source.clip = null;
                },
                onPut: source =>
                {
                    // 归还时的复位（对应 RevPool 里说的"OnPoolReturn → 停止音效、清空数据"）
                    source.Stop();
                    source.clip = null;
                    source.loop = false;
                    source.volume = 1f;
                    source.pitch = 1f;
                    source.spatialBlend = 0f;
                    source.transform.SetParent(_root, false);   // ★ 3D 音效是挂在目标物体下的，归还时先摘回来
                    source.transform.localPosition = Vector3.zero;
                    source.gameObject.SetActive(false);
                },
                onDestroy: source =>
                {
                    if (source != null) UnityEngine.Object.Destroy(source.gameObject);
                });
        }

        /// <summary>整体释放：还掉所有 clip 引用 + 销毁空闲播放器</summary>
        internal void Dispose()
        {
            UnloadAll();
            _sources?.Trim(0);
            _sources = null;
            _root = null;
        }

        // ============================================================
        // 音效片段（RevResourceSystem）
        // ============================================================

        /// <summary>已加载好的片段（没加载好返回 null）。BGM 与 SFX 根目录不同，不能只按同名片段查表。</summary>
        internal AudioClip Find(string name, bool bgm)
        {
            string root = bgm ? RevSoundPath.Bgm : RevSoundPath.Sfx;
            return _map.TryGetValue((root, name), out Entry entry) ? entry.Clip : null;
        }

        /// <summary>加载是否失败过（失败会缓存，避免每帧重试打爆加载队列；Unload 后可重试）。</summary>
        internal bool IsFailed(string name, bool bgm)
        {
            string root = bgm ? RevSoundPath.Bgm : RevSoundPath.Sfx;
            return _map.TryGetValue((root, name), out Entry entry) && entry.Failed;
        }

        /// <summary>登记本次请求的域，再复用或读取片段。无域请求显式保留到 Unload。</summary>
        internal AudioClip Request(string name, bool bgm, RevScopeHandle scope)
        {
            string root = bgm ? RevSoundPath.Bgm : RevSoundPath.Sfx;
            var key = (root, name);
            if (!_map.TryGetValue(key, out Entry entry))
            {
                entry = new Entry { Root = root, Name = name };
                _map[key] = entry;
            }
            Track(entry, scope);
            if (entry.Clip != null || entry.Loading || entry.Failed)
            {
                return entry.Clip;
            }

            // 归属在调用加载器前提交，以便缓存同步完成、预加载并发及域内退出均能清理同一条记录。
            entry.Loading = true;
            entry.Handle = RevResManager.LoadAsync<AudioClip>(root, name,
                clip =>
                {
                    entry.Loading = false;
                    entry.Clip = clip;
                    entry.Failed = clip == null;
                },
                RevResGroup.Sound, RevResLoadPriority.Urgent, RevResLifetime.Manual);
            if (!_map.TryGetValue(key, out Entry current) || !ReferenceEquals(current, entry))
            {
                RevResManager.DecRef(entry.Handle);
                return null;
            }
            return entry.Clip;
        }

        /// <summary>以明确的独立持有预热片段，退出发起域时继续可用。</summary>
        internal void Preload(string name, bool bgm) => Request(name, bgm, scope: null);

        /// <summary>为首次消费此条目的域建立双向索引，重复播放无需分配引用对象。</summary>
        private void Track(Entry entry, RevScopeHandle scope)
        {
            if (scope == null)
            {
                entry.Pinned = true;
                return;
            }
            if ((entry.Scopes ??= new HashSet<RevScopeHandle>()).Add(scope))
            {
                if (!_entriesByScope.TryGetValue(scope, out HashSet<Entry> entries))
                {
                    entries = new HashSet<Entry>();
                    _entriesByScope.Add(scope, entries);
                }
                entries.Add(entry);
            }
        }

        /// <summary>归还退出域的音频使用权，仍有其它域或独立预加载使用时继续保留。</summary>
        internal int ReleaseScope(RevScopeHandle scope)
        {
            if (!_entriesByScope.TryGetValue(scope, out HashSet<Entry> entries))
            {
                return 0;
            }
            _entriesByScope.Remove(scope);
            int released = 0;
            foreach (Entry entry in entries)
            {
                entry.Scopes.Remove(scope);
                if (entry.Scopes.Count == 0 && !entry.Pinned)
                {
                    _map.Remove((entry.Root, entry.Name));
                    RevResManager.DecRef(entry.Handle);
                    released++;
                }
            }
            return released;
        }

        /// <summary>
        /// 卸载这个名字在 SFX 和 BGM 两个默认目录下的缓存项；API 没有 bgm 参数，所以相同名字的两类资源都尝试卸载。
        /// 播放器已经把 AudioClip 交给 AudioSource，释放资源缓存引用不会突然掐断正在播放的声音。
        /// </summary>
        internal void Unload(string name)
        {
            Unload(RevSoundPath.Sfx, name);
            Unload(RevSoundPath.Bgm, name);
        }

        /// <summary>显式卸载一个缓存条目并移除反向归属。晚到回执只写原来已被摘除的条目。</summary>
        private void Unload(string root, string name)
        {
            var key = (root, name);
            if (!_map.TryGetValue(key, out Entry entry))
            {
                return;
            }
            _map.Remove(key);
            if (entry.Scopes != null)
            {
                foreach (RevScopeHandle scope in entry.Scopes)
                {
                    if (_entriesByScope.TryGetValue(scope, out HashSet<Entry> entries))
                    {
                        entries.Remove(entry);
                        if (entries.Count == 0)
                        {
                            _entriesByScope.Remove(scope);
                        }
                    }
                }
            }
            RevResManager.DecRef(entry.Handle);
        }

        /// <summary>卸载全部缓存项，并逐一归还每次异步加载所持有的资源引用。</summary>
        internal void UnloadAll()
        {
            foreach (Entry entry in _map.Values)
            {
                RevResManager.DecRef(entry.Handle);
            }
            _map.Clear();
            _entriesByScope.Clear();
        }

        // ============================================================
        // 播放器（RevObjectPool）
        // ============================================================

        internal AudioSource RentSource() => _sources?.Get();

        internal void ReturnSource(AudioSource source)
        {
            if (source == null) return;
            _sources?.Return(source);
        }

        /// <summary>每帧推进对象池（延迟回收用；本层不传 delayFrames，调用它是为了将来改配置时也不会漏）</summary>
        internal void Tick() => _sources?.Tick();
    }
}
