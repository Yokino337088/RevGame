// ============================================================
// EndlessMusicPlaylist.cs —— 无尽模式的 BGM 轮播（新手先读这段）
//
// 【为什么不直接用 RevSound.PlayBgmList】
// 歌曲是最吃内存的资源之一。要求是：内存里平时只留一首歌，
// 快播完才去加载下一首，切完马上卸载上一首。这需要知道“当前还剩几秒”，
// 而 RevSound 的播放句柄读不到进度，所以这里自己管理 AudioClip 的加载和释放。
//
// 【一首歌的生命周期】
//   1. 播放中：内存里只有这一首（一个 AB 包）。
//   2. 还剩 MusicPreloadLead 秒：异步加载下一首的 AB 包（LoadAsync 不卡主线程）。
//   3. 还剩 MusicCrossfade 秒且下一首已就绪：两首交叉淡入淡出，此时内存里短暂有两首。
//   4. 淡出结束：停掉旧歌，Release 句柄。引用计数归零后框架卸载它的 AB 包。
//
// 【下一首没加载好怎么办】
// 当前曲目开了循环，所以来不及时只是多播一遍开头，不会出现静音。加载好后再切。
//
// 【加载失败怎么办】
// 记日志并跳过这首，继续尝试再下一首；全部失败就保持当前曲目循环，绝不让音乐报错影响战斗。
// ============================================================
using System;
using Revolution;
using RevGame.Services;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>无尽 BGM 播放器，由 EndlessArena 创建和销毁，不是全局单例：退出无尽就彻底释放。</summary>
    public sealed class EndlessMusicPlaylist : IDisposable
    {
        /// <summary>一首歌的加载状态。把“是哪首”和“持有的资源句柄”放在一起，释放时不会弄错。</summary>
        private sealed class Track
        {
            /// <summary>曲目在表里的序号。</summary>
            public int Index;
            /// <summary>资源根目录，释放时要和加载时一致。</summary>
            public string Root;
            /// <summary>资源名，释放时要和加载时一致。</summary>
            public string Name;
            /// <summary>加载完成后的音频；null 表示还没加载好。</summary>
            public AudioClip Clip;
            /// <summary>加载是否已失败。</summary>
            public bool Failed;
        }

        /// <summary>两个声源轮流用：一个放当前歌，另一个在交叉淡入时放下一首。</summary>
        private readonly AudioSource[] _sources = new AudioSource[2];
        /// <summary>_sources 里哪个是“当前歌”（0 或 1）。</summary>
        private int _front;
        /// <summary>正在播放的歌。</summary>
        private Track _current;
        /// <summary>正在淡出、淡完就要卸载的上一首。</summary>
        private Track _fading;
        /// <summary>已经开始加载、等待切入的下一首。</summary>
        private Track _next;
        /// <summary>交叉淡入淡出的进度：0 刚开始，1 结束；小于 0 表示当前没有在淡入淡出。</summary>
        private float _fade = -1;
        /// <summary>曲目总数，从配置表读取。</summary>
        private readonly int _count;
        /// <summary>对象已销毁标记：异步加载回来时要检查，防止给已释放的对象赋值。</summary>
        private bool _disposed;

        /// <summary>当前播放的资源名，诊断和自动化测试用。</summary>
        public string CurrentName => _current?.Name ?? "";
        /// <summary>等待切入的资源名，诊断和自动化测试用。</summary>
        public string NextName => _next?.Name ?? "";
        /// <summary>淡出中的资源名，诊断和自动化测试用。</summary>
        public string FadingName => _fading?.Name ?? "";

        /// <summary>创建两个声源并开始加载第一首。声源挂在 host 下，随竞技场一起销毁。</summary>
        public EndlessMusicPlaylist(GameObject host)
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                _sources[i] = host.AddComponent<AudioSource>();
                _sources[i].playOnAwake = false;
                _sources[i].loop = true;
                _sources[i].spatialBlend = 0;
                _sources[i].ignoreListenerPause = true;
            }
            _count = RevDataTableManager.Get<EndlessMusicTable>().All.Count;
            _current = Load(0);
        }

        /// <summary>按序号异步加载一首歌，回调里记录结果。LoadAsync 不阻塞主线程。</summary>
        private Track Load(int index)
        {
            EndlessMusic row = EndlessConfig.Music(index);
            var track = new Track { Index = index, Root = row.Root, Name = row.Sound };
            RevResManager.LoadAsync<AudioClip>(row.Root, row.Sound, clip =>
            {
                track.Clip = clip;
                track.Failed = clip == null;
                if (clip == null)
                {
                    RevLog.Warn("无尽BGM加载失败，将跳过：" + row.Sound, "RevGame");
                }
                // 对象已释放时，这个迟到的结果要立刻还回去，否则 AB 包永远卸不掉。
                if (_disposed && clip != null)
                {
                    RevResManager.Release(track.Root, track.Name);
                }
            }, RevResGroup.Scene);
            return track;
        }

        /// <summary>每帧调用一次，只比较几个数字，不产生垃圾，放在 Update 里没有性能负担。</summary>
        public void Tick(float deltaTime)
        {
            if (_disposed)
            {
                return;
            }
            float volume = RevSound.GetVolume(RevSoundKind.Bgm) * GameAudioSettings.MusicBaseVolume;
            StartFirstIfReady();
            UpdateFade(deltaTime, volume);
            if (_fade < 0)
            {
                _sources[_front].volume = volume;
                PrepareNext();
            }
        }

        /// <summary>第一首加载好后立刻开始播放；失败则换下一首。</summary>
        private void StartFirstIfReady()
        {
            if (_current == null || _sources[_front].clip != null)
            {
                return;
            }
            if (_current.Failed)
            {
                _current = _current.Index + 1 < _count ? Load(_current.Index + 1) : null;
                return;
            }
            if (_current.Clip != null)
            {
                _sources[_front].clip = _current.Clip;
                _sources[_front].Play();
            }
        }

        /// <summary>检查剩余时间：快播完就加载下一首，再近一点就开始切歌。</summary>
        private void PrepareNext()
        {
            AudioSource source = _sources[_front];
            // 曲目少于两首时没有“下一首”，循环播放即可。
            if (source.clip == null || _count < 2)
            {
                return;
            }
            float remaining = source.clip.length - source.time;
            if (_next == null && remaining <= EndlessConfig.Value("MusicPreloadLead"))
            {
                _next = Load((_current.Index + 1) % _count);
            }
            if (_next == null)
            {
                return;
            }
            if (_next.Failed)
            {
                // 下一首坏了就试再下一首；试到当前这首说明全坏了，保持循环。
                int skip = (_next.Index + 1) % _count;
                _next = skip == _current.Index ? null : Load(skip);
                return;
            }
            if (_next.Clip != null && remaining <= EndlessConfig.Value("MusicCrossfade"))
            {
                BeginFade();
            }
        }

        /// <summary>把下一首放到另一个声源上，从音量 0 开始播放。</summary>
        private void BeginFade()
        {
            int back = 1 - _front;
            _sources[back].clip = _next.Clip;
            _sources[back].volume = 0;
            _sources[back].time = 0;
            _sources[back].Play();
            _fading = _current;
            _current = _next;
            _next = null;
            _front = back;
            _fade = 0;
        }

        /// <summary>
        /// 推进交叉淡入淡出。音量用平方根曲线（等功率淡变）：
        /// 如果线性淡变，两首都是 50% 音量时听感会明显变小，中间像掉了一块。
        /// </summary>
        private void UpdateFade(float deltaTime, float volume)
        {
            if (_fade < 0)
            {
                return;
            }
            _fade = Mathf.Min(1, _fade + deltaTime / EndlessConfig.Value("MusicCrossfade"));
            _sources[_front].volume = volume * Mathf.Sqrt(_fade);
            _sources[1 - _front].volume = volume * Mathf.Sqrt(1 - _fade);
            if (_fade >= 1)
            {
                FinishFade();
            }
        }

        /// <summary>淡出结束：先让声源放开音频，再释放资源，顺序反了会听到爆音或读到已卸载的数据。</summary>
        private void FinishFade()
        {
            int back = 1 - _front;
            _sources[back].Stop();
            _sources[back].clip = null;
            Release(_fading);
            _fading = null;
            _fade = -1;
        }

        /// <summary>归还一首歌占用的资源引用。引用数归零后，框架卸载它的 AB 包并回收内存。</summary>
        private static void Release(Track track)
        {
            if (track != null && track.Clip != null)
            {
                RevResManager.Release(track.Root, track.Name);
                track.Clip = null;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>测试用：把当前曲目跳到结尾前 remaining 秒，不用真的等一首歌播完。</summary>
        public void DebugSeek(float remaining)
        {
            AudioSource source = _sources[_front];
            if (source.clip != null)
            {
                source.time = Mathf.Max(0, source.clip.length - remaining);
            }
        }

        /// <summary>测试用：当前曲目剩余秒数。</summary>
        public float DebugRemaining => _sources[_front].clip == null ? -1 : _sources[_front].clip.length - _sources[_front].time;
#endif

        /// <summary>退出无尽时调用：停止播放并释放所有曲目，内存里不留任何一首无尽 BGM。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            for (int i = 0; i < _sources.Length; i++)
            {
                if (_sources[i] != null)
                {
                    _sources[i].Stop();
                    _sources[i].clip = null;
                    UnityEngine.Object.Destroy(_sources[i]);
                }
            }
            Release(_current);
            Release(_next);
            Release(_fading);
            _current = _next = _fading = null;
        }
    }
}
