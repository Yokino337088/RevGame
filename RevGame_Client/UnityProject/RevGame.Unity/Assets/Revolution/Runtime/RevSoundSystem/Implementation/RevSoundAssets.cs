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

        private sealed class Entry
        {
            internal AudioClip Clip;
            internal string Root;
            internal string Name;
            internal bool Loading;
            internal bool Failed;
            internal RevResHandle Handle;
        }

        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);
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

        // 同名文件可以同时存在于 Audio/Sfx 和 Audio/Bgm。只用 name 作字典键会让先加载的一类挡住另一类，
        // 例如先请求 SFX/theme，再请求 BGM/theme 时就会错误复用 SFX 的 clip；把资源根目录也纳入键，两个缓存相互独立。
        private static string CacheKey(string root, string name) => root + "\0" + name;

        /// <summary>已加载好的片段（没加载好返回 null）。BGM 与 SFX 根目录不同，不能只按同名片段查表。</summary>
        internal AudioClip Find(string name, bool bgm)
        {
            string root = bgm ? RevSoundPath.Bgm : RevSoundPath.Sfx;
            return _map.TryGetValue(CacheKey(root, name), out Entry entry) ? entry.Clip : null;
        }

        /// <summary>加载是否失败过（失败会缓存，避免每帧重试打爆加载队列；Unload 后可重试）。</summary>
        internal bool IsFailed(string name, bool bgm)
        {
            string root = bgm ? RevSoundPath.Bgm : RevSoundPath.Sfx;
            return _map.TryGetValue(CacheKey(root, name), out Entry entry) && entry.Failed;
        }

        /// <summary>请求加载（以完整资源位置区分缓存，已加载/加载中/失败过都不重复请求）。</summary>
        internal void Request(string name, bool bgm)
        {
            string root = bgm ? RevSoundPath.Bgm : RevSoundPath.Sfx;
            string key = CacheKey(root, name);
            if (_map.TryGetValue(key, out Entry entry))
            {
                if (entry.Clip != null || entry.Loading || entry.Failed) return;
            }
            else
            {
                entry = new Entry { Root = root, Name = name };
                _map[key] = entry;
            }

            entry.Loading = true;
            Entry requestEntry = entry;

            // ResourceSystem 返回的 handle 是本次音频缓存请求持有的引用；完成后 Unload 会通过这张句柄准确归还它。
            requestEntry.Handle = RevResManager.LoadAsync<AudioClip>(root, name,
                clip =>
                {
                    requestEntry.Loading = false;
                    requestEntry.Clip = clip;
                    requestEntry.Failed = clip == null;
                },
                RevResGroup.Sound, RevResLoadPriority.Urgent);
        }

        /// <summary>预加载（= 请求加载；加载完常驻，直到 Unload）。</summary>
        internal void Preload(string name, bool bgm) => Request(name, bgm);

        /// <summary>
        /// 卸载这个名字在 SFX 和 BGM 两个默认目录下的缓存项；API 没有 bgm 参数，所以相同名字的两类资源都尝试卸载。
        /// 播放器已经把 AudioClip 交给 AudioSource，释放资源缓存引用不会突然掐断正在播放的声音。
        /// </summary>
        internal void Unload(string name)
        {
            Unload(RevSoundPath.Sfx, name);
            Unload(RevSoundPath.Bgm, name);
        }

        private void Unload(string root, string name)
        {
            string key = CacheKey(root, name);
            if (!_map.TryGetValue(key, out Entry entry)) return;

            // 先从本地表摘掉，之后的 Request 会建立/加入一个新的有效条目。
            // 即使异步 LoadAsync 尚未回调，entry.Handle 也代表本次请求已经占用的资源引用；现在归还它，不能因 Loading=true 而跳过。
            // 旧回调仍只更新旧 Entry，不会重新放回 _map；若新请求共用同一个 ResourceSystem 句柄，它自己的引用计数仍独立保留。
            _map.Remove(key);
            if (entry.Handle != null && entry.Handle.Key != 0)
                RevResManager.DecRef(entry.Handle);
            else
                RevResManager.Release(entry.Root, name);
        }

        /// <summary>卸载全部缓存项，并逐一归还每次异步加载所持有的资源引用。</summary>
        internal void UnloadAll()
        {
            if (_map.Count == 0) return;

            var entries = new List<KeyValuePair<string, Entry>>(_map);
            _map.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i].Value;
                if (entry.Handle != null && entry.Handle.Key != 0)
                    RevResManager.DecRef(entry.Handle);
                else if (!string.IsNullOrEmpty(entry.Root))
                {
                    RevResManager.Release(entry.Root, entry.Name);
                }
            }
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
