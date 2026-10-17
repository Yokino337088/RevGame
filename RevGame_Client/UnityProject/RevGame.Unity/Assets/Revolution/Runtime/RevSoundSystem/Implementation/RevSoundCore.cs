// RevSoundCore.cs —— 内核：加载 → 播放 → 每帧推进 → 回收（框架里唯一"懂 Unity 音频"的地方）
//
// 【职责】把"播一个音效"翻译成一串动作：策略放行 → 同帧去重 → 上限淘汰 → 占槽位 → 拿播放器 → 播放；
//   之后每帧：等资源的自动开播、淡入淡出、播完回收。
//
// 【2D / 3D 怎么区分】
//   2D：播放器留在框架根节点下，spatialBlend = 0（跟着听者，不受距离影响）。
//   3D：播放器**直接挂到你给的那个 GameObject 下面**（localPosition = 0）——
//       位置由父子关系自动更新（零每帧开销），物体被销毁时播放器一起销毁（声音自动收，业务不用管）。
//       PlayAt 则留在根节点下、把世界坐标设到你给的那个点。
//   （对应王者"3D 音效的职责是给后端正确的 emitter"：这里就是把播放器挂对地方，衰减交给 Unity。）
//
// 【与王者的对应关系（哪些抄、哪些丢）】
//   抄：异步是常态（资源没到 → 立即返回句柄，到了自动播，业务看不见异步窗口）；
//       淘汰先于提交（上限满了先淘汰最旧的一次性音效，再决定播不播）；分帧只遍历活跃槽位；
//       播放器复用（对应它的 emitter 缓存 / AkGameObj 预创建）。
//   丢：占位 ID 三张映射表 + 全表遍历（这里一个数组 + 槽位 + 代际号，O(1)）；
//       异步窗口内的 Stop/RTPC 定盘缓存（这里"停"就是对槽位打标记，天生正确）；
//       3539 行上帝类（这里是 ~300 行，且内核不知道任何业务规则）。
//
// 【线程】只在主线程使用（Unity 音频 API 的要求）。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 音效内核：可自己 new 一个独立实例（对应"内核不是单例"），日常用 <see cref="RevSound"/> 静态门面即可。
    /// </summary>
    public sealed class RevSoundCore : IRevSoundHandleOwner
    {
        private const int SlotCapacity = 64;      // 声音槽位硬上限（MaxVoices 只能更小）

        private readonly RevSoundVoiceTable _table = new RevSoundVoiceTable(SlotCapacity);
        private readonly RevSoundAssets _assets = new RevSoundAssets();
        private readonly RevSoundCatalog _catalog = new RevSoundCatalog();

        // Unity 侧对象：与槽位下标一一对应（槽位表是纯 C# 的，这些是它对应的"实体"）
        private readonly AudioSource[] _sources = new AudioSource[SlotCapacity];
        private readonly Vector3[] _positions = new Vector3[SlotCapacity];    // PlayAt：挂在哪个世界坐标点
        private readonly Transform[] _targets = new Transform[SlotCapacity];  // PlayOn：挂在哪个物体下面
        private readonly bool[] _requiresTarget = new bool[SlotCapacity];
        private readonly bool[] _is3D = new bool[SlotCapacity];

        private readonly float[] _kindVolumes = { 1f, 1f, 1f, 1f };
        private int _maxVoices = 24;
        private float _master = 1f;
        private float _minDistance = 1f;
        private float _maxDistance = 50f;
        private int _bgmSlot = -1;
        private List<string> _bgmList;
        private int _bgmIndex;
        private bool _initialized;

        /// <summary>总开关：false 之后 <see cref="Play"/> 直接空操作</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>静音：音量按 0 处理（声音继续走）</summary>
        public bool Mute
        {
            get => _mute;
            set
            {
                if (_mute == value) return;
                _mute = value;
                ApplyVolumes();
            }
        }
        private bool _mute;

        /// <summary>主音量（最终音量 = 单次音量 × 分类音量 × 主音量）</summary>
        public float MasterVolume
        {
            get => _master;
            set
            {
                float next = Clamp01(value);
                if (Mathf.Approximately(_master, next)) return;
                _master = next;
                ApplyVolumes();
            }
        }

        /// <summary>同一帧同名音效是否只播一次</summary>
        public bool FrameDedupe { get; set; } = true;

        /// <summary>同时在播的声音上限（1~64；超出时淘汰最旧的一次性音效）</summary>
        public int MaxVoices
        {
            get => _maxVoices;
            set => _maxVoices = value < 1 ? 1 : (value > SlotCapacity ? SlotCapacity : value);
        }

        /// <summary>业务策略（"该不该播"的门面钩子；为 null 表示全放行）</summary>
        public IRevSoundPolicy Policy { get; set; }

        /// <summary>播放没能发生时触发（音效名 + 原因）</summary>
        public event Action<string, RevSoundErrorReason> Failed;

        /// <summary>一条声音自然播完时触发（手动 Stop 不触发）</summary>
        public event Action<RevSoundHandle> VoiceFinished;

        /// <summary>是否已被手动驱动接管（一旦调用过 <see cref="Tick"/>，就不再自动创建宿主）</summary>
        internal bool ManualDriven { get; private set; }

        // ============================================================
        // 播放
        // ============================================================

        /// <summary>
        /// 播放一条声音（门面与作用域最终都走到这里）。
        /// </summary>
        /// <param name="position">PlayAt：挂在哪个世界坐标点</param>
        /// <param name="target">PlayOn：挂到哪个物体下面（3D 音效随它走，它没了声音就停）</param>
        /// <param name="is3D">true = 3D（有距离衰减，必须给 target 或 position）；false = 2D（直接播）</param>
        /// <param name="kind">null = 用音效表里的分类，表里也没有则用 Sfx</param>
        /// <param name="volume">null = 用音效表里的音量，表里也没有则用分类默认音量</param>
        /// <param name="loop">null = 用音效表里的循环设置，表里也没有则用分类默认值</param>
        internal RevSoundHandle Play(string name, RevSoundKind? kind, float? volume, Vector3? position,
                                     Transform target, bool is3D, bool? loop)
            => PlayInternal(name, kind, volume, position, target, is3D, loop, out _);

        private RevSoundHandle PlayInternal(string name, RevSoundKind? kind, float? volume, Vector3? position,
                                            Transform target, bool is3D, bool? loop, out int slot)
        {
            slot = -1;

            if (!Enabled) return RevSoundHandle.Empty;

            // 音效名 = "相对音效根目录的资源路径"（可带子目录）：先统一写法（反斜杠 / 多余斜杠 / 空格）
            string logicalName = RevResPathUtil.NormalizeResName(name);
            if (logicalName.Length == 0) return RevSoundHandle.Empty;

            // 音效表：登记过就把"逻辑名"翻成真实路径，并补齐调用点没写的参数（显式传的永远优先）
            string path = _catalog.ResolvePath(logicalName, ref kind, ref volume, ref loop);
            RevSoundKind resolvedKind = kind ?? RevSoundKind.Sfx;

            EnsureInitialized();

            // 3D 必须知道"声音从哪发出"：PlayOn 的目标为 null（物体已销毁）时明确报告，
            // 绝不偷偷降级成 2D（否则技能音会突然变成"贴脸满音量"，更难排查）
            if (is3D && target == null && !position.HasValue)
            {
                RaiseFailed(logicalName, RevSoundErrorReason.NoTarget);
                return RevSoundHandle.Empty;
            }

            if (Policy != null && !Policy.CanPlay(logicalName, resolvedKind))
            {
                RaiseFailed(logicalName, RevSoundErrorReason.PolicyRejected);
                return RevSoundHandle.Empty;
            }

            RevSoundKindInfo info = RevSoundKindTable.Of(resolvedKind);

            // 同帧去重按"真实路径 + 分类"判定：两个逻辑名指向同一条音效时，也算重复
            if (FrameDedupe && info.FrameDedupe && !_table.TryMarkFrame(path, resolvedKind))
            {
                RaiseFailed(logicalName, RevSoundErrorReason.Duplicated);
                return RevSoundHandle.Empty;
            }

            if (_table.AliveCount >= _maxVoices)
            {
                int evict = _table.FindOldestOneShot();
                if (evict < 0)
                {
                    RaiseFailed(logicalName, RevSoundErrorReason.TooManyVoices);
                    return RevSoundHandle.Empty;
                }

                Recycle(evict, finished: false);        // 淘汰最旧的一次性音效（先腾地方，再决定播不播）
            }

            float resolvedVolume = volume.HasValue && volume.Value > 0f ? volume.Value : info.Volume;
            slot = _table.Rent(path, resolvedKind, resolvedVolume, loop ?? info.Loop, out int generation);
            if (slot < 0)
            {
                RaiseFailed(logicalName, RevSoundErrorReason.TooManyVoices);
                return RevSoundHandle.Empty;
            }

            _positions[slot] = position ?? Vector3.zero;
            _targets[slot] = target;
            // PlayOn 与 PlayAt 都是 3D，但只有 PlayOn 必须跟随一个物体。单靠 target == null 无法区分“物体后来被销毁”和“本来就指定世界坐标”，
            // 所以在创建声音槽位时记下调用方式，避免异步加载期间目标销毁后误把声音播到世界原点。
            _requiresTarget[slot] = is3D && !position.HasValue && target != null;
            _is3D[slot] = is3D;

            bool isBgm = resolvedKind == RevSoundKind.Bgm;
            AudioClip clip = _assets.Find(path, isBgm);
            if (clip != null)
            {
                StartVoice(slot, clip);
            }
            else
            {
                // 资源还没到：先占着槽位（业务已经拿到句柄），加载完自动开播
                _table.Slot(slot).Pending = true;
                _assets.Request(path, resolvedKind == RevSoundKind.Bgm);
            }

            // StartVoice 可能发现 AudioSource 无法创建，或 PlayOn 的目标刚好已销毁并提前回收槽位。
            // 槽位若已释放，就不能再返回一个看起来有效、实际无法控制任何声音的句柄。
            return _table.IsAlive(slot, generation) ? new RevSoundHandle(this, slot, generation) : RevSoundHandle.Empty;
        }

        private void StartVoice(int slot, AudioClip clip)
        {
            ref RevSoundSlot state = ref _table.Slot(slot);
            if (_requiresTarget[slot] && _targets[slot] == null)
            {
                string name = state.Name;
                Recycle(slot, finished: false);
                RaiseFailed(name, RevSoundErrorReason.NoTarget);
                return;
            }

            AudioSource source = _assets.RentSource();
            if (source == null)
            {
                Recycle(slot, finished: false);
                return;
            }

            // ★ 3D 音效直接挂到目标物体下面：位置由父子关系自动维护（零每帧开销），物毁音停
            Transform target = _targets[slot];
            if (target != null)
            {
                source.transform.SetParent(target, false);
                source.transform.localPosition = Vector3.zero;
            }
            else
            {
                source.transform.position = _positions[slot];    // PlayAt：挂在世界坐标点（2D 用不到）
            }

            _sources[slot] = source;
            source.clip = clip;
            source.loop = state.Loop;
            source.spatialBlend = _is3D[slot] ? 1f : 0f;         // 2D 不受距离影响；3D 才有衰减
            source.minDistance = _minDistance;
            source.maxDistance = _maxDistance;
            source.volume = state.FadeSpeed > 0f ? 0f : FinalVolume(state.Kind, state.Volume);   // 淡入的从 0 起步
            source.Play();
        }

        // ============================================================
        // 背景音乐（单曲 / 歌单）
        // ============================================================

        internal RevSoundHandle PlayBgm(string name, float? volume, float fadeSeconds)
        {
            _bgmList = null;
            return StartBgm(name, volume, fadeSeconds, loop: true);
        }

        /// <summary>歌单：一首播完自动下一首（保留你旧框架 MusicMgr 的列表播放能力，去掉它的 Timer 轮询）</summary>
        internal void PlayBgmList(IReadOnlyList<string> names, float volume, float fadeSeconds)
        {
            if (names == null || names.Count == 0)
            {
                StopBgm(fadeSeconds);
                return;
            }

            _bgmList = new List<string>(names);
            _bgmIndex = 0;
            StartBgm(_bgmList[0], volume, fadeSeconds, loop: _bgmList.Count == 1);
        }

        internal void StopBgm(float fadeSeconds)
        {
            _bgmList = null;

            if (_bgmSlot >= 0 && _table.Slot(_bgmSlot).Alive) FadeOut(_bgmSlot, fadeSeconds);
            _bgmSlot = -1;
        }

        private RevSoundHandle StartBgm(string name, float? volume, float fadeSeconds, bool loop)
        {
            // 旧 BGM 淡出（不立刻掐断，切歌不突兀）；它自己会淡完回收，不影响新的一条
            if (_bgmSlot >= 0 && _table.Slot(_bgmSlot).Alive) FadeOut(_bgmSlot, fadeSeconds > 0f ? fadeSeconds : 0.05f);

            RevSoundHandle handle = PlayInternal(name, RevSoundKind.Bgm, volume, position: null, target: null,
                                                is3D: false, loop: loop, out int slot);
            _bgmSlot = handle.IsEmpty ? -1 : slot;

            if (!handle.IsEmpty && fadeSeconds > 0f)
            {
                ref RevSoundSlot state = ref _table.Slot(slot);
                state.FadeTarget = FinalVolume(state.Kind, state.Volume);
                state.FadeSpeed = 1f / fadeSeconds;
                if (_sources[slot] != null) _sources[slot].volume = 0f;
            }

            return handle;
        }

        private void PlayNextBgmInList()
        {
            if (_bgmList == null || _bgmList.Count == 0) return;

            _bgmIndex++;
            if (_bgmIndex >= _bgmList.Count)
            {
                _bgmList = null;                     // 播完最后一首就收工（要循环就传单元素列表）
                return;
            }

            StartBgm(_bgmList[_bgmIndex], null, 0f, loop: false);    // 音量交给音效表 / 分类默认
        }

        // ============================================================
        // 停止 / 音量 / 预加载
        // ============================================================

        /// <summary>停止某个槽位（句柄入口；过期句柄一律空操作）</summary>
        void IRevSoundHandleOwner.StopSlot(int slot, int generation)
        {
            if (!_table.IsAlive(slot, generation)) return;
            Recycle(slot, finished: false);
        }

        internal void StopAll(float fadeSeconds)
        {
            _bgmList = null;

            for (int i = 0; i < SlotCapacity; i++)
            {
                if (!_table.Slot(i).Alive) continue;

                if (fadeSeconds > 0f) FadeOut(i, fadeSeconds);
                else Recycle(i, finished: false);
            }
        }

        internal float GetKindVolume(RevSoundKind kind) => _kindVolumes[(int)kind];

        internal void SetKindVolume(RevSoundKind kind, float volume)
        {
            int index = (int)kind;
            if (index < 0 || index >= _kindVolumes.Length) return;

            float next = Clamp01(volume);
            if (Mathf.Approximately(_kindVolumes[index], next)) return;

            _kindVolumes[index] = next;
            ApplyVolumes();
        }

        internal void Set3DRange(float minDistance, float maxDistance)
        {
            _minDistance = minDistance > 0f ? minDistance : 0.01f;
            _maxDistance = maxDistance > _minDistance ? maxDistance : _minDistance + 0.01f;

            for (int i = 0; i < SlotCapacity; i++)
            {
                AudioSource source = _sources[i];
                if (source == null) continue;

                source.minDistance = _minDistance;
                source.maxDistance = _maxDistance;
            }
        }

        internal void Preload(string[] names, bool bgm)
        {
            if (names == null) return;

            EnsureInitialized();
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i])) _assets.Preload(names[i], bgm);
            }
        }

        internal void Unload(string name)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            // 先卸缓存，再让等待此名称的槽明确失败；失败监听者重入请求时会取得新 Entry。
            _assets.Unload(name);
            var pending = new List<int>();
            for (int i = 0; i < SlotCapacity; i++)
            {
                ref RevSoundSlot slot = ref _table.Slot(i);
                if (slot.Alive && slot.Pending && slot.Name == name) { pending.Add(i); }
            }
            // 先收完旧槽再通知业务，防止失败回调中新播放的声音被同一轮卸载误伤。
            foreach (int index in pending) { Recycle(index, finished: false); }
            foreach (int index in pending) { RaiseFailed(name, RevSoundErrorReason.LoadFailed); }
        }

        internal void UnloadAll()
        {
            StopAll(0f);
            _assets.UnloadAll();
        }

        // ============================================================
        // 音效表（逻辑名 → 真实路径 + 默认参数）
        // ============================================================

        /// <summary>登记一条音效（同名 = 覆盖）。名字/路径为空返回 false。</summary>
        internal bool Register(string name, string path, RevSoundKind kind, float volume, bool loop)
            => _catalog.Register(name, path, kind, volume, loop);

        /// <summary>注销一条音效（没登记过返回 false）</summary>
        internal bool Unregister(string name) => _catalog.Unregister(name);

        /// <summary>这个名字登记过吗</summary>
        internal bool IsRegistered(string name) => _catalog.IsRegistered(name);

        /// <summary>清空音效表</summary>
        internal void ClearCatalog() => _catalog.Clear();

        /// <summary>预加载音效表里的所有音效（每条按自己的分类选根目录）</summary>
        internal void PreloadAll()
        {
            if (_catalog.Count == 0) return;

            EnsureInitialized();

            foreach (KeyValuePair<string, RevSoundCatalog.Entry> kv in _catalog.Entries)
                _assets.Preload(kv.Value.Path, kv.Value.Kind == RevSoundKind.Bgm);
        }

        // ============================================================
        // 每帧推进
        // ============================================================

        /// <summary>
        /// 业务手动驱动（在 Update 里调它）：内核从此不再接受自动驱动，避免同一帧推进两次。
        /// </summary>
        public void TickManual(float deltaTime)
        {
            ManualDriven = true;
            Tick(deltaTime);
        }

        /// <summary>
        /// 每帧推进（**宿主驱动入口**；只遍历活跃槽位，稳态零分配）。
        /// ★ 这里不设 ManualDriven —— 否则宿主第一次调用后就永久让位、整个音效系统只推进一帧。
        ///   业务手动驱动请走 <see cref="TickManual"/>。
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime < 0f) deltaTime = 0f;

            _table.AdvanceFrame();

            for (int i = 0; i < SlotCapacity; i++)
            {
                ref RevSoundSlot state = ref _table.Slot(i);
                if (!state.Alive) continue;

                // ① 等资源：到了就开播，失败就报出去并回收
                if (state.Pending)
                {
                    bool isBgm = state.Kind == RevSoundKind.Bgm;
                    // PlayOn 在等待音频加载期间，目标物体可能被销毁。此时不要把它当成 PlayAt 使用默认坐标 (0,0,0)，
                    // 否则声音会突然从世界原点响起；释放等待槽位并报告 NoTarget，和“目标已销毁就停止”契约一致。
                    if (_requiresTarget[i] && _targets[i] == null)
                    {
                        string missingTargetName = state.Name;
                        Recycle(i, finished: false);
                        RaiseFailed(missingTargetName, RevSoundErrorReason.NoTarget);
                        continue;
                    }

                    AudioClip clip = _assets.Find(state.Name, isBgm);
                    if (clip != null)
                    {
                        state.Pending = false;
                        StartVoice(i, clip);
                    }
                    else if (_assets.IsFailed(state.Name, isBgm))
                    {
                        string failedName = state.Name;
                        Recycle(i, finished: false);
                        RaiseFailed(failedName, RevSoundErrorReason.LoadFailed);
                    }

                    continue;
                }

                AudioSource source = _sources[i];
                if (source == null)
                {
                    Recycle(i, finished: false);
                    continue;
                }

                // ② 3D 音效挂在目标物体下 → 位置由 Unity 的父子关系维护（零每帧开销）。
                //    目标被销毁时播放器会被一起销毁，上面那句 source == null 已经把它收掉了；
                //    目标只是被隐藏（activeInHierarchy == false）→ isPlaying 变 false，下面 ④ 当作播完回收。

                // ③ 淡入淡出
                if (state.FadeSpeed != 0f)
                {
                    float volume = source.volume + state.FadeSpeed * deltaTime;

                    if (state.FadeSpeed > 0f ? volume >= state.FadeTarget : volume <= state.FadeTarget)
                    {
                        volume = state.FadeTarget;
                        state.FadeSpeed = 0f;
                    }

                    source.volume = Clamp01(volume);

                    if (state.FadeSpeed == 0f && state.FadeStop && volume <= 0f)
                    {
                        Recycle(i, finished: false);
                        continue;
                    }
                }

                // ④ 播完了吗（一次性音效）
                if (!state.Loop && !source.isPlaying) Recycle(i, finished: true);
            }

            _assets.Tick();
        }

        // ============================================================
        // 内部
        // ============================================================

        private void Recycle(int slot, bool finished)
        {
            ref RevSoundSlot state = ref _table.Slot(slot);
            if (!state.Alive) return;

            // ★ 先取"当前代际"：Free 之后会 +1，用新值构造句柄会误指向下一条复用该槽位的声音
            int generation = state.Generation;
            string name = state.Name;
            RevSoundKind kind = state.Kind;

            AudioSource source = _sources[slot];
            if (source != null)
            {
                _assets.ReturnSource(source);
                _sources[slot] = null;
            }

            _targets[slot] = null;
            _requiresTarget[slot] = false;
            _is3D[slot] = false;
            _positions[slot] = Vector3.zero;
            if (_bgmSlot == slot) _bgmSlot = -1;

            _table.Free(slot);

            if (!finished) return;

            RaiseVoiceFinished(new RevSoundHandle(this, slot, generation));

            // 事件属于外部观察者；某个订阅者抛异常不能阻断内核推进，否则整张歌单会停在当前曲后面。
            if (kind == RevSoundKind.Bgm && _bgmList != null) PlayNextBgmInList();
        }

        private void FadeOut(int slot, float seconds)
        {
            ref RevSoundSlot state = ref _table.Slot(slot);
            if (!state.Alive) return;

            if (seconds <= 0f)
            {
                Recycle(slot, finished: false);
                return;
            }

            AudioSource source = _sources[slot];
            float from = source != null ? source.volume : FinalVolume(state.Kind, state.Volume);
            if (from <= 0f) { Recycle(slot, finished: false); return; }

            state.FadeTarget = 0f;
            state.FadeSpeed = -from / seconds;
            state.FadeStop = true;
        }

        private void ApplyVolumes()
        {
            for (int i = 0; i < SlotCapacity; i++)
            {
                ref RevSoundSlot state = ref _table.Slot(i);
                if (!state.Alive || state.Pending || state.FadeSpeed != 0f) continue;

                AudioSource source = _sources[i];
                if (source != null) source.volume = FinalVolume(state.Kind, state.Volume);
            }
        }

        private float FinalVolume(RevSoundKind kind, float volume)
        {
            if (_mute) return 0f;

            float kindVolume = _kindVolumes[(int)kind];
            return Clamp01(volume * kindVolume * _master);
        }

        private void RaiseFailed(string name, RevSoundErrorReason reason)
        {
            // Failed 是业务订阅事件；一个页面的回调写错抛异常，不应再阻止其他订阅者收到失败原因，也不能把异常带回播放内核。
            Action<string, RevSoundErrorReason> handlers = Failed;
            if (handlers == null) return;
            foreach (Action<string, RevSoundErrorReason> handler in handlers.GetInvocationList())
            {
                try { handler(name, reason); }
                catch (Exception e) { RevLog.Exception(e, "RevSound.Failed 订阅者异常", "Sound"); }
            }
        }

        private void RaiseVoiceFinished(RevSoundHandle handle)
        {
            // 结束事件发生在声音槽位回收后；逐个保护订阅者，避免一个异常跳过其他监听，并保证调用方返回后歌单仍能继续下一首。
            Action<RevSoundHandle> handlers = VoiceFinished;
            if (handlers == null) return;
            foreach (Action<RevSoundHandle> handler in handlers.GetInvocationList())
            {
                try { handler(handle); }
                catch (Exception e) { RevLog.Exception(e, "RevSound.VoiceFinished 订阅者异常", "Sound"); }
            }
        }

        private static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

        /// <summary>进入新的 Play 会话时清除上一局的运行状态与 Unity 对象引用。</summary>
        internal void ResetForNewSession()
        {
            // Unity 关闭 Domain Reload 时不会重建这个静态 Core；上一次 Play 的槽位、AudioSource、Transform 和事件订阅会继续留在内存里。
            // 新一局若沿用它们，可能操作已经销毁的 Unity 对象，或把上一局的播放回调发给旧 UI。因此先停掉并归还所有声音、卸载资源，
            // 再清空目录/目标等引用和事件订阅，让下一次播放从干净状态重新初始化。
            _bgmList = null;
            StopAll(0f);
            _assets.Dispose();
            _table.Clear();

            for (int i = 0; i < SlotCapacity; i++)
            {
                _sources[i] = null;
                _targets[i] = null;
                _requiresTarget[i] = false;
                _is3D[i] = false;
                _positions[i] = Vector3.zero;
            }

            // ★ 声音槽位有 64 个，音量分类只有 4 个，两者不是同一种编号。
            //   按槽位数清分类数组会在第 5 次写入时越界，使首次进入 Play 就中断复位。
            for (int i = 0; i < _kindVolumes.Length; i++)
            {
                _kindVolumes[i] = 1f;
            }

            _catalog.Clear();
            _bgmSlot = -1;
            _bgmIndex = 0;
            _initialized = false;
            ManualDriven = false;
            Enabled = true;
            _mute = false;
            _master = 1f;
            _minDistance = 1f;
            _maxDistance = 50f;
            Failed = null;
            VoiceFinished = null;
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;

            _initialized = true;
            _assets.Initialize(CreateRoot());
            RevSoundDriver.EnsureDefault(this);
        }

        private static Transform CreateRoot()
        {
            GameObject root = GameObject.Find("RevSoundRoot");
            if (root != null) return root.transform;

            root = new GameObject("RevSoundRoot");
            root.hideFlags = HideFlags.HideAndDontSave;      // 不进场景文件
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(root);

            return root.transform;
        }

        /// <summary>调试显示：例如 <c>RevSoundCore(活跃 3/24，BGM=login，表 12 条)</c></summary>
        public override string ToString()
        {
            string bgm = _bgmSlot >= 0 && _table.Slot(_bgmSlot).Alive ? _table.Slot(_bgmSlot).Name : "（无）";
            return $"RevSoundCore(活跃 {_table.AliveCount}/{_maxVoices}，BGM={bgm}，表 {_catalog.Count} 条)";
        }
    }
}
